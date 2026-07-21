using System.Data.Common;
using System.Text;
using Qplus.Core.Models;

namespace Qplus.Core.Admin;

/// <summary>
/// SQL Server user administration. There are no tablespaces/quotas; "roles" are database
/// roles and "system privileges" are database-level permissions.
/// </summary>
public sealed class SqlServerUserAdmin : IUserAdmin
{
    public DbEngineKind Engine => DbEngineKind.SqlServer;
    public bool SupportsTablespaces => false;

    /// <summary>Database-level permissions offered in the privileges tab.</summary>
    private static readonly string[] CommonPermissions =
    {
        "CONNECT", "CREATE TABLE", "CREATE VIEW", "CREATE PROCEDURE", "CREATE FUNCTION",
        "CREATE SCHEMA", "ALTER ANY SCHEMA", "ALTER ANY USER", "ALTER ANY ROLE",
        "SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE",
        "VIEW DEFINITION", "SHOWPLAN", "BACKUP DATABASE",
    };

    public async Task<UserDetails?> GetUserAsync(DbConnection open, string user, CancellationToken ct)
    {
        const string sql = @"
SELECT dp.name, dp.type_desc, dp.default_schema_name,
       CAST(CASE WHEN sp.is_disabled = 1 THEN 1 ELSE 0 END AS INT) AS is_disabled
FROM sys.database_principals dp
LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid
WHERE dp.name = @u;";

        await using var cmd = Cmd(open, sql, ("@u", user));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var typeDesc = r.IsDBNull(1) ? "" : r.GetString(1);
        return new UserDetails
        {
            Name = r.GetString(0),
            DefaultTablespace = r.IsDBNull(2) ? "" : r.GetString(2), // reused as "default schema"
            AccountStatus = typeDesc,
            IsExternalAuth = typeDesc.Contains("WINDOWS", StringComparison.OrdinalIgnoreCase),
            IsLocked = !r.IsDBNull(3) && r.GetInt32(3) == 1,
        };
    }

    public Task<IReadOnlyList<string>> ListTablespacesAsync(DbConnection open, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public async Task<IReadOnlyList<RoleGrant>> GetRolesAsync(DbConnection open, string user, CancellationToken ct)
    {
        const string allSql = "SELECT name FROM sys.database_principals WHERE type = 'R' ORDER BY name;";
        const string grantedSql = @"
SELECT r.name
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
WHERE u.name = @u;";

        var all = await ReadStringsAsync(open, allSql, ct);
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = Cmd(open, grantedSql, ("@u", user)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) granted.Add(r.GetString(0));
        }

        return all.Select(role => new RoleGrant
        {
            Role = role,
            Granted = granted.Contains(role),
            WasGranted = granted.Contains(role),
        }).ToList();
    }

    public async Task<IReadOnlyList<PrivilegeGrant>> GetPrivilegesAsync(DbConnection open, string user, CancellationToken ct)
    {
        const string sql = @"
SELECT p.permission_name, p.state_desc
FROM sys.database_permissions p
JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id
WHERE pr.name = @u AND p.class = 0;";   // class 0 = database-level

        var granted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = Cmd(open, sql, ("@u", user)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var state = r.IsDBNull(1) ? "" : r.GetString(1);
                granted[r.GetString(0)] = state.Equals("GRANT_WITH_GRANT_OPTION", StringComparison.OrdinalIgnoreCase);
            }
        }

        var names = CommonPermissions.ToList();
        foreach (var g in granted.Keys)
            if (!names.Contains(g, StringComparer.OrdinalIgnoreCase)) names.Add(g);

        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var has = granted.TryGetValue(p, out var withGrant);
            return new PrivilegeGrant
            {
                Privilege = p,
                Granted = has,
                WasGranted = has,
                AdminOption = has && withGrant,
                WasAdminOption = has && withGrant,
            };
        }).ToList();
    }

    public Task<IReadOnlyList<TablespaceQuota>> GetQuotasAsync(DbConnection open, string user, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TablespaceQuota>>(Array.Empty<TablespaceQuota>());

    public string BuildAlterScript(UserEditModel m)
    {
        var name = Quote(m.Details.Name);
        var sb = new StringBuilder();

        if (m.IsNew)
        {
            if (m.ExternalAuth)
            {
                sb.AppendLine($"CREATE LOGIN {name} FROM WINDOWS;");
            }
            else
            {
                sb.Append($"CREATE LOGIN {name} WITH PASSWORD = {Literal(m.NewPassword)}");
                if (m.ExpirePassword) sb.Append(" MUST_CHANGE, CHECK_EXPIRATION = ON");
                sb.AppendLine(";");
            }
            sb.AppendLine("GO");
            sb.AppendLine($"CREATE USER {name} FOR LOGIN {name};");
        }
        else
        {
            if (!string.IsNullOrEmpty(m.NewPassword))
            {
                sb.Append($"ALTER LOGIN {name} WITH PASSWORD = {Literal(m.NewPassword)}");
                if (m.ExpirePassword) sb.Append(" MUST_CHANGE");
                sb.AppendLine(";");
            }

            if (m.Locked != m.Details.IsLocked)
                sb.AppendLine($"ALTER LOGIN {name} {(m.Locked ? "DISABLE" : "ENABLE")};");

            if (!string.IsNullOrWhiteSpace(m.DefaultTablespace)
                && !m.DefaultTablespace.Equals(m.Details.DefaultTablespace, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"ALTER USER {name} WITH DEFAULT_SCHEMA = {Quote(m.DefaultTablespace)};");
        }

        foreach (var r in m.Roles)
        {
            if (r.Granted && !r.WasGranted)
                sb.AppendLine($"ALTER ROLE {Quote(r.Role)} ADD MEMBER {name};");
            else if (!r.Granted && r.WasGranted)
                sb.AppendLine($"ALTER ROLE {Quote(r.Role)} DROP MEMBER {name};");
        }

        foreach (var p in m.Privileges)
        {
            if (p.Granted && !p.WasGranted)
                sb.AppendLine($"GRANT {p.Privilege} TO {name}{(p.AdminOption ? " WITH GRANT OPTION" : "")};");
            else if (!p.Granted && p.WasGranted)
                sb.AppendLine($"REVOKE {p.Privilege} FROM {name};");
        }

        return sb.ToString().TrimEnd();
    }

    // ---- helpers ---------------------------------------------------------

    private static string Quote(string id) => "[" + id.Replace("]", "]]") + "]";
    private static string Literal(string s) => "'" + s.Replace("'", "''") + "'";

    private static DbCommand Cmd(DbConnection open, string sql, params (string name, object value)[] ps)
    {
        var cmd = open.CreateCommand();
        cmd.CommandText = sql;
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
