using System.Data.Common;
using System.Text;
using Qplus.Core.Models;

namespace Qplus.Core.Admin;

/// <summary>
/// Oracle user administration. Prefers DBA_* views (needs DBA privileges); falls back to
/// the ALL_*/USER_* equivalents so the dialog still works for a non-DBA login.
/// </summary>
public sealed class OracleUserAdmin : IUserAdmin
{
    public DbEngineKind Engine => DbEngineKind.Oracle;
    public bool SupportsTablespaces => true;

    public async Task<UserDetails?> GetUserAsync(DbConnection open, string user, CancellationToken ct)
    {
        const string dbaSql = @"
SELECT username, default_tablespace, temporary_tablespace, account_status,
       profile, authentication_type
FROM dba_users WHERE username = :u";
        const string fallbackSql = @"
SELECT username, NULL, NULL, NULL, NULL, NULL FROM all_users WHERE username = :u";

        foreach (var sql in new[] { dbaSql, fallbackSql })
        {
            try
            {
                await using var cmd = Cmd(open, sql, ("u", user.ToUpperInvariant()));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return null;

                var status = r.IsDBNull(3) ? "" : r.GetString(3);
                return new UserDetails
                {
                    Name = r.GetString(0),
                    DefaultTablespace = r.IsDBNull(1) ? "" : r.GetString(1),
                    TemporaryTablespace = r.IsDBNull(2) ? "" : r.GetString(2),
                    AccountStatus = status,
                    Profile = r.IsDBNull(4) ? "" : r.GetString(4),
                    IsLocked = status.Contains("LOCKED", StringComparison.OrdinalIgnoreCase),
                    IsPasswordExpired = status.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase),
                    IsExternalAuth = !r.IsDBNull(5)
                                     && r.GetString(5).Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase),
                };
            }
            catch
            {
                // try the next statement
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> ListTablespacesAsync(DbConnection open, CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "SELECT tablespace_name FROM dba_tablespaces ORDER BY 1",
                     "SELECT tablespace_name FROM user_tablespaces ORDER BY 1",
                 })
        {
            try { return await ReadStringsAsync(open, sql, ct); }
            catch { /* try next */ }
        }
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<RoleGrant>> GetRolesAsync(DbConnection open, string user, CancellationToken ct)
    {
        var u = user.ToUpperInvariant();

        var all = new List<string>();
        foreach (var sql in new[]
                 {
                     "SELECT role FROM dba_roles ORDER BY 1",
                     "SELECT DISTINCT granted_role FROM user_role_privs ORDER BY 1",
                 })
        {
            try { all = (await ReadStringsAsync(open, sql, ct)).ToList(); if (all.Count > 0) break; }
            catch { /* try next */ }
        }

        var granted = new Dictionary<string, (bool admin, bool def)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sql in new[]
                 {
                     "SELECT granted_role, admin_option, default_role FROM dba_role_privs WHERE grantee = :u",
                     "SELECT granted_role, admin_option, default_role FROM user_role_privs WHERE username = :u",
                 })
        {
            try
            {
                await using var cmd = Cmd(open, sql, ("u", u));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    granted[r.GetString(0)] = (
                        !r.IsDBNull(1) && r.GetString(1).Equals("YES", StringComparison.OrdinalIgnoreCase),
                        !r.IsDBNull(2) && r.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase));
                }
                break;
            }
            catch { /* try next */ }
        }

        // Make sure roles the user holds are listed even if we couldn't read the full catalogue.
        foreach (var g in granted.Keys) if (!all.Contains(g, StringComparer.OrdinalIgnoreCase)) all.Add(g);

        return all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(role =>
        {
            var has = granted.TryGetValue(role, out var flags);
            return new RoleGrant
            {
                Role = role,
                Granted = has,
                WasGranted = has,
                AdminOption = has && flags.admin,
                WasAdminOption = has && flags.admin,
                DefaultRole = has && flags.def,
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<PrivilegeGrant>> GetPrivilegesAsync(DbConnection open, string user, CancellationToken ct)
    {
        var u = user.ToUpperInvariant();

        var all = new List<string>();
        foreach (var sql in new[]
                 {
                     "SELECT name FROM system_privilege_map WHERE property = 0 ORDER BY name",
                     "SELECT DISTINCT privilege FROM user_sys_privs ORDER BY 1",
                 })
        {
            try { all = (await ReadStringsAsync(open, sql, ct)).ToList(); if (all.Count > 0) break; }
            catch { /* try next */ }
        }

        var granted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var sql in new[]
                 {
                     "SELECT privilege, admin_option FROM dba_sys_privs WHERE grantee = :u",
                     "SELECT privilege, admin_option FROM user_sys_privs WHERE username = :u",
                 })
        {
            try
            {
                await using var cmd = Cmd(open, sql, ("u", u));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    granted[r.GetString(0)] = !r.IsDBNull(1)
                        && r.GetString(1).Equals("YES", StringComparison.OrdinalIgnoreCase);
                break;
            }
            catch { /* try next */ }
        }

        foreach (var g in granted.Keys) if (!all.Contains(g, StringComparer.OrdinalIgnoreCase)) all.Add(g);

        return all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var has = granted.TryGetValue(p, out var admin);
            return new PrivilegeGrant
            {
                Privilege = p,
                Granted = has,
                WasGranted = has,
                AdminOption = has && admin,
                WasAdminOption = has && admin,
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<TablespaceQuota>> GetQuotasAsync(DbConnection open, string user, CancellationToken ct)
    {
        var u = user.ToUpperInvariant();
        foreach (var sql in new[]
                 {
                     "SELECT tablespace_name, bytes, max_bytes FROM dba_ts_quotas WHERE username = :u",
                     "SELECT tablespace_name, bytes, max_bytes FROM user_ts_quotas",
                 })
        {
            try
            {
                var list = new List<TablespaceQuota>();
                await using var cmd = sql.Contains(":u") ? Cmd(open, sql, ("u", u)) : Cmd(open, sql);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var max = r.IsDBNull(2) ? 0L : Convert.ToInt64(r.GetValue(2));
                    list.Add(new TablespaceQuota
                    {
                        Tablespace = r.GetString(0),
                        UsedBytes = r.IsDBNull(1) ? 0L : Convert.ToInt64(r.GetValue(1)),
                        MaxBytes = max,
                        WasMaxBytes = max,
                    });
                }
                return list;
            }
            catch { /* try next */ }
        }
        return Array.Empty<TablespaceQuota>();
    }

    public string BuildAlterScript(UserEditModel m)
    {
        var name = Quote(m.Details.Name);
        var sb = new StringBuilder();

        if (m.IsNew)
        {
            var identified = m.ExternalAuth
                ? "IDENTIFIED EXTERNALLY"
                : $"IDENTIFIED BY {Quote(m.NewPassword)}";
            sb.Append($"CREATE USER {name} {identified}");
            if (!string.IsNullOrWhiteSpace(m.DefaultTablespace))
                sb.Append($"\n  DEFAULT TABLESPACE {Quote(m.DefaultTablespace)}");
            if (!string.IsNullOrWhiteSpace(m.TemporaryTablespace))
                sb.Append($"\n  TEMPORARY TABLESPACE {Quote(m.TemporaryTablespace)}");
            if (m.Locked) sb.Append("\n  ACCOUNT LOCK");
            if (m.ExpirePassword) sb.Append("\n  PASSWORD EXPIRE");
            sb.AppendLine(";");
        }
        else
        {
            // Identification
            if (m.ExternalAuth && !m.Details.IsExternalAuth)
                sb.AppendLine($"ALTER USER {name} IDENTIFIED EXTERNALLY;");
            else if (!string.IsNullOrEmpty(m.NewPassword))
                sb.AppendLine($"ALTER USER {name} IDENTIFIED BY {Quote(m.NewPassword)};");

            // Tablespaces
            if (!string.IsNullOrWhiteSpace(m.DefaultTablespace)
                && !m.DefaultTablespace.Equals(m.Details.DefaultTablespace, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"ALTER USER {name} DEFAULT TABLESPACE {Quote(m.DefaultTablespace)};");

            if (!string.IsNullOrWhiteSpace(m.TemporaryTablespace)
                && !m.TemporaryTablespace.Equals(m.Details.TemporaryTablespace, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"ALTER USER {name} TEMPORARY TABLESPACE {Quote(m.TemporaryTablespace)};");

            // Lock / unlock
            if (m.Locked != m.Details.IsLocked)
                sb.AppendLine($"ALTER USER {name} ACCOUNT {(m.Locked ? "LOCK" : "UNLOCK")};");

            // Expire (one-way: you can't un-expire, only set a new password)
            if (m.ExpirePassword && !m.Details.IsPasswordExpired)
                sb.AppendLine($"ALTER USER {name} PASSWORD EXPIRE;");

            if (m.EditionsEnabled && !m.Details.EditionsEnabled)
                sb.AppendLine($"ALTER USER {name} ENABLE EDITIONS;");
        }

        // Quotas
        foreach (var q in m.Quotas.Where(q => q.MaxBytes != q.WasMaxBytes))
        {
            var amount = q.MaxBytes < 0 ? "UNLIMITED" : q.MaxBytes.ToString();
            sb.AppendLine($"ALTER USER {name} QUOTA {amount} ON {Quote(q.Tablespace)};");
        }

        // Roles
        foreach (var r in m.Roles)
        {
            if (r.Granted && !r.WasGranted)
                sb.AppendLine($"GRANT {Quote(r.Role)} TO {name}{(r.AdminOption ? " WITH ADMIN OPTION" : "")};");
            else if (!r.Granted && r.WasGranted)
                sb.AppendLine($"REVOKE {Quote(r.Role)} FROM {name};");
            else if (r.Granted && r.AdminOption != r.WasAdminOption && r.AdminOption)
                sb.AppendLine($"GRANT {Quote(r.Role)} TO {name} WITH ADMIN OPTION;");
        }

        // System privileges
        foreach (var p in m.Privileges)
        {
            if (p.Granted && !p.WasGranted)
                sb.AppendLine($"GRANT {p.Privilege} TO {name}{(p.AdminOption ? " WITH ADMIN OPTION" : "")};");
            else if (!p.Granted && p.WasGranted)
                sb.AppendLine($"REVOKE {p.Privilege} FROM {name};");
            else if (p.Granted && p.AdminOption != p.WasAdminOption && p.AdminOption)
                sb.AppendLine($"GRANT {p.Privilege} TO {name} WITH ADMIN OPTION;");
        }

        return sb.ToString().TrimEnd();
    }

    // ---- helpers ---------------------------------------------------------

    private static string Quote(string id) => "\"" + id.Replace("\"", "") + "\"";

    private static DbCommand Cmd(DbConnection open, string sql, params (string name, object value)[] ps)
    {
        var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        // Bind by name so repeated placeholders work.
        var bindByName = cmd.GetType().GetProperty("BindByName");
        bindByName?.SetValue(cmd, true);
        foreach (var (name, value) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task<List<string>> ReadStringsAsync(DbConnection open, string sql, CancellationToken ct)
    {
        var list = new List<string>();
        await using var cmd = Cmd(open, sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        return list;
    }
}
