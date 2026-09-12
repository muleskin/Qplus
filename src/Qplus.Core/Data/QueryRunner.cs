using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Qplus.Core.Models;

namespace Qplus.Core.Data;

/// <summary>Executes a SQL batch against a connection and collects grids + messages.</summary>
public static class QueryRunner
{
    /// <summary>
    /// Runs <paramref name="sql"/> on a connection of its own, opened for the call and closed
    /// after it. This is auto-commit: each statement is permanent as soon as it runs. For work
    /// held until an explicit commit, see <see cref="QuerySession"/>.
    /// </summary>
    public static Task<QueryExecutionResult> ExecuteAsync(
        ConnectionInfo info, string sql, CancellationToken ct)
    {
        var engine = DbEngines.For(info);
        return RunAsync(async result =>
        {
            await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
            await conn.OpenAsync(ct);
            await RunBatchesAsync(engine, conn, null, sql, result, ct);
        });
    }

    /// <summary>
    /// Times <paramref name="work"/> and turns its failures into messages, so every route to the
    /// database reports the same way.
    /// </summary>
    internal static async Task<QueryExecutionResult> RunAsync(Func<QueryExecutionResult, Task> work)
    {
        var result = new QueryExecutionResult();
        var sw = Stopwatch.StartNew();

        try
        {
            await work(result);
        }
        catch (OperationCanceledException)
        {
            result.HasError = true;
            result.ErrorText = "Query cancelled.";
            result.Messages.Add("Query cancelled.");
        }
        catch (Exception ex)
        {
            result.HasError = true;
            result.ErrorText = ex.Message;
            result.Messages.Add("Error: " + ex.Message);
        }
        finally
        {
            sw.Stop();
            result.Elapsed = sw.Elapsed;
        }

        if (!result.HasError && result.Grids.Count == 0 && result.Messages.Count == 0)
            result.Messages.Add("Commands completed successfully.");

        return result;
    }

    /// <summary>
    /// Runs each batch of <paramref name="sql"/> on an open connection, inside
    /// <paramref name="transaction"/> when there is one.
    /// </summary>
    internal static async Task RunBatchesAsync(IDbEngine engine, DbConnection conn,
        DbTransaction? transaction, string sql, QueryExecutionResult result, CancellationToken ct)
    {
        // Capture PRINT / info messages (fired on this thread during execution).
        using var _messages = engine.CaptureMessages(conn, msg => result.Messages.Add(msg));

        foreach (var batch in engine.SplitBatches(sql))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 0; // let long queries run; user can cancel

            // SQL Server refuses to run a command on a connection with a transaction open unless
            // the command is enlisted in it explicitly.
            if (transaction is not null) cmd.Transaction = transaction;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await CollectResultsAsync(reader, result, ct);
        }
    }

    /// <summary>
    /// Walks every result set on an open reader, collecting grids and row-count messages.
    /// <para>
    /// Deliberately does NOT use <c>DataTable.Load</c>: that advances the reader to the next
    /// result set itself, so combining it with <c>NextResultAsync</c> double-advances — skipping
    /// alternate result sets and throwing "NextResultAsync when reader is closed" on the last one.
    /// </para>
    /// </summary>
    public static async Task CollectResultsAsync(
        DbDataReader reader, QueryExecutionResult result, CancellationToken ct)
    {
        do
        {
            if (reader.FieldCount > 0)
            {
                var table = await ReadTableAsync(reader, ct);
                result.Grids.Add(table);
                result.Messages.Add($"({table.Rows.Count} row{(table.Rows.Count == 1 ? "" : "s")})");
            }
            else
            {
                var affected = reader.RecordsAffected;
                if (affected >= 0)
                {
                    result.TotalRowsAffected += affected;
                    result.Messages.Add($"({affected} row{(affected == 1 ? "" : "s")} affected)");
                }
            }
        }
        while (!reader.IsClosed && await reader.NextResultAsync(ct));
    }

    /// <summary>
    /// Reads the reader's current result set into a DataTable without advancing past it.
    /// Handles unnamed columns (e.g. <c>SELECT @var</c>) and duplicate column names.
    /// </summary>
    private static async Task<DataTable> ReadTableAsync(DbDataReader reader, CancellationToken ct)
    {
        var table = new DataTable();

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(name)) name = $"(no column name)";

            // De-duplicate so DataTable doesn't throw on repeated names.
            var unique = name;
            var suffix = 1;
            while (table.Columns.Contains(unique)) unique = $"{name}_{++suffix}";

            Type type;
            try { type = reader.GetFieldType(i) ?? typeof(object); }
            catch { type = typeof(object); }

            table.Columns.Add(unique, type);
        }

        var values = new object[reader.FieldCount];
        while (await reader.ReadAsync(ct))
        {
            reader.GetValues(values);
            table.Rows.Add(values);
        }

        return table;
    }

    /// <summary>Opens and immediately closes a connection to validate credentials/reachability.</summary>
    public static async Task<(bool ok, string message)> TestAsync(ConnectionInfo info, CancellationToken ct)
    {
        var engine = DbEngines.For(info);
        try
        {
            await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
            await conn.OpenAsync(ct);
            return (true, $"Connected to {engine.DisplayName} successfully.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
