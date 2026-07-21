using Microsoft.Data.Sqlite;

namespace Qplus.Server;

/// <summary>Wire format — kept identical to the client's DTO.</summary>
public sealed class QueryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Sql { get; set; } = "";
    public int EngineScope { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class SyncRequest
{
    public string ClientId { get; set; } = "";

    /// <summary>Server revision the client already has; 0 asks for everything.</summary>
    public long SinceRev { get; set; }

    public List<QueryDto> Queries { get; set; } = new();
}

public sealed class SyncResponse
{
    /// <summary>
    /// Highest revision the client now holds. Stored as the next SinceRev.
    /// A server-assigned counter, not a clock: a row written after the client's watermark
    /// can carry an older client timestamp, so filtering by timestamp would lose it.
    /// </summary>
    public long ServerRev { get; set; }

    public DateTime ServerTimeUtc { get; set; }
    public List<QueryDto> Queries { get; set; } = new();
    public int Accepted { get; set; }
    public int Rejected { get; set; }
}

/// <summary>
/// SQLite-backed central store for the shared query library. Rows are never hard-deleted:
/// a tombstone is kept so the deletion can propagate to every client.
/// </summary>
public sealed class QueryStore
{
    private readonly string _connString;

    public QueryStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS queries (
    id           TEXT PRIMARY KEY,
    name         TEXT NOT NULL,
    tags         TEXT NOT NULL DEFAULT '',
    sql          TEXT NOT NULL,
    engine_scope INTEGER NOT NULL DEFAULT 0,
    created_utc  TEXT NOT NULL,
    updated_utc  TEXT NOT NULL,
    is_deleted   INTEGER NOT NULL DEFAULT 0,
    rev          INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_queries_rev ON queries(rev);";
        cmd.ExecuteNonQuery();

        // Defensive migration for a store created before revisions existed.
        var hasRev = false;
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(queries);";
            using var r = info.ExecuteReader();
            while (r.Read()) if (string.Equals(r.GetString(1), "rev", StringComparison.OrdinalIgnoreCase)) hasRev = true;
        }
        if (!hasRev)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = @"ALTER TABLE queries ADD COLUMN rev INTEGER NOT NULL DEFAULT 0;
                                  CREATE INDEX IF NOT EXISTS ix_queries_rev ON queries(rev);";
            alter.ExecuteNonQuery();
        }
    }

    private static long MaxRev(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT IFNULL(MAX(rev), 0) FROM queries;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    public int Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM queries WHERE is_deleted = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Applies uploaded rows (last-writer-wins on UpdatedUtc) and returns everything
    /// changed since the client's watermark.
    /// </summary>
    public SyncResponse Sync(SyncRequest request)
    {
        var accepted = 0;
        var rejected = 0;

        using var c = Open();
        using (var tx = c.BeginTransaction())
        {
            var nextRev = MaxRev(c, tx);

            foreach (var q in request.Queries)
            {
                if (string.IsNullOrWhiteSpace(q.Id)) { rejected++; continue; }

                // Conflict resolution stays on the client timestamp: last writer wins.
                var existing = ReadUpdatedUtc(c, tx, q.Id);
                if (existing is not null && existing >= q.UpdatedUtc) { rejected++; continue; }

                nextRev++;
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO queries (id,name,tags,sql,engine_scope,created_utc,updated_utc,is_deleted,rev)
VALUES ($id,$name,$tags,$sql,$scope,$created,$updated,$deleted,$rev)
ON CONFLICT(id) DO UPDATE SET
  name=$name, tags=$tags, sql=$sql, engine_scope=$scope,
  updated_utc=$updated, is_deleted=$deleted, rev=$rev;";
                Bind(cmd, "$id", q.Id);
                Bind(cmd, "$name", q.Name ?? "");
                Bind(cmd, "$tags", q.Tags ?? "");
                Bind(cmd, "$sql", q.Sql ?? "");
                Bind(cmd, "$scope", q.EngineScope);
                Bind(cmd, "$created", Iso(q.CreatedUtc));
                Bind(cmd, "$updated", Iso(q.UpdatedUtc));
                Bind(cmd, "$deleted", q.IsDeleted ? 1 : 0);
                Bind(cmd, "$rev", nextRev);
                cmd.ExecuteNonQuery();
                accepted++;
            }
            tx.Commit();
        }

        // Read after the commit so the caller sees its own writes reflected.
        var changed = ReadChangedSince(c, request.SinceRev);

        return new SyncResponse
        {
            ServerRev = MaxRev(c),
            ServerTimeUtc = DateTime.UtcNow,
            Queries = changed,
            Accepted = accepted,
            Rejected = rejected,
        };
    }

    private static DateTime? ReadUpdatedUtc(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT updated_utc FROM queries WHERE id = $id;";
        Bind(cmd, "$id", id);
        var v = cmd.ExecuteScalar() as string;
        return v is null ? null : DateTime.Parse(v, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static List<QueryDto> ReadChangedSince(SqliteConnection c, long sinceRev)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM queries WHERE rev > $since ORDER BY rev;";
        Bind(cmd, "$since", sinceRev);

        using var r = cmd.ExecuteReader();
        var list = new List<QueryDto>();
        while (r.Read())
        {
            list.Add(new QueryDto
            {
                Id = r.GetString(r.GetOrdinal("id")),
                Name = r.GetString(r.GetOrdinal("name")),
                Tags = r.GetString(r.GetOrdinal("tags")),
                Sql = r.GetString(r.GetOrdinal("sql")),
                EngineScope = r.GetInt32(r.GetOrdinal("engine_scope")),
                CreatedUtc = ParseUtc(r.GetString(r.GetOrdinal("created_utc"))),
                UpdatedUtc = ParseUtc(r.GetString(r.GetOrdinal("updated_utc"))),
                IsDeleted = r.GetInt32(r.GetOrdinal("is_deleted")) != 0,
            });
        }
        return list;
    }

    // Always store UTC in a single sortable format — the "changed since" filter is a
    // string comparison, so mixed formats or local times would silently break it.
    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("o");

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static void Bind(SqliteCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
