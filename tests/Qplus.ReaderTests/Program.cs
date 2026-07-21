using Microsoft.Data.Sqlite;
using Qplus.Core.Data;
using Qplus.Core.Models;

// Drives the real QueryRunner.CollectResultsAsync against SQLite, which (like SQL Server)
// returns multiple result sets from a single command. Regression guard for the
// "NextResultAsync when reader is closed" bug caused by DataTable.Load double-advancing.

var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) failures++;
}

async Task<QueryExecutionResult> RunAsync(string sql)
{
    await using var conn = new SqliteConnection("Data Source=:memory:");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();

    var result = new QueryExecutionResult();
    await QueryRunner.CollectResultsAsync(reader, result, CancellationToken.None);
    return result;
}

// 1) Single result set — the exact shape that used to throw on the trailing NextResult.
var r1 = await RunAsync("SELECT 1 AS a;");
Check("single result set: no exception", true);
Check("single result set: 1 grid", r1.Grids.Count == 1, $"got {r1.Grids.Count}");
Check("single result set: 1 row", r1.Grids.Count == 1 && r1.Grids[0].Rows.Count == 1);

// 2) Multiple result sets — DataTable.Load used to silently skip alternate ones.
var r2 = await RunAsync("SELECT 1 AS a; SELECT 2 AS b; SELECT 3 AS c;");
Check("three result sets: all captured", r2.Grids.Count == 3, $"got {r2.Grids.Count}");
if (r2.Grids.Count == 3)
{
    Check("result set order/values preserved",
        Convert.ToInt32(r2.Grids[0].Rows[0][0]) == 1 &&
        Convert.ToInt32(r2.Grids[1].Rows[0][0]) == 2 &&
        Convert.ToInt32(r2.Grids[2].Rows[0][0]) == 3);
    Check("column names preserved",
        r2.Grids[0].Columns[0].ColumnName == "a" &&
        r2.Grids[2].Columns[0].ColumnName == "c");
}

// 3) Multi-row result set.
var r3 = await RunAsync("SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3;");
Check("multi-row: 3 rows", r3.Grids.Count == 1 && r3.Grids[0].Rows.Count == 3,
    $"rows={(r3.Grids.Count == 1 ? r3.Grids[0].Rows.Count : -1)}");

// 4) Unnamed expression column (SQL Server's `SELECT @var` analogue).
var r4 = await RunAsync("SELECT 1+1;");
Check("unnamed column: grid produced with a usable column name",
    r4.Grids.Count == 1 && r4.Grids[0].Columns.Count == 1 &&
    !string.IsNullOrWhiteSpace(r4.Grids[0].Columns[0].ColumnName),
    r4.Grids.Count == 1 ? $"name='{r4.Grids[0].Columns[0].ColumnName}'" : "no grid");

// 5) Duplicate column names must not throw.
try
{
    var r5 = await RunAsync("SELECT 1 AS dup, 2 AS dup;");
    Check("duplicate column names de-duplicated",
        r5.Grids.Count == 1 && r5.Grids[0].Columns.Count == 2,
        r5.Grids.Count == 1
            ? $"cols={string.Join(",", r5.Grids[0].Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName))}"
            : "no grid");
}
catch (Exception ex)
{
    Check("duplicate column names de-duplicated", false, ex.GetType().Name + ": " + ex.Message);
}

Console.WriteLine();
failures += Qplus.ReaderTests.AnalyzerTests.Run();
failures += await Qplus.ReaderTests.SyncTests.RunAsync();
failures += await Qplus.ReaderTests.CryptoTests.RunAsync();
failures += await Qplus.ReaderTests.IntegrationTests.RunAsync();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
