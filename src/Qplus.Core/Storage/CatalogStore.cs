using Microsoft.Data.Sqlite;
using Qplus.Core.Models;
using Qplus.Core.Security;

namespace Qplus.Core.Storage;

/// <summary>
/// Local SQLite catalog holding saved connections and the reusable query library.
/// Lives under %AppData%\Qplus\qplus.db by default.
/// </summary>
public sealed class CatalogStore
{
    private readonly string _connString;

    public CatalogStore(string? dbPath = null)
    {
        dbPath ??= DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Initialize();
    }

    public static string DefaultDbPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qplus");
        return Path.Combine(dir, "qplus.db");
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
CREATE TABLE IF NOT EXISTS connections (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    engine INTEGER NOT NULL,
    host TEXT NOT NULL,
    port INTEGER NOT NULL,
    database TEXT NOT NULL,
    oracle_use_sid INTEGER NOT NULL DEFAULT 0,
    integrated_security INTEGER NOT NULL DEFAULT 0,
    username TEXT NOT NULL DEFAULT '',
    enc_password TEXT NOT NULL DEFAULT '',
    trust_server_cert INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    last_used_utc TEXT
);

CREATE TABLE IF NOT EXISTS saved_queries (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    tags TEXT NOT NULL DEFAULT '',
    sql TEXT NOT NULL,
    engine INTEGER,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);";
        cmd.ExecuteNonQuery();

        MigrateSavedQueries(c);
    }

    /// <summary>
    /// Brings an existing saved_queries table up to the current shape. Older catalogs have a
    /// nullable `engine` hint and no tombstone column; both are added in place so upgrading
    /// never loses the user's query library.
    /// </summary>
    private static void MigrateSavedQueries(SqliteConnection c)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(saved_queries);";
            using var r = info.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }

        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        if (!columns.Contains("engine_scope"))
        {
            Exec("ALTER TABLE saved_queries ADD COLUMN engine_scope INTEGER NOT NULL DEFAULT 0;");
            if (columns.Contains("engine"))
            {
                // Old hint: NULL = any, 0 = SQL Server, 1 = Oracle.
                Exec(@"UPDATE saved_queries
                       SET engine_scope = CASE engine WHEN 0 THEN 1 WHEN 1 THEN 2 ELSE 0 END;");
            }
        }

        if (!columns.Contains("is_deleted"))
            Exec("ALTER TABLE saved_queries ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0;");
    }

    // ---- Connections ---------------------------------------------------------

    public List<ConnectionInfo> GetConnections()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM connections ORDER BY name COLLATE NOCASE;";
        using var r = cmd.ExecuteReader();
        var list = new List<ConnectionInfo>();
        while (r.Read())
        {
            list.Add(new ConnectionInfo
            {
                Id = r.GetString(r.GetOrdinal("id")),
                Name = r.GetString(r.GetOrdinal("name")),
                Engine = (DbEngineKind)r.GetInt32(r.GetOrdinal("engine")),
                Host = r.GetString(r.GetOrdinal("host")),
                Port = r.GetInt32(r.GetOrdinal("port")),
                Database = r.GetString(r.GetOrdinal("database")),
                OracleUseSid = r.GetInt32(r.GetOrdinal("oracle_use_sid")) != 0,
                IntegratedSecurity = r.GetInt32(r.GetOrdinal("integrated_security")) != 0,
                Username = r.GetString(r.GetOrdinal("username")),
                EncryptedPassword = r.GetString(r.GetOrdinal("enc_password")),
                TrustServerCertificate = r.GetInt32(r.GetOrdinal("trust_server_cert")) != 0,
                CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("created_utc"))),
                LastUsedUtc = r.IsDBNull(r.GetOrdinal("last_used_utc"))
                    ? null : DateTime.Parse(r.GetString(r.GetOrdinal("last_used_utc"))),
            });
        }
        return list;
    }

    public void UpsertConnection(ConnectionInfo x)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO connections
  (id,name,engine,host,port,database,oracle_use_sid,integrated_security,username,enc_password,trust_server_cert,created_utc,last_used_utc)
VALUES
  ($id,$name,$engine,$host,$port,$db,$sid,$intsec,$user,$pwd,$trust,$created,$last)
ON CONFLICT(id) DO UPDATE SET
  name=$name, engine=$engine, host=$host, port=$port, database=$db,
  oracle_use_sid=$sid, integrated_security=$intsec, username=$user,
  enc_password=$pwd, trust_server_cert=$trust, last_used_utc=$last;";
        Bind(cmd, "$id", x.Id);
        Bind(cmd, "$name", x.Name);
        Bind(cmd, "$engine", (int)x.Engine);
        Bind(cmd, "$host", x.Host);
        Bind(cmd, "$port", x.Port);
        Bind(cmd, "$db", x.Database);
        Bind(cmd, "$sid", x.OracleUseSid ? 1 : 0);
        Bind(cmd, "$intsec", x.IntegratedSecurity ? 1 : 0);
        Bind(cmd, "$user", x.Username);
        Bind(cmd, "$pwd", x.EncryptedPassword);
        Bind(cmd, "$trust", x.TrustServerCertificate ? 1 : 0);
        Bind(cmd, "$created", x.CreatedUtc.ToString("o"));
        Bind(cmd, "$last", (object?)x.LastUsedUtc?.ToString("o") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteConnection(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM connections WHERE id=$id;";
        Bind(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    public void TouchConnection(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE connections SET last_used_utc=$now WHERE id=$id;";
        Bind(cmd, "$now", DateTime.UtcNow.ToString("o"));
        Bind(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Saved queries -------------------------------------------------------

    /// <summary>
    /// Keys used to protect query text at rest. When set, name/tags/sql are encrypted on
    /// write and decrypted on read. Null means the library is stored in the clear.
    /// </summary>
    public QueryKeys? ProtectionKeys { get; set; }

    /// <summary>Live queries, sorted by name. Tombstones are excluded.</summary>
    public List<SavedQuery> GetSavedQueries() => ReadQueries("WHERE is_deleted = 0", decrypt: true);

    /// <summary>
    /// Everything changed since a watermark, tombstones included — the push half of a sync.
    /// Values are returned exactly as stored (still encrypted), so the ciphertext travels
    /// to the server unchanged and the server never sees plaintext.
    /// </summary>
    public List<SavedQuery> GetSavedQueriesChangedSince(DateTime? sinceUtc)
    {
        if (sinceUtc is null) return ReadQueries(null, decrypt: false);
        return ReadQueries("WHERE updated_utc > $since", decrypt: false,
            ("$since", sinceUtc.Value.ToString("o")));
    }

    public SavedQuery? GetSavedQuery(string id)
        => ReadQueries("WHERE id = $id", decrypt: true, ("$id", id)).FirstOrDefault();

    private List<SavedQuery> ReadQueries(string? where, bool decrypt, params (string name, object value)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // No ORDER BY in SQL: once the name column holds ciphertext, sorting it in the
        // database is meaningless. Ordering happens in memory after decryption.
        cmd.CommandText = $"SELECT * FROM saved_queries {where};";
        foreach (var (n, v) in ps) Bind(cmd, n, v);

        using var r = cmd.ExecuteReader();
        var list = new List<SavedQuery>();
        while (r.Read())
        {
            var name = r.GetString(r.GetOrdinal("name"));
            var tags = r.GetString(r.GetOrdinal("tags"));
            var sql = r.GetString(r.GetOrdinal("sql"));

            if (decrypt)
            {
                // Locked or wrong-key rows degrade to a placeholder rather than throwing,
                // so the library still lists and the user can be prompted to unlock.
                name = QueryCipher.DecryptOrPlaceholder(name, ProtectionKeys);
                tags = QueryCipher.DecryptOrPlaceholder(tags, ProtectionKeys, "");
                sql = QueryCipher.DecryptOrPlaceholder(sql, ProtectionKeys,
                    "-- This query is encrypted. Unlock it from Query ▸ Encryption…");
            }

            list.Add(new SavedQuery
            {
                Id = r.GetString(r.GetOrdinal("id")),
                Name = name,
                Tags = tags,
                Sql = sql,
                Scope = (QueryEngineScope)r.GetInt32(r.GetOrdinal("engine_scope")),
                CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("created_utc")),
                    null, System.Globalization.DateTimeStyles.RoundtripKind),
                UpdatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("updated_utc")),
                    null, System.Globalization.DateTimeStyles.RoundtripKind),
                IsDeleted = r.GetInt32(r.GetOrdinal("is_deleted")) != 0,
            });
        }

        if (decrypt)
            list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

        return list;
    }

    /// <summary>Saves a user edit, stamping UpdatedUtc to now and encrypting if enabled.</summary>
    public void UpsertSavedQuery(SavedQuery q)
    {
        q.UpdatedUtc = DateTime.UtcNow;
        WriteQuery(q, protect: true);
    }

    /// <summary>
    /// Writes a query byte-for-byte as given, preserving UpdatedUtc and skipping encryption.
    /// Used when applying rows received from the server: those values are already ciphertext,
    /// so re-encrypting would double-wrap them.
    /// </summary>
    public void UpsertSavedQueryPreservingTimestamp(SavedQuery q) => WriteQuery(q, protect: false);

    private void WriteQuery(SavedQuery q, bool protect)
    {
        var name = q.Name;
        var tags = q.Tags;
        var sql = q.Sql;

        if (protect && ProtectionKeys is { } keys)
        {
            name = QueryCipher.Encrypt(name, keys);
            tags = QueryCipher.Encrypt(tags, keys);
            sql = QueryCipher.Encrypt(sql, keys);
        }

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO saved_queries (id,name,tags,sql,engine_scope,created_utc,updated_utc,is_deleted)
VALUES ($id,$name,$tags,$sql,$scope,$created,$updated,$deleted)
ON CONFLICT(id) DO UPDATE SET
  name=$name, tags=$tags, sql=$sql, engine_scope=$scope,
  updated_utc=$updated, is_deleted=$deleted;";
        Bind(cmd, "$id", q.Id);
        Bind(cmd, "$name", name);
        Bind(cmd, "$tags", tags);
        Bind(cmd, "$sql", sql);
        Bind(cmd, "$scope", (int)q.Scope);
        Bind(cmd, "$created", q.CreatedUtc.ToString("o"));
        Bind(cmd, "$updated", q.UpdatedUtc.ToString("o"));
        Bind(cmd, "$deleted", q.IsDeleted ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Re-writes every stored query from one protection state to another: turning encryption
    /// on (oldKeys null), off (newKeys null), or rotating to a new passphrase.
    ///
    /// UpdatedUtc is bumped so the re-protected rows propagate on the next sync — otherwise
    /// other machines would keep serving the previously-encrypted copies back.
    /// </summary>
    /// <returns>How many rows were rewritten.</returns>
    public int ReprotectAllQueries(QueryKeys? oldKeys, QueryKeys? newKeys)
    {
        var rows = ReadQueries(null, decrypt: false);
        var now = DateTime.UtcNow;
        var changed = 0;

        var previous = ProtectionKeys;
        try
        {
            ProtectionKeys = newKeys;

            foreach (var q in rows)
            {
                // Decrypt with the outgoing key (a no-op for values already in the clear).
                if (oldKeys is not null)
                {
                    q.Name = QueryCipher.Decrypt(q.Name, oldKeys);
                    q.Tags = QueryCipher.Decrypt(q.Tags, oldKeys);
                    q.Sql = QueryCipher.Decrypt(q.Sql, oldKeys);
                }

                q.UpdatedUtc = now;
                WriteQuery(q, protect: true);   // uses ProtectionKeys = newKeys
                changed++;
            }
        }
        finally
        {
            ProtectionKeys = newKeys ?? previous;
        }

        return changed;
    }

    /// <summary>
    /// Tombstoned queries — deleted here or on another machine, but still recoverable
    /// because the row is kept so the deletion can propagate.
    /// </summary>
    public List<SavedQuery> GetDeletedSavedQueries() => ReadQueries("WHERE is_deleted = 1", decrypt: true);

    /// <summary>
    /// Brings a tombstoned query back. UpdatedUtc is bumped so the restoration wins over the
    /// deletion on every other machine at the next sync — otherwise they would delete it again.
    /// </summary>
    public bool RestoreSavedQuery(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE saved_queries
                            SET is_deleted = 0, updated_utc = $now
                            WHERE id = $id AND is_deleted = 1;";
        Bind(cmd, "$now", DateTime.UtcNow.ToString("o"));
        Bind(cmd, "$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Soft-deletes so the removal can propagate to other machines during sync.</summary>
    public void DeleteSavedQuery(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE saved_queries
                            SET is_deleted = 1, updated_utc = $now
                            WHERE id = $id;";
        Bind(cmd, "$now", DateTime.UtcNow.ToString("o"));
        Bind(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Settings (key/value) ------------------------------------------------

    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k;";
        Bind(cmd, "$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO settings (key,value) VALUES ($k,$v)
                            ON CONFLICT(key) DO UPDATE SET value=$v;";
        Bind(cmd, "$k", key);
        Bind(cmd, "$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
