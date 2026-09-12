using Qplus.Core.Data;
using Qplus.Core.Models;
using Qplus.Core.Security;

namespace Qplus.ReaderTests;

/// <summary>
/// Manual-commit query sessions against real databases. Everything runs on temporary tables —
/// #local ones private to the session's connection, and ##global ones only where a second
/// connection has to look in — so no real table is touched. Same environment variables as
/// <see cref="IntegrationTests"/>; skips cleanly when they're unset.
/// </summary>
public static class TransactionTests
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    private static void Skip(string name, string why) => Console.WriteLine($"SKIP  {name} — {why}");

    private static object? Scalar(QueryExecutionResult r) =>
        r.Grids.Count > 0 && r.Grids[0].Rows.Count > 0 ? r.Grids[0].Rows[0][0] : null;

    private static string Detail(QueryExecutionResult r) =>
        r.HasError ? r.ErrorText ?? "error" : $"{r.Grids.Count} grid(s), value {Scalar(r) ?? "none"}";

    public static async Task<int> RunAsync()
    {
        _failures = 0;

        var user = Environment.GetEnvironmentVariable("EDMUSER");
        var password = Environment.GetEnvironmentVariable("EDMPASSWD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine();
            Skip("transactions", "EDMUSER/EDMPASSWD not set");
            return 0;
        }

        var encrypted = SecretProtector.Protect(password);

        await SqlServerAsync(new ConnectionInfo
        {
            Name = "EDM SQL Server",
            Engine = DbEngineKind.SqlServer,
            Host = Environment.GetEnvironmentVariable("EDMHOST") ?? "localhost",
            Database = Environment.GetEnvironmentVariable("EDMDSN") ?? "CRNL",
            Username = user,
            EncryptedPassword = encrypted,
            TrustServerCertificate = true,
        });

        await OracleAsync(new ConnectionInfo
        {
            Name = "EDM Oracle",
            Engine = DbEngineKind.Oracle,
            Host = Environment.GetEnvironmentVariable("EDMORAHOST") ?? "127.0.0.1",
            Port = int.TryParse(Environment.GetEnvironmentVariable("EDMORAPORT"), out var p) ? p : 1521,
            Database = Environment.GetEnvironmentVariable("EDMORASERVICE") ?? "BPX",
            Username = user,
            EncryptedPassword = encrypted,
        });

        return _failures;
    }

    // ================= SQL Server =================

    private static async Task SqlServerAsync(ConnectionInfo conn)
    {
        Console.WriteLine();
        Console.WriteLine("--- transactions: SQL Server ---");
        var ct = CancellationToken.None;

        var (ok, msg) = await QueryRunner.TestAsync(conn, ct);
        if (!ok) { Skip("SQL Server transactions", msg); return; }

        await using (var s = new QuerySession(conn))
        {
            Check("nothing pending before anything runs", !s.HasPendingTransaction);

            var r = await s.ExecuteAsync("CREATE TABLE #qplus_tx (id int); INSERT INTO #qplus_tx VALUES (1);", ct);
            Check("session runs DDL and DML", !r.HasError, Detail(r));
            Check("the work is pending afterwards", s.HasPendingTransaction);

            await s.RollbackAsync();
            Check("nothing pending after Rollback", !s.HasPendingTransaction);
            var gone = await s.ExecuteAsync("SELECT OBJECT_ID('tempdb..#qplus_tx') AS id;", ct);
            Check("Rollback undid the table and its row", Scalar(gone) is DBNull, Detail(gone));
            await s.RollbackAsync();

            await s.ExecuteAsync("CREATE TABLE #qplus_tx (id int); INSERT INTO #qplus_tx VALUES (1), (2);", ct);
            await s.CommitAsync();
            Check("nothing pending after Commit", !s.HasPendingTransaction);
            var kept = await s.ExecuteAsync("SELECT COUNT(*) FROM #qplus_tx;", ct);
            Check("Commit kept both rows", Convert.ToInt32(Scalar(kept)) == 2, Detail(kept));
            await s.RollbackAsync();

            // A statement-level error leaves the transaction usable.
            await s.ExecuteAsync("INSERT INTO #qplus_tx VALUES (3);", ct);
            var bad = await s.ExecuteAsync("SELECT * FROM qplus_no_such_table_xyz;", ct);
            Check("a failing statement reports its error", bad.HasError, Detail(bad));
            Check("…and leaves the transaction open to Commit or Roll back", s.HasPendingTransaction);
            await s.RollbackAsync();
            var afterError = await s.ExecuteAsync("SELECT COUNT(*) FROM #qplus_tx;", ct);
            Check("rolling back after the error undid the insert before it",
                Convert.ToInt32(Scalar(afterError)) == 2, Detail(afterError));
            await s.RollbackAsync();

            // A ROLLBACK in the SQL itself ends the transaction out from under the session.
            var ended = await s.ExecuteAsync("ROLLBACK;", ct);
            Check("a ROLLBACK in the SQL leaves nothing pending", !s.HasPendingTransaction, Detail(ended));
            Check("…and the session says the transaction ended",
                ended.Messages.Any(m => m.Contains("ended on the server")), string.Join(" | ", ended.Messages));
            var still = await s.ExecuteAsync("SELECT 1 AS n;", ct);
            Check("the session keeps working afterwards", !still.HasError && s.HasPendingTransaction, Detail(still));
            await s.RollbackAsync();
        }

        // Isolation: a second connection can't see uncommitted rows, and can once they're committed.
        // The table is committed first so the second connection never waits on its definition —
        // SQL Server compiles a whole batch before running any SET LOCK_TIMEOUT in it, so a batch
        // naming an uncommitted table waits forever. READPAST then skips the row the session holds
        // locked rather than waiting for it, and a timeout keeps any regression from hanging the run.
        var shared = "##qplus_tx_" + Guid.NewGuid().ToString("N")[..8];
        await using (var s = new QuerySession(conn))
        {
            await s.ExecuteAsync($"CREATE TABLE {shared} (id int);", ct);
            await s.CommitAsync();
            await s.ExecuteAsync($"INSERT INTO {shared} VALUES (1);", ct);

            using (var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var peek = await QueryRunner.ExecuteAsync(conn, $"SELECT COUNT(*) FROM {shared} WITH (READPAST);", guard.Token);
                Check("another connection can't see the uncommitted row",
                    !peek.HasError && Convert.ToInt32(Scalar(peek)) == 0, Detail(peek));
            }

            await s.CommitAsync();
            using (var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var seen = await QueryRunner.ExecuteAsync(conn, $"SELECT COUNT(*) FROM {shared};", guard.Token);
                Check("…and can once it's committed", !seen.HasError && Convert.ToInt32(Scalar(seen)) == 1, Detail(seen));
            }

            await s.ExecuteAsync($"DROP TABLE {shared};", ct);
            await s.CommitAsync();
        }

        // Disposing a session with work pending rolls it back.
        var orphan = "##qplus_tx_" + Guid.NewGuid().ToString("N")[..8];
        await using (var s = new QuerySession(conn))
        {
            await s.ExecuteAsync($"CREATE TABLE {orphan} (id int);", ct);
        }
        using (var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var left = await QueryRunner.ExecuteAsync(conn, $"SELECT OBJECT_ID('tempdb..{orphan}') AS id;", guard.Token);
            Check("disposing a session rolls back what it left pending", Scalar(left) is DBNull, Detail(left));
        }
    }

    // ================= Oracle =================

    private static async Task OracleAsync(ConnectionInfo conn)
    {
        Console.WriteLine();
        Console.WriteLine("--- transactions: Oracle ---");
        var ct = CancellationToken.None;

        var (ok, msg) = await QueryRunner.TestAsync(conn, ct);
        if (!ok) { Skip("Oracle transactions", msg); return; }

        await using var s = new QuerySession(conn);
        var r = await s.ExecuteAsync("SELECT 1 AS n FROM DUAL", ct);
        Check("[Oracle] session runs a query", !r.HasError && r.Grids.Count == 1, Detail(r));
        Check("[Oracle] a transaction is open afterwards", s.HasPendingTransaction);

        await s.CommitAsync();
        Check("[Oracle] Commit completes", !s.HasPendingTransaction);

        await s.ExecuteAsync("SELECT 2 AS n FROM DUAL", ct);
        await s.RollbackAsync();
        Check("[Oracle] Rollback completes", !s.HasPendingTransaction);

        var again = await s.ExecuteAsync("SELECT 3 AS n FROM DUAL", ct);
        Check("[Oracle] the session runs again after both", !again.HasError, Detail(again));
        await s.RollbackAsync();

        Console.WriteLine("      (Oracle checks cover the transaction lifecycle only: showing what a rollback " +
                          "undoes needs a table, and Oracle DDL commits on its own)");
    }
}
