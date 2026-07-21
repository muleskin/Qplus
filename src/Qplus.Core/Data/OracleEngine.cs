using System.Data.Common;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Qplus.Core.Models;
using Qplus.Core.Security;

namespace Qplus.Core.Data;

public sealed class OracleEngine : IDbEngine
{
    public DbEngineKind Kind => DbEngineKind.Oracle;
    public string DisplayName => "Oracle";

    public string BuildConnectionString(ConnectionInfo info)
    {
        var port = info.Port > 0 ? info.Port : 1521;
        // Build an EZCONNECT / TNS descriptor so no tnsnames.ora is required.
        var connectData = info.OracleUseSid
            ? $"(SID={info.Database})"
            : $"(SERVICE_NAME={info.Database})";
        var descriptor =
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={info.Host})(PORT={port}))" +
            $"(CONNECT_DATA={connectData}))";

        var b = new OracleConnectionStringBuilder
        {
            DataSource = descriptor,
            UserID = info.Username,
            Password = SecretProtector.Unprotect(info.EncryptedPassword),
            ConnectionTimeout = 15,
        };
        return b.ConnectionString;
    }

    public DbConnection CreateConnection(string connectionString) => new OracleConnection(connectionString);

    public string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public IEnumerable<string> SplitBatches(string sql)
    {
        // Oracle has no GO. Strip a single trailing statement terminator and run as one batch.
        var trimmed = Regex.Replace(sql.Trim(), @";\s*$", "");
        if (!string.IsNullOrWhiteSpace(trimmed))
            yield return trimmed;
    }

    public async Task<IReadOnlyList<SchemaNode>> GetSchemasAsync(DbConnection open, CancellationToken ct)
    {
        // Only schemas that actually own tables/views the current user can see, to keep the tree small.
        const string sql = @"
SELECT owner FROM all_tables
UNION
SELECT owner FROM all_views
ORDER BY 1";
        var list = new List<SchemaNode>();
        await using var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            list.Add(new SchemaNode { Name = name, Kind = SchemaNodeKind.SchemaFolder, Schema = name });
        }
        return list;
    }

    public Task<IReadOnlyList<SchemaNode>> GetTablesAsync(DbConnection open, string schema, CancellationToken ct)
    {
        // Exclude recycle-bin objects (dropped tables Oracle keeps as BIN$…).
        const string sql = @"SELECT table_name FROM all_tables
                             WHERE owner = :owner AND table_name NOT LIKE 'BIN$%'
                             ORDER BY table_name";
        return ReadObjectsAsync(open, sql, schema, SchemaNodeKind.Table, ct);
    }

    public Task<IReadOnlyList<SchemaNode>> GetViewsAsync(DbConnection open, string schema, CancellationToken ct)
    {
        const string sql = @"SELECT view_name FROM all_views
                             WHERE owner = :owner AND view_name NOT LIKE 'BIN$%'
                             ORDER BY view_name";
        return ReadObjectsAsync(open, sql, schema, SchemaNodeKind.View, ct);
    }

    public async Task<IReadOnlyList<SchemaNode>> GetColumnsAsync(DbConnection open, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT column_name, data_type, data_length, data_precision, data_scale, nullable
FROM all_tab_columns
WHERE owner = :owner AND table_name = :tab
ORDER BY column_id";
        var list = new List<SchemaNode>();
        await using var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "owner", schema);
        AddParam(cmd, "tab", table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var type = r.IsDBNull(1) ? "" : r.GetString(1);
            var len = r.IsDBNull(2) ? (int?)null : Convert.ToInt32(r.GetValue(2));
            var prec = r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r.GetValue(3));
            var scale = r.IsDBNull(4) ? (int?)null : Convert.ToInt32(r.GetValue(4));
            var nullable = !r.IsDBNull(5) && string.Equals(r.GetString(5), "Y", StringComparison.OrdinalIgnoreCase);
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
        $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} FETCH FIRST {topN} ROWS ONLY";

    public string ListAllObjectsSql() => @"
SELECT owner AS obj_schema, table_name AS obj_name, 'TABLE' AS obj_type FROM all_tables
WHERE table_name NOT LIKE 'BIN$%'
UNION ALL
SELECT owner, view_name, 'VIEW' FROM all_views
WHERE view_name NOT LIKE 'BIN$%'
ORDER BY 1, 2";

    public DbCommand CreateTableDetailCommand(DbConnection open, TableDetailKind kind, string schema, string table)
    {
        var cmd = open.CreateCommand();
        cmd.CommandText = kind switch
        {
            TableDetailKind.Columns => @"
SELECT c.column_id AS ""#"", c.column_name AS COLUMN_NAME, c.data_type AS DATA_TYPE,
       c.data_length AS ""LENGTH"", c.data_precision AS ""PRECISION"", c.data_scale AS ""SCALE"",
       c.nullable AS NULLABLE, c.data_default AS ""DEFAULT"", cc.comments AS COMMENTS
FROM all_tab_columns c
LEFT JOIN all_col_comments cc
       ON cc.owner = c.owner AND cc.table_name = c.table_name AND cc.column_name = c.column_name
WHERE c.owner = :owner AND c.table_name = :tab
ORDER BY c.column_id",

            TableDetailKind.Constraints => @"
SELECT c.constraint_name AS CONSTRAINT_NAME,
       CASE c.constraint_type WHEN 'P' THEN 'PRIMARY KEY' WHEN 'R' THEN 'FOREIGN KEY'
            WHEN 'U' THEN 'UNIQUE' WHEN 'C' THEN 'CHECK' ELSE c.constraint_type END AS CONSTRAINT_TYPE,
       c.status AS STATUS, c.search_condition AS DETAIL
FROM all_constraints c
WHERE c.owner = :owner AND c.table_name = :tab
ORDER BY 2, 1",

            TableDetailKind.Indexes => @"
SELECT i.index_name AS INDEX_NAME, i.index_type AS ""TYPE"", i.uniqueness AS UNIQUENESS,
       LISTAGG(ic.column_name, ', ') WITHIN GROUP (ORDER BY ic.column_position) AS ""COLUMNS""
FROM all_indexes i
LEFT JOIN all_ind_columns ic
       ON ic.index_owner = i.owner AND ic.index_name = i.index_name
WHERE i.table_owner = :owner AND i.table_name = :tab
GROUP BY i.index_name, i.index_type, i.uniqueness
ORDER BY i.index_name",

            TableDetailKind.Triggers => @"
SELECT trigger_name AS TRIGGER_NAME, status AS STATUS,
       trigger_type AS ""TYPE"", triggering_event AS EVENT
FROM all_triggers
WHERE table_owner = :owner AND table_name = :tab
ORDER BY trigger_name",

            TableDetailKind.Dependencies => @"
SELECT 'USES THIS' AS DIRECTION, d.owner AS ""SCHEMA"", d.name AS ""NAME"", d.type AS ""TYPE""
FROM all_dependencies d
WHERE d.referenced_owner = :owner AND d.referenced_name = :tab
UNION ALL
SELECT 'USED BY THIS', d.referenced_owner, d.referenced_name, d.referenced_type
FROM all_dependencies d
WHERE d.owner = :owner AND d.name = :tab
ORDER BY 1, 2, 3",

            TableDetailKind.Grants => @"
SELECT grantee AS GRANTEE, privilege AS PRIVILEGE, grantor AS GRANTOR, grantable AS GRANTABLE
FROM all_tab_privs
WHERE table_schema = :owner AND table_name = :tab
ORDER BY grantee, privilege",

            TableDetailKind.Statistics => @"
SELECT 'Rows' AS ""STATISTIC"", TO_CHAR(num_rows) AS ""VALUE"" FROM all_tables
WHERE owner = :owner AND table_name = :tab
UNION ALL
SELECT 'Blocks', TO_CHAR(blocks) FROM all_tables WHERE owner = :owner AND table_name = :tab
UNION ALL
SELECT 'Avg row len', TO_CHAR(avg_row_len) FROM all_tables WHERE owner = :owner AND table_name = :tab
UNION ALL
SELECT 'Tablespace', tablespace_name FROM all_tables WHERE owner = :owner AND table_name = :tab
UNION ALL
SELECT 'Last analyzed', TO_CHAR(last_analyzed, 'YYYY-MM-DD HH24:MI:SS') FROM all_tables
WHERE owner = :owner AND table_name = :tab",

            _ => throw new NotSupportedException($"Unsupported detail kind: {kind}"),
        };

        // Some statements bind :owner/:tab more than once; bind by name to keep it simple.
        if (cmd is OracleCommand oc) oc.BindByName = true;
        AddParam(cmd, "owner", schema);
        AddParam(cmd, "tab", table);
        return cmd;
    }

    public async Task<string> BuildTableDdlAsync(DbConnection open, string schema, string table, CancellationToken ct)
    {
        // Oracle can produce real DDL for us.
        await using var cmd = open.CreateCommand();
        cmd.CommandText = "SELECT DBMS_METADATA.GET_DDL('TABLE', :tab, :owner) FROM dual";
        if (cmd is OracleCommand oc) oc.BindByName = true;
        AddParam(cmd, "tab", table);
        AddParam(cmd, "owner", schema);
        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result?.ToString() ?? $"-- No DDL returned for {schema}.{table}";
        }
        catch (Exception ex)
        {
            return $"-- DBMS_METADATA.GET_DDL failed: {ex.Message}";
        }
    }

    public DbDataAdapter CreateAdapter(string selectSql, DbConnection open)
        => new OracleDataAdapter(selectSql, (OracleConnection)open);

    public DbCommandBuilder CreateCommandBuilder(DbDataAdapter adapter)
        => new OracleCommandBuilder((OracleDataAdapter)adapter);

    public IDisposable CaptureMessages(DbConnection connection, Action<string> sink)
    {
        var conn = (OracleConnection)connection;
        // Note: Oracle DBMS_OUTPUT is not delivered here; InfoMessage carries warnings only.
        void Handler(object sender, OracleInfoMessageEventArgs e) => sink(e.Message);
        conn.InfoMessage += Handler;
        return new DisposableAction(() => conn.InfoMessage -= Handler);
    }

    public IReadOnlyList<string> CommonColumnTypes { get; } = new[]
    {
        "NUMBER", "INTEGER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE",
        "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "CLOB", "NCLOB",
        "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE",
        "RAW", "BLOB", "LONG",
    };

    public string BuildCreateTableSql(TableDesign design)
    {
        var lines = new List<string>();
        foreach (var c in design.Columns.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            var def = $"    {QuoteIdentifier(c.Name)} {c.TypeText}";
            if (!string.IsNullOrWhiteSpace(c.Default)) def += $" DEFAULT {c.Default}";
            def += c.Nullable ? "" : " NOT NULL";
            lines.Add(def);
        }
        var pk = design.Columns.Where(c => c.PrimaryKey && !string.IsNullOrWhiteSpace(c.Name))
                               .Select(c => QuoteIdentifier(c.Name)).ToList();
        if (pk.Count > 0)
            lines.Add($"    PRIMARY KEY ({string.Join(", ", pk)})");

        var owner = string.IsNullOrWhiteSpace(design.Schema)
            ? QuoteIdentifier(design.Name)
            : $"{QuoteIdentifier(design.Schema)}.{QuoteIdentifier(design.Name)}";
        return $"CREATE TABLE {owner} (\n" + string.Join(",\n", lines) + "\n);";
    }

    public string BuildAddColumnSql(string schema, string table, ColumnDesign c)
    {
        var def = $"{QuoteIdentifier(c.Name)} {c.TypeText}";
        if (!string.IsNullOrWhiteSpace(c.Default)) def += $" DEFAULT {c.Default}";
        def += c.Nullable ? "" : " NOT NULL";
        return $"ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} ADD ({def});";
    }

    public string ListUsersSql() => "SELECT username FROM all_users ORDER BY username";

    public string ListRolesSql() =>
        "SELECT role FROM session_roles UNION SELECT granted_role FROM user_role_privs ORDER BY 1";

    public string BuildCreateUserSql(string user, string password)
    {
        // Oracle identifiers/passwords: quote the user; password as a quoted identifier avoids reserved-word issues.
        var u = QuoteIdentifier(user);
        var pwd = "\"" + password.Replace("\"", "") + "\"";
        return $"CREATE USER {u} IDENTIFIED BY {pwd};\nGRANT CREATE SESSION TO {u};";
    }

    public string BuildDropUserSql(string user) => $"DROP USER {QuoteIdentifier(user)} CASCADE;";

    public string BuildGrantRoleSql(string role, string user) =>
        $"GRANT {QuoteIdentifier(role)} TO {QuoteIdentifier(user)};";

    private static string FormatType(string type, int? len, int? prec, int? scale)
    {
        if (prec is int p)
            return scale is int s && s > 0 ? $"{type}({p},{s})" : $"{type}({p})";
        if (len is int l && (type.Contains("CHAR", StringComparison.OrdinalIgnoreCase)))
            return $"{type}({l})";
        return type;
    }

    private async Task<IReadOnlyList<SchemaNode>> ReadObjectsAsync(
        DbConnection open, string sql, string schema, SchemaNodeKind kind, CancellationToken ct)
    {
        var list = new List<SchemaNode>();
        await using var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "owner", schema);
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

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
