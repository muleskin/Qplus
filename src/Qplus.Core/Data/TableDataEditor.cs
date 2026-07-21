using System.Data;
using Qplus.Core.Models;

namespace Qplus.Core.Data;

/// <summary>Result of saving edits back to a table.</summary>
public sealed record SaveResult(bool Ok, int RowsAffected, string Message);

/// <summary>
/// Loads a table into an editable <see cref="DataTable"/> and writes inserts/updates/deletes
/// back using the provider's command builder (which derives DML from the SELECT + key info).
/// </summary>
public static class TableDataEditor
{
    /// <summary>Loads up to <paramref name="top"/> rows, including key info so edits can be saved.</summary>
    public static async Task<(DataTable table, string? error)> LoadAsync(
        ConnectionInfo info, string schema, string table, int top, CancellationToken ct)
    {
        var engine = DbEngines.For(info);
        var sql = engine.BuildSelectTopSql(schema, table, top);

        try
        {
            return await Task.Run(() =>
            {
                using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
                conn.Open();
                using var adapter = engine.CreateAdapter(sql, conn);
                // AddWithKey gives us the primary key, which the command builder needs.
                adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
                var dt = new DataTable(table);
                adapter.Fill(dt);
                return (dt, (string?)null);
            }, ct);
        }
        catch (Exception ex)
        {
            return (new DataTable(table), ex.Message);
        }
    }

    /// <summary>True when the loaded table carries a primary key, i.e. edits can be saved.</summary>
    public static bool IsEditable(DataTable table) =>
        table.PrimaryKey is { Length: > 0 };

    /// <summary>Pushes pending changes back to the database.</summary>
    public static async Task<SaveResult> SaveAsync(
        ConnectionInfo info, string schema, string table, int top, DataTable data, CancellationToken ct)
    {
        if (!IsEditable(data))
        {
            return new SaveResult(false, 0,
                $"{schema}.{table} has no primary key, so Qplus can't safely match rows for update. " +
                "Edit it with an explicit UPDATE/DELETE statement in a query tab instead.");
        }

        var changes = data.GetChanges();
        if (changes is null) return new SaveResult(true, 0, "No changes to save.");

        var engine = DbEngines.For(info);
        var sql = engine.BuildSelectTopSql(schema, table, top);

        try
        {
            var affected = await Task.Run(() =>
            {
                using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
                conn.Open();
                using var adapter = engine.CreateAdapter(sql, conn);
                adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
                using var builder = engine.CreateCommandBuilder(adapter);

                adapter.InsertCommand = builder.GetInsertCommand();
                adapter.UpdateCommand = builder.GetUpdateCommand();
                adapter.DeleteCommand = builder.GetDeleteCommand();

                return adapter.Update(data);
            }, ct);

            data.AcceptChanges();
            return new SaveResult(true, affected,
                $"Saved {affected} row{(affected == 1 ? "" : "s")}.");
        }
        catch (DBConcurrencyException)
        {
            return new SaveResult(false, 0,
                "Another session changed or deleted one of these rows. Refresh and reapply your edits.");
        }
        catch (Exception ex)
        {
            return new SaveResult(false, 0, "Save failed: " + ex.Message);
        }
    }
}
