using System.Data.Common;
using Qplus.Core.Models;

namespace Qplus.Core.Data;

/// <summary>
/// Engine-specific behaviour that ADO.NET does not abstract away: connection-string
/// construction, identifier quoting, and metadata (schema) queries.
/// </summary>
public interface IDbEngine
{
    DbEngineKind Kind { get; }

    /// <summary>Human label, e.g. "SQL Server".</summary>
    string DisplayName { get; }

    /// <summary>Build a live connection string from stored connection info (decrypts the password).</summary>
    string BuildConnectionString(ConnectionInfo info);

    /// <summary>Create an unopened provider connection for the given connection string.</summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>Quote an identifier for safe use in generated SQL.</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>Split editor text into individually-executable batches (e.g. on GO for SQL Server).</summary>
    IEnumerable<string> SplitBatches(string sql);

    /// <summary>Top-level schemas / owners for the object explorer.</summary>
    Task<IReadOnlyList<SchemaNode>> GetSchemasAsync(DbConnection open, CancellationToken ct);

    /// <summary>Tables within a schema.</summary>
    Task<IReadOnlyList<SchemaNode>> GetTablesAsync(DbConnection open, string schema, CancellationToken ct);

    /// <summary>Views within a schema.</summary>
    Task<IReadOnlyList<SchemaNode>> GetViewsAsync(DbConnection open, string schema, CancellationToken ct);

    /// <summary>Columns of a table or view.</summary>
    Task<IReadOnlyList<SchemaNode>> GetColumnsAsync(DbConnection open, string schema, string table, CancellationToken ct);

    /// <summary>
    /// Single-round-trip listing of every table and view for editor completion.
    /// Columns: schema, name, type ('TABLE' or 'VIEW').
    /// </summary>
    string ListAllObjectsSql();

    // ---- Table details ----------------------------------------------------

    /// <summary>Builds the (parameterised) query backing one tab of the table-details view.</summary>
    DbCommand CreateTableDetailCommand(DbConnection open, TableDetailKind kind, string schema, string table);

    /// <summary>Best-effort CREATE script for a table.</summary>
    Task<string> BuildTableDdlAsync(DbConnection open, string schema, string table, CancellationToken ct);

    // ---- Editable data ----------------------------------------------------

    /// <summary>Adapter used to load and save an editable result set.</summary>
    DbDataAdapter CreateAdapter(string selectSql, DbConnection open);

    /// <summary>Command builder that derives INSERT/UPDATE/DELETE from the adapter's SELECT.</summary>
    DbCommandBuilder CreateCommandBuilder(DbDataAdapter adapter);

    /// <summary>A SELECT that returns the first <paramref name="topN"/> rows of an object.</summary>
    string BuildSelectTopSql(string schema, string table, int topN);

    /// <summary>
    /// Subscribe to provider info/PRINT messages (e.g. SQL Server PRINT, low-severity RAISERROR)
    /// on an open connection, routing each line to <paramref name="sink"/>. Dispose to unsubscribe.
    /// </summary>
    IDisposable CaptureMessages(DbConnection connection, Action<string> sink);

    // ---- Visual table designer -------------------------------------------

    /// <summary>Type names offered in the designer's data-type picker.</summary>
    IReadOnlyList<string> CommonColumnTypes { get; }

    /// <summary>CREATE TABLE DDL for a fully-specified design.</summary>
    string BuildCreateTableSql(TableDesign design);

    /// <summary>ALTER TABLE … ADD DDL for a single new column.</summary>
    string BuildAddColumnSql(string schema, string table, ColumnDesign column);

    // ---- User / role administration --------------------------------------

    /// <summary>Query returning one row per user/principal (first column = name).</summary>
    string ListUsersSql();

    /// <summary>Query returning one row per grantable role (first column = name).</summary>
    string ListRolesSql();

    /// <summary>DDL to create a login/user with the given password.</summary>
    string BuildCreateUserSql(string user, string password);

    /// <summary>DDL to drop a user.</summary>
    string BuildDropUserSql(string user);

    /// <summary>DDL to grant a role to a user.</summary>
    string BuildGrantRoleSql(string role, string user);
}
