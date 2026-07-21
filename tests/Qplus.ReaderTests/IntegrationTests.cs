using Qplus.Core.Admin;
using Qplus.Core.Completion;
using Qplus.Core.Data;
using Qplus.Core.Models;
using Qplus.Core.Security;

namespace Qplus.ReaderTests;

/// <summary>
/// End-to-end tests against real databases (SQL Server and Oracle), exercising the paths
/// that can't be covered in-memory: message capture, multi-result-set batches, table-detail
/// metadata queries, DDL generation and editable loads.
///
/// Credentials come from environment variables so nothing sensitive lives in source control:
///   EDMUSER / EDMPASSWD  — shared credentials
///   EDMDSN               — SQL Server database   (default CRNL, host localhost)
///   EDMORASERVICE        — Oracle service name   (default BPX, host 127.0.0.1:1521)
/// Skips cleanly when unset. Everything here is read-only.
/// </summary>
public static class IntegrationTests
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    private static void Skip(string name, string why) => Console.WriteLine($"SKIP  {name} — {why}");

    public static async Task<int> RunAsync()
    {
        _failures = 0;

        var user = Environment.GetEnvironmentVariable("EDMUSER");
        var password = Environment.GetEnvironmentVariable("EDMPASSWD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine();
            Skip("integration", "EDMUSER/EDMPASSWD not set");
            return 0;
        }

        var encrypted = SecretProtector.Protect(password);

        // ---- SQL Server ----
        var sqlServer = new ConnectionInfo
        {
            Name = "EDM SQL Server",
            Engine = DbEngineKind.SqlServer,
            Host = Environment.GetEnvironmentVariable("EDMHOST") ?? "localhost",
            Database = Environment.GetEnvironmentVariable("EDMDSN") ?? "CRNL",
            Username = user,
            EncryptedPassword = encrypted,
            TrustServerCertificate = true,
        };
        await RunSqlServerSpecificAsync(sqlServer);
        await RunCommonAsync(sqlServer, "SQL Server");

        // ---- Oracle ----
        var oracle = new ConnectionInfo
        {
            Name = "EDM Oracle",
            Engine = DbEngineKind.Oracle,
            Host = Environment.GetEnvironmentVariable("EDMORAHOST") ?? "127.0.0.1",
            Port = int.TryParse(Environment.GetEnvironmentVariable("EDMORAPORT"), out var p) ? p : 1521,
            Database = Environment.GetEnvironmentVariable("EDMORASERVICE") ?? "BPX",
            Username = user,
            EncryptedPassword = encrypted,
        };
        await RunCommonAsync(oracle, "Oracle");

        return _failures;
    }

    // ================= SQL Server only =================

    private static async Task RunSqlServerSpecificAsync(ConnectionInfo conn)
    {
        Console.WriteLine();
        Console.WriteLine("--- integration: SQL Server (T-SQL specifics) ---");
        var ct = CancellationToken.None;

        var (ok, msg) = await QueryRunner.TestAsync(conn, ct);
        Check("connect", ok, msg);
        if (!ok) { Console.WriteLine("      (T-SQL specifics skipped)"); return; }

        var r = await QueryRunner.ExecuteAsync(conn, "PRINT 'hello from qplus';", ct);
        Check("PRINT captured as a message",
            r.Messages.Any(m => m.Contains("hello from qplus", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", r.Messages));

        r = await QueryRunner.ExecuteAsync(conn, @"
SET NOCOUNT ON;
DECLARE @i INT = 1;
WHILE @i <= 3
BEGIN
    PRINT 'line ' + CAST(@i AS VARCHAR(3));
    SET @i += 1;
END", ct);
        Check("multiple PRINTs captured in order",
            r.Messages.Count(m => m.StartsWith("line ")) == 3
            && r.Messages.First(m => m.StartsWith("line ")) == "line 1",
            string.Join(" | ", r.Messages));

        r = await QueryRunner.ExecuteAsync(conn, "SELECT 1 AS a; SELECT 2 AS b; SELECT 3 AS c;", ct);
        Check("three result sets returned", r.Grids.Count == 3, $"got {r.Grids.Count}");

        r = await QueryRunner.ExecuteAsync(conn, @"
DECLARE @str VARCHAR(250);
SET @str = 'exec something';
SELECT @str;
PRINT 'after select';", ct);
        Check("SELECT @var + PRINT: no error", !r.HasError, r.ErrorText ?? "");
        Check("unnamed column got a header",
            r.Grids.Count == 1 && !string.IsNullOrWhiteSpace(r.Grids[0].Columns[0].ColumnName),
            r.Grids.Count == 1 ? r.Grids[0].Columns[0].ColumnName : "n/a");
        Check("PRINT after a grid still captured",
            r.Messages.Any(m => m.Contains("after select", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", r.Messages));
    }

    // ================= Engine-agnostic suite =================

    private static async Task RunCommonAsync(ConnectionInfo conn, string label)
    {
        Console.WriteLine();
        Console.WriteLine($"--- integration: {label} ---");
        var ct = CancellationToken.None;
        var engine = DbEngines.For(conn);

        var (ok, msg) = await QueryRunner.TestAsync(conn, ct);
        Check($"[{label}] connect", ok, msg);
        if (!ok)
        {
            Console.WriteLine($"      ({label} tests skipped — database unreachable)");
            return;
        }

        // Trivial round trip
        var probe = conn.Engine == DbEngineKind.Oracle ? "SELECT 1 AS n FROM dual" : "SELECT 1 AS n";
        var r = await QueryRunner.ExecuteAsync(conn, probe, ct);
        Check($"[{label}] simple select", !r.HasError && r.Grids.Count == 1, r.ErrorText ?? "");

        // Completion metadata
        var cache = new SchemaCache();
        var objects = await cache.GetObjectsAsync(conn);
        Check($"[{label}] object list loaded", objects.Count > 0, $"{objects.Count} tables/views");
        if (objects.Count == 0) return;

        if (conn.Engine == DbEngineKind.Oracle)
        {
            // Dropped tables linger in the recycle bin as BIN$… and must not pollute
            // completion or the object explorer.
            var binInDb = await ScalarLongAsync(conn,
                "SELECT COUNT(*) FROM all_tables WHERE table_name LIKE 'BIN$%'", ct);
            var binInList = objects.Count(o => o.Name.StartsWith("BIN$", StringComparison.Ordinal));
            Check($"[{label}] recycle-bin objects excluded from object list", binInList == 0,
                $"{binInList} in list; {binInDb} exist in all_tables");
        }

        // Prefer a table owned by the login, so privileges aren't an issue.
        var owner = conn.Username.ToUpperInvariant();
        var target = objects.FirstOrDefault(o => !o.IsView && o.Schema.ToUpperInvariant() == owner)
                     ?? objects.First(o => !o.IsView);
        Console.WriteLine($"      (target table: {target.Qualified})");

        var cols = await cache.GetColumnsAsync(conn, target.Schema, target.Name);
        Check($"[{label}] columns for {target.Qualified}", cols.Count > 0, $"{cols.Count} columns");

        // Analyzer against a real object
        var sql = $"SELECT * FROM {target.Name} WHERE ";
        var ctx = SqlContextAnalyzer.Analyze(sql, sql.Length);
        Check($"[{label}] analyzer: WHERE -> Columns",
            ctx.Kind == SqlCompletionKind.Columns
            && ctx.Tables.Count == 1
            && string.Equals(ctx.Tables[0].Name, target.Name, StringComparison.OrdinalIgnoreCase),
            ctx.Kind.ToString());

        // SELECT TOP / FETCH FIRST must be valid syntax for this engine
        var topSql = engine.BuildSelectTopSql(target.Schema, target.Name, 5);
        r = await QueryRunner.ExecuteAsync(conn, topSql, ct);
        Check($"[{label}] top-N select runs", !r.HasError, r.ErrorText ?? topSql);

        // Every table-details pane
        await using (var open = engine.CreateConnection(engine.BuildConnectionString(conn)))
        {
            await open.OpenAsync(ct);

            foreach (TableDetailKind kind in Enum.GetValues<TableDetailKind>())
            {
                try
                {
                    await using var cmd = engine.CreateTableDetailCommand(open, kind, target.Schema, target.Name);
                    await using var rr = await cmd.ExecuteReaderAsync(ct);
                    var res = new QueryExecutionResult();
                    await QueryRunner.CollectResultsAsync(rr, res, ct);
                    var c = res.Grids.Count > 0 ? res.Grids[0].Columns.Count : 0;
                    var n = res.Grids.Count > 0 ? res.Grids[0].Rows.Count : 0;
                    Check($"[{label}] detail pane: {kind}", res.Grids.Count == 1 && c > 0, $"{c} cols, {n} rows");
                }
                catch (Exception ex)
                {
                    Check($"[{label}] detail pane: {kind}", false, ex.Message);
                }
            }

            try
            {
                var ddl = await engine.BuildTableDdlAsync(open, target.Schema, target.Name, ct);
                Check($"[{label}] DDL generated",
                    ddl.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase),
                    ddl.Trim().Split('\n')[0].Trim());
            }
            catch (Exception ex)
            {
                Check($"[{label}] DDL generated", false, ex.Message);
            }
        }

        // Editable load (read-only: never save against the live DB)
        var (data, loadErr) = await TableDataEditor.LoadAsync(conn, target.Schema, target.Name, 25, ct);
        Check($"[{label}] editable load", loadErr is null && data.Columns.Count > 0,
            loadErr ?? $"{data.Columns.Count} cols, {data.Rows.Count} rows, editable={TableDataEditor.IsEditable(data)}");

        var noop = await TableDataEditor.SaveAsync(conn, target.Schema, target.Name, 25, data, ct);
        Check($"[{label}] save with no changes is a no-op", noop.RowsAffected == 0, noop.Message);

        // Errors surface rather than throw. Use a legal-but-missing identifier so we test
        // "object not found" rather than "invalid identifier" (Oracle rejects a leading underscore).
        r = await QueryRunner.ExecuteAsync(conn, "SELECT * FROM NO_SUCH_TABLE_QPLUS", ct);
        Check($"[{label}] missing table reports an error", r.HasError && !string.IsNullOrEmpty(r.ErrorText),
            r.ErrorText ?? "");

        // A table with an actual primary key, so the metadata joins are proven to return data
        // (an empty pane alone can't distinguish "works" from "matches nothing").
        var keyed = await FindTableWithPrimaryKeyAsync(conn, ct);
        if (keyed is null)
        {
            Skip($"[{label}] populated detail panes", "no table with a primary key found");
            return;
        }

        Console.WriteLine($"      (keyed table: {keyed.Value.schema}.{keyed.Value.table})");
        await using (var open2 = engine.CreateConnection(engine.BuildConnectionString(conn)))
        {
            await open2.OpenAsync(ct);
            foreach (var kind in new[] { TableDetailKind.Constraints, TableDetailKind.Indexes })
            {
                await using var cmd = engine.CreateTableDetailCommand(open2, kind, keyed.Value.schema, keyed.Value.table);
                await using var rr = await cmd.ExecuteReaderAsync(ct);
                var res = new QueryExecutionResult();
                await QueryRunner.CollectResultsAsync(rr, res, ct);
                var n = res.Grids.Count > 0 ? res.Grids[0].Rows.Count : 0;
                Check($"[{label}] {kind} pane returns rows for a keyed table", n > 0, $"{n} rows");
            }
        }

        // The command builder must detect that key, i.e. the grid will be editable.
        var (keyedData, keyedErr) = await TableDataEditor.LoadAsync(conn, keyed.Value.schema, keyed.Value.table, 5, ct);
        Check($"[{label}] keyed table loads as editable",
            keyedErr is null && TableDataEditor.IsEditable(keyedData),
            keyedErr ?? $"editable={TableDataEditor.IsEditable(keyedData)}");

        await RunUserAdminAsync(conn, label, ct);
    }

    /// <summary>Read-only checks of the Edit User backend. Never applies a script.</summary>
    private static async Task RunUserAdminAsync(ConnectionInfo conn, string label, CancellationToken ct)
    {
        var admin = UserAdmins.For(conn);
        var engine = DbEngines.For(conn);
        var who = conn.Username;

        await using var open = engine.CreateConnection(engine.BuildConnectionString(conn));
        await open.OpenAsync(ct);

        var details = await admin.GetUserAsync(open, who, ct);
        Check($"[{label}] user details loaded", details is not null,
            details is null ? "null" : $"{details.Name} status='{details.AccountStatus}' " +
                                       $"default='{details.DefaultTablespace}' temp='{details.TemporaryTablespace}'");
        if (details is null) return;

        var roles = await admin.GetRolesAsync(open, who, ct);
        Check($"[{label}] roles listed", roles.Count > 0,
            $"{roles.Count} roles, {roles.Count(r => r.Granted)} granted");

        var privs = await admin.GetPrivilegesAsync(open, who, ct);
        Check($"[{label}] privileges listed", privs.Count > 0,
            $"{privs.Count} privileges, {privs.Count(p => p.Granted)} granted");

        if (admin.SupportsTablespaces)
        {
            var spaces = await admin.ListTablespacesAsync(open, ct);
            Check($"[{label}] tablespaces listed", spaces.Count > 0, string.Join(", ", spaces.Take(6)));

            var quotas = await admin.GetQuotasAsync(open, who, ct);
            Check($"[{label}] quotas query runs", true, $"{quotas.Count} quota row(s)");
        }

        // Script generation: no pending edits => empty script (must never emit stray DDL).
        var model = new UserEditModel
        {
            Details = details,
            DefaultTablespace = details.DefaultTablespace,
            TemporaryTablespace = details.TemporaryTablespace,
            Locked = details.IsLocked,
            ExternalAuth = details.IsExternalAuth,
            Roles = roles.ToList(),
            Privileges = privs.ToList(),
        };
        var empty = admin.BuildAlterScript(model);
        Check($"[{label}] no changes => empty script", empty.Length == 0,
            empty.Length == 0 ? "" : empty.Replace("\n", " ⏎ "));

        // A representative edit produces the expected statements (generated, not executed).
        model.NewPassword = "S0me#Temp";
        model.Locked = !details.IsLocked;
        var firstUngranted = model.Roles.FirstOrDefault(r => !r.Granted);
        if (firstUngranted is not null) firstUngranted.Granted = true;
        var firstUngrantedPriv = model.Privileges.FirstOrDefault(p => !p.Granted);
        if (firstUngrantedPriv is not null) firstUngrantedPriv.Granted = true;

        var script = admin.BuildAlterScript(model);
        Console.WriteLine("      generated script:");
        foreach (var line in script.Split('\n')) Console.WriteLine("        " + line.TrimEnd());

        var isOracle = conn.Engine == DbEngineKind.Oracle;

        Check($"[{label}] password change emitted",
            script.Contains(isOracle ? "IDENTIFIED BY" : "PASSWORD", StringComparison.OrdinalIgnoreCase));

        Check($"[{label}] lock toggle emitted",
            script.Contains(isOracle ? "ACCOUNT" : "ALTER LOGIN", StringComparison.OrdinalIgnoreCase));

        Check($"[{label}] role grant emitted",
            firstUngranted is null || script.Contains(firstUngranted.Role, StringComparison.OrdinalIgnoreCase),
            firstUngranted?.Role ?? "n/a");

        Check($"[{label}] privilege grant emitted",
            firstUngrantedPriv is null || script.Contains(firstUngrantedPriv.Privilege, StringComparison.OrdinalIgnoreCase),
            firstUngrantedPriv?.Privilege ?? "n/a");
    }

    private static async Task<long> ScalarLongAsync(ConnectionInfo conn, string sql, CancellationToken ct)
    {
        try
        {
            var engine = DbEngines.For(conn);
            await using var open = engine.CreateConnection(engine.BuildConnectionString(conn));
            await open.OpenAsync(ct);
            await using var cmd = open.CreateCommand();
            cmd.CommandText = sql;
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is null or DBNull ? -1 : Convert.ToInt64(v);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Finds a table owned by the login that has a primary key.</summary>
    private static async Task<(string schema, string table)?> FindTableWithPrimaryKeyAsync(
        ConnectionInfo conn, CancellationToken ct)
    {
        var engine = DbEngines.For(conn);
        var sql = conn.Engine == DbEngineKind.Oracle
            // Skip recycle-bin (BIN$…) objects — a dropped table's constraints survive but
            // its indexes don't, which would make this a misleading probe.
            ? @"SELECT owner, table_name FROM all_constraints
                WHERE constraint_type = 'P' AND owner = :owner
                  AND table_name NOT LIKE 'BIN$%' AND ROWNUM = 1"
            : @"SELECT TOP 1 s.name, o.name
                FROM sys.key_constraints kc
                JOIN sys.objects o ON o.object_id = kc.parent_object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE kc.type = 'PK';";

        try
        {
            await using var open = engine.CreateConnection(engine.BuildConnectionString(conn));
            await open.OpenAsync(ct);
            await using var cmd = open.CreateCommand();
            cmd.CommandText = sql;
            if (conn.Engine == DbEngineKind.Oracle)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "owner";
                p.Value = conn.Username.ToUpperInvariant();
                cmd.Parameters.Add(p);
            }
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) return (r.GetString(0), r.GetString(1));
        }
        catch
        {
            // fall through
        }
        return null;
    }
}
