using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Qplus.Core.Models;
using Qplus.Core.Security;

namespace Qplus.Core.Data;

public sealed class SqlServerEngine : IDbEngine
{
    public DbEngineKind Kind => DbEngineKind.SqlServer;
    public string DisplayName => "SQL Server";

    public string BuildConnectionString(ConnectionInfo info)
    {
        var dataSource = info.Port > 0 ? $"{info.Host},{info.Port}" : info.Host;
        var b = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = string.IsNullOrWhiteSpace(info.Database) ? "" : info.Database,
            TrustServerCertificate = info.TrustServerCertificate,
            ConnectTimeout = 15,
            ApplicationName = "Qplus",
        };

        if (info.IntegratedSecurity)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = info.Username;
            b.Password = SecretProtector.Unprotect(info.EncryptedPassword);
        }

        return b.ConnectionString;
    }

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public string QuoteIdentifier(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public IEnumerable<string> SplitBatches(string sql)
    {
        // Split on lines consisting solely of GO (case-insensitive), the SSMS batch separator.
        var parts = Regex.Split(sql, @"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var p in parts)
        {
            if (!string.IsNullOrWhiteSpace(p))
                yield return p;
        }
    }

    public async Task<IReadOnlyList<SchemaNode>> GetSchemasAsync(DbConnection open, CancellationToken ct)
    {
        const string sql = @"
SELECT s.name
FROM sys.schemas s
WHERE s.name NOT IN ('guest','INFORMATION_SCHEMA','sys','db_owner','db_accessadmin',
                     'db_securityadmin','db_ddladmin','db_backupoperator','db_datareader',
                     'db_datawriter','db_denydatareader','db_denydatawriter')
ORDER BY s.name;";
        var list = new List<SchemaNode>();
        await foreach (var name in ReadStringsAsync(open, sql, ct))
            list.Add(new SchemaNode { Name = name, Kind = SchemaNodeKind.SchemaFolder, Schema = name });
        return list;
    }

    public async Task<IReadOnlyList<SchemaNode>> GetTablesAsync(DbConnection open, string schema, CancellationToken ct)
    {
        const string sql = @"
SELECT t.name
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schema
ORDER BY t.name;";
        return await ReadObjectsAsync(open, sql, schema, SchemaNodeKind.Table, ct);
    }

    public async Task<IReadOnlyList<SchemaNode>> GetViewsAsync(DbConnection open, string schema, CancellationToken ct)
    {
        const string sql = @"
SELECT v.name
FROM sys.views v
JOIN sys.schemas s ON s.schema_id = v.schema_id
WHERE s.name = @schema
ORDER BY v.name;";
        return await ReadObjectsAsync(open, sql, schema, SchemaNodeKind.View, ct);
    }

    public async Task<IReadOnlyList<SchemaNode>> GetColumnsAsync(DbConnection open, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION,
       c.NUMERIC_SCALE, c.IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
ORDER BY c.ORDINAL_POSITION;";
        var list = new List<SchemaNode>();
        await using var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var type = r.IsDBNull(1) ? "" : r.GetString(1);
            var len = r.IsDBNull(2) ? (int?)null : Convert.ToInt32(r.GetValue(2));
            var prec = r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r.GetValue(3));
            var scale = r.IsDBNull(4) ? (int?)null : Convert.ToInt32(r.GetValue(4));
            var nullable = !r.IsDBNull(5) && string.Equals(r.GetString(5), "YES", StringComparison.OrdinalIgnoreCase);
            list.Add(new SchemaNode
            {
                Name = name,
                Kind = SchemaNodeKind.Column,
                Schema = schema,
                Detail = FormatType(type, len, prec, scale) + (nullable ? " null" : " not null"),
            });
        }
        return list;
    }

    public string BuildSelectTopSql(string schema, string table, int topN) =>
        $"SELECT TOP {topN} * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)};";

    public string ListAllObjectsSql() => @"
SELECT s.name AS obj_schema, t.name AS obj_name, 'TABLE' AS obj_type
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
UNION ALL
SELECT s.name, v.name, 'VIEW'
FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id
ORDER BY 1, 2;";

    public DbCommand CreateTableDetailCommand(DbConnection open, TableDetailKind kind, string schema, string table)
    {
        var cmd = open.CreateCommand();
        cmd.CommandText = kind switch
        {
            TableDetailKind.Columns => @"
SELECT c.column_id AS [#], c.name AS COLUMN_NAME, t.name AS DATA_TYPE,
       c.max_length AS [LENGTH], c.precision AS [PRECISION], c.scale AS [SCALE],
       CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END AS NULLABLE,
       dc.definition AS [DEFAULT],
       CASE WHEN c.is_identity = 1 THEN 'YES' ELSE 'NO' END AS [IDENTITY],
       CAST(ep.value AS NVARCHAR(400)) AS COMMENTS
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
LEFT JOIN sys.extended_properties ep
       ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
WHERE s.name = @schema AND o.name = @table
ORDER BY c.column_id;",

            TableDetailKind.Constraints => @"
SELECT kc.name AS CONSTRAINT_NAME, kc.type_desc AS CONSTRAINT_TYPE,
       CAST(NULL AS NVARCHAR(MAX)) AS DETAIL
FROM sys.key_constraints kc
JOIN sys.objects o ON o.object_id = kc.parent_object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.name = @table
UNION ALL
SELECT fk.name, 'FOREIGN KEY',
       CAST(OBJECT_SCHEMA_NAME(fk.referenced_object_id) + '.' + OBJECT_NAME(fk.referenced_object_id) AS NVARCHAR(MAX))
FROM sys.foreign_keys fk
JOIN sys.objects o ON o.object_id = fk.parent_object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.name = @table
UNION ALL
SELECT cc.name, 'CHECK', CAST(cc.definition AS NVARCHAR(MAX))
FROM sys.check_constraints cc
JOIN sys.objects o ON o.object_id = cc.parent_object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.name = @table
ORDER BY 2, 1;",

            TableDetailKind.Indexes => @"
SELECT i.name AS INDEX_NAME, i.type_desc AS [TYPE],
       CASE WHEN i.is_unique = 1 THEN 'YES' ELSE 'NO' END AS [UNIQUE],
       CASE WHEN i.is_primary_key = 1 THEN 'YES' ELSE 'NO' END AS PRIMARY_KEY,
       STUFF((SELECT ', ' + col.name
              FROM sys.index_columns ic
              JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
              ORDER BY ic.key_ordinal
              FOR XML PATH('')), 1, 2, '') AS [COLUMNS]
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.name = @table AND i.type > 0
ORDER BY i.name;",

            TableDetailKind.Triggers => @"
SELECT tr.name AS TRIGGER_NAME,
       CASE WHEN tr.is_disabled = 1 THEN 'DISABLED' ELSE 'ENABLED' END AS STATUS,
       CASE WHEN tr.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END AS [TYPE],
       OBJECT_DEFINITION(tr.object_id) AS [DEFINITION]
FROM sys.triggers tr
JOIN sys.objects o ON o.object_id = tr.parent_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.name = @table
ORDER BY tr.name;",

            TableDetailKind.Dependencies => @"
SELECT DISTINCT 'USES THIS' AS DIRECTION,
       OBJECT_SCHEMA_NAME(d.referencing_id) AS [SCHEMA],
       OBJECT_NAME(d.referencing_id) AS [NAME],
       o2.type_desc AS [TYPE]
FROM sys.sql_expression_dependencies d
JOIN sys.objects o2 ON o2.object_id = d.referencing_id
WHERE d.referenced_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
UNION ALL
SELECT DISTINCT 'USED BY THIS',
       OBJECT_SCHEMA_NAME(d.referenced_id),
       OBJECT_NAME(d.referenced_id),
       o2.type_desc
FROM sys.sql_expression_dependencies d
JOIN sys.objects o2 ON o2.object_id = d.referenced_id
WHERE d.referencing_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
ORDER BY 1, 2, 3;",

            TableDetailKind.Grants => @"
SELECT pr.name AS GRANTEE, pr.type_desc AS GRANTEE_TYPE,
       p.permission_name AS PERMISSION, p.state_desc AS STATE
FROM sys.database_permissions p
JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id
WHERE p.major_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
ORDER BY pr.name, p.permission_name;",

            TableDetailKind.Statistics => @"
SELECT 'Rows' AS [STATISTIC],
       CAST(SUM(p.rows) AS NVARCHAR(50)) AS [VALUE]
FROM sys.partitions p
WHERE p.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) AND p.index_id IN (0, 1)
UNION ALL
SELECT 'Total space (KB)',
       CAST(SUM(a.total_pages) * 8 AS NVARCHAR(50))
FROM sys.allocation_units a
JOIN sys.partitions p ON p.partition_id = a.container_id
WHERE p.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
UNION ALL
SELECT 'Created', CONVERT(NVARCHAR(30), o.create_date, 120) FROM sys.objects o
WHERE o.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
UNION ALL
SELECT 'Last modified', CONVERT(NVARCHAR(30), o.modify_date, 120) FROM sys.objects o
WHERE o.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table));",

            _ => throw new NotSupportedException($"Unsupported detail kind: {kind}"),
        };

        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);
        return cmd;
    }

    public async Task<string> BuildTableDdlAsync(DbConnection open, string schema, string table, CancellationToken ct)
    {
        // SQL Server has no built-in "get DDL", so assemble one from metadata.
        const string sql = @"
SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale,
       c.is_nullable, c.is_identity, dc.definition
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
WHERE s.name = @schema AND o.name = @table
ORDER BY c.column_id;";

        var lines = new List<string>();
        await using (var cmd = open.CreateCommand())
        {
            cmd.CommandText = sql;
            AddParam(cmd, "@schema", schema);
            AddParam(cmd, "@table", table);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var typeName = r.GetString(1);
                var maxLen = r.IsDBNull(2) ? (int?)null : Convert.ToInt32(r.GetValue(2));
                var prec = r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r.GetValue(3));
                var scale = r.IsDBNull(4) ? (int?)null : Convert.ToInt32(r.GetValue(4));
                var nullable = !r.IsDBNull(5) && Convert.ToBoolean(r.GetValue(5));
                var identity = !r.IsDBNull(6) && Convert.ToBoolean(r.GetValue(6));
                var def = r.IsDBNull(7) ? null : r.GetString(7);

                var typeText = FormatDdlType(typeName, maxLen, prec, scale);
                var line = $"    {QuoteIdentifier(name)} {typeText}";
                if (identity) line += " IDENTITY(1,1)";
                if (def is not null) line += $" DEFAULT {def}";
                line += nullable ? " NULL" : " NOT NULL";
                lines.Add(line);
            }
        }

        if (lines.Count == 0) return $"-- Table {schema}.{table} not found.";

        // Primary key, if any.
        const string pkSql = @"
SELECT col.name
FROM sys.key_constraints kc
JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
JOIN sys.objects o ON o.object_id = kc.parent_object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE kc.type = 'PK' AND s.name = @schema AND o.name = @table
ORDER BY ic.key_ordinal;";

        var pk = new List<string>();
        await using (var cmd = open.CreateCommand())
        {
            cmd.CommandText = pkSql;
            AddParam(cmd, "@schema", schema);
            AddParam(cmd, "@table", table);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) pk.Add(QuoteIdentifier(r.GetString(0)));
        }
        if (pk.Count > 0) lines.Add($"    PRIMARY KEY ({string.Join(", ", pk)})");

        return $"CREATE TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} (\n"
             + string.Join(",\n", lines) + "\n);";
    }

    private static string FormatDdlType(string typeName, int? maxLen, int? prec, int? scale)
    {
        switch (typeName.ToLowerInvariant())
        {
            case "varchar" or "char" or "varbinary" or "binary":
                return maxLen == -1 ? $"{typeName}(max)" : $"{typeName}({maxLen})";
            case "nvarchar" or "nchar":
                // max_length is in bytes for N types.
                return maxLen == -1 ? $"{typeName}(max)" : $"{typeName}({maxLen / 2})";
            case "decimal" or "numeric":
                return $"{typeName}({prec},{scale})";
            default:
                return typeName;
        }
    }

    public DbDataAdapter CreateAdapter(string selectSql, DbConnection open)
        => new SqlDataAdapter(selectSql, (SqlConnection)open);

    public DbCommandBuilder CreateCommandBuilder(DbDataAdapter adapter)
        => new SqlCommandBuilder((SqlDataAdapter)adapter);

    public IDisposable CaptureMessages(DbConnection connection, Action<string> sink)
    {
        var conn = (SqlConnection)connection;
        void Handler(object sender, SqlInfoMessageEventArgs e)
        {
            // Each PRINT / low-severity RAISERROR surfaces as a SqlError entry.
            foreach (SqlError err in e.Errors)
                sink(err.Message);
        }
        conn.InfoMessage += Handler;
        return new DisposableAction(() => conn.InfoMessage -= Handler);
    }

    public IReadOnlyList<string> CommonColumnTypes { get; } = new[]
    {
        "int", "bigint", "smallint", "tinyint", "bit",
        "decimal", "numeric", "money", "float", "real",
        "char", "varchar", "nchar", "nvarchar", "text",
        "date", "datetime", "datetime2", "datetimeoffset", "time",
        "uniqueidentifier", "varbinary",
    };

    public string BuildCreateTableSql(TableDesign design)
    {
        var schema = string.IsNullOrWhiteSpace(design.Schema) ? "dbo" : design.Schema;
        var lines = new List<string>();
        foreach (var c in design.Columns.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            var def = $"    {QuoteIdentifier(c.Name)} {c.TypeText}";
            def += c.Nullable ? " NULL" : " NOT NULL";
            if (!string.IsNullOrWhiteSpace(c.Default)) def += $" DEFAULT {c.Default}";
            lines.Add(def);
        }
        var pk = design.Columns.Where(c => c.PrimaryKey && !string.IsNullOrWhiteSpace(c.Name))
                               .Select(c => QuoteIdentifier(c.Name)).ToList();
        if (pk.Count > 0)
            lines.Add($"    PRIMARY KEY ({string.Join(", ", pk)})");

        return $"CREATE TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(design.Name)} (\n"
             + string.Join(",\n", lines) + "\n);";
    }

    public string BuildAddColumnSql(string schema, string table, ColumnDesign c)
    {
        var def = $"{QuoteIdentifier(c.Name)} {c.TypeText}" + (c.Nullable ? " NULL" : " NOT NULL");
        if (!string.IsNullOrWhiteSpace(c.Default)) def += $" DEFAULT {c.Default}";
        return $"ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} ADD {def};";
    }

    public string ListUsersSql() =>
        "SELECT name FROM sys.database_principals WHERE type IN ('S','U','G') AND name NOT LIKE 'db[_]%' ORDER BY name;";

    public string ListRolesSql() =>
        "SELECT name FROM sys.database_principals WHERE type = 'R' ORDER BY name;";

    public string BuildCreateUserSql(string user, string password)
    {
        var u = QuoteIdentifier(user);
        var lit = "'" + password.Replace("'", "''") + "'";
        return $"CREATE LOGIN {u} WITH PASSWORD = {lit};\nGO\nCREATE USER {u} FOR LOGIN {u};";
    }

    public string BuildDropUserSql(string user) => $"DROP USER {QuoteIdentifier(user)};";

    public string BuildGrantRoleSql(string role, string user) =>
        $"ALTER ROLE {QuoteIdentifier(role)} ADD MEMBER {QuoteIdentifier(user)};";

    private static string FormatType(string type, int? len, int? prec, int? scale)
    {
        if (len is int l) return l < 0 ? $"{type}(max)" : $"{type}({l})";
        if (prec is int p && scale is int s && (type is "decimal" or "numeric")) return $"{type}({p},{s})";
        return type;
    }

    private async Task<IReadOnlyList<SchemaNode>> ReadObjectsAsync(
        DbConnection open, string sql, string schema, SchemaNodeKind kind, CancellationToken ct)
    {
        var list = new List<SchemaNode>();
        await using var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@schema", schema);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            list.Add(new SchemaNode
            {
                Name = name,
                Kind = kind,
                Schema = schema,
                QualifiedName = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}",
            });
        }
        return list;
    }

    private static async IAsyncEnumerable<string> ReadStringsAsync(
        DbConnection open, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            yield return r.GetString(0);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
