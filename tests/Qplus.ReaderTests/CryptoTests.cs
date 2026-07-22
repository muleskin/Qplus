using System.Security.Cryptography;
using System.Text;
using Qplus.Core.Models;
using Qplus.Core.Security;
using Qplus.Core.Storage;
using Qplus.Core.Sync;

namespace Qplus.ReaderTests;

/// <summary>
/// Query-encryption tests: the cipher itself, the key ring, encrypted storage, and an
/// end-to-end check that a synced server database contains no plaintext.
/// </summary>
public static class CryptoTests
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    public static async Task<int> RunAsync()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("--- query encryption ---");

        var keys = QueryKeyRing.Derive("correct horse battery staple");
        const string secret = "SELECT proprietary_calc(well_id) FROM confidential_model";

        // ---- cipher ---------------------------------------------------------
        var blob = QueryCipher.Encrypt(secret, keys);
        Check("ciphertext is marked and unreadable",
            QueryCipher.IsEncrypted(blob) && !blob.Contains("proprietary", StringComparison.OrdinalIgnoreCase),
            blob[..Math.Min(40, blob.Length)] + "…");
        Check("round trip", QueryCipher.Decrypt(blob, keys) == secret);

        var again = QueryCipher.Encrypt(secret, keys);
        Check("same plaintext gives different ciphertext (random IV)", blob != again);
        Check("both still decrypt", QueryCipher.Decrypt(again, keys) == secret);

        Check("empty stays empty", QueryCipher.Encrypt("", keys) == "");
        Check("plaintext passes through undecrypted", QueryCipher.Decrypt("SELECT 1", keys) == "SELECT 1");

        // ---- tamper detection ----------------------------------------------
        var raw = Convert.FromBase64String(blob[QueryCipher.Prefix.Length..]);
        raw[^1] ^= 0xFF;                                   // corrupt the MAC
        var tamperedMac = QueryCipher.Prefix + Convert.ToBase64String(raw);
        Check("tampered MAC is rejected", Throws(() => QueryCipher.Decrypt(tamperedMac, keys)));

        var raw2 = Convert.FromBase64String(blob[QueryCipher.Prefix.Length..]);
        raw2[20] ^= 0xFF;                                  // corrupt the ciphertext body
        var tamperedBody = QueryCipher.Prefix + Convert.ToBase64String(raw2);
        Check("tampered ciphertext is rejected", Throws(() => QueryCipher.Decrypt(tamperedBody, keys)));

        var raw3 = Convert.FromBase64String(blob[QueryCipher.Prefix.Length..]);
        raw3[0] ^= 0xFF;                                   // corrupt the IV (covered by the MAC)
        var tamperedIv = QueryCipher.Prefix + Convert.ToBase64String(raw3);
        Check("tampered IV is rejected", Throws(() => QueryCipher.Decrypt(tamperedIv, keys)));

        Check("truncated value is rejected",
            Throws(() => QueryCipher.Decrypt(QueryCipher.Prefix + Convert.ToBase64String(new byte[8]), keys)));

        // ---- wrong key -------------------------------------------------------
        var otherKeys = QueryKeyRing.Derive("a completely different passphrase");
        Check("wrong key cannot decrypt", Throws(() => QueryCipher.Decrypt(blob, otherKeys)));
        Check("wrong key yields a placeholder, not a crash",
            QueryCipher.DecryptOrPlaceholder(blob, otherKeys).Contains("locked"));

        // ---- key derivation --------------------------------------------------
        var same = QueryKeyRing.Derive("correct horse battery staple");
        Check("derivation is deterministic across machines", same.Verifier == keys.Verifier);
        Check("different passphrase gives a different key", otherKeys.Verifier != keys.Verifier);
        Check("encryption and MAC keys are distinct",
            !keys.AesKey.SequenceEqual(keys.MacKey));

        // ---- encrypted local storage ----------------------------------------
        var dir = Path.Combine(Path.GetTempPath(), "qplus-crypto-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var dbPath = Path.Combine(dir, "local.db");
            var store = new CatalogStore(dbPath);
            var ring = new QueryKeyRing(store);
            ring.Enable("correct horse battery staple");
            store.ProtectionKeys = ring.Keys;

            var id = Guid.NewGuid().ToString("N");
            store.UpsertSavedQuery(new SavedQuery
            {
                Id = id, Name = "Confidential model", Tags = "ip secret",
                Folder = "SecretProject", Sql = secret, Scope = QueryEngineScope.OracleOnly,
            });

            var readBack = store.GetSavedQuery(id);
            Check("encrypted store round-trips", readBack?.Sql == secret && readBack.Name == "Confidential model");

            // The bytes on disk must not contain the plaintext.
            var text = ReadDatabaseText(dbPath);
            Check("local database file holds no plaintext",
                !text.Contains("proprietary_calc") && !text.Contains("Confidential model")
                && !text.Contains("SecretProject"));
            Check("folder round-trips through encryption",
                store.GetSavedQuery(id)?.Folder == "SecretProject",
                store.GetSavedQuery(id)?.Folder ?? "n/a");

            // A locked catalog must not leak, and must not throw.
            store.ProtectionKeys = null;
            var locked = store.GetSavedQuery(id);
            Check("locked read gives a placeholder rather than plaintext",
                locked is not null && !locked.Sql.Contains("proprietary_calc"), locked?.Name ?? "n/a");
            store.ProtectionKeys = ring.Keys;

            // Wrong passphrase must be refused up front.
            var ring2 = new QueryKeyRing(store);
            Check("wrong passphrase is refused", !ring2.Unlock("not the passphrase"));
            Check("correct passphrase unlocks", ring2.Unlock("correct horse battery staple"));

            // Turning encryption off must restore readable text.
            var n = store.ReprotectAllQueries(oldKeys: ring.Keys, newKeys: null);
            Check("disable decrypts every row", n >= 1 && store.GetSavedQuery(id)?.Sql == secret, $"{n} row(s)");

            // And turning it back on must re-protect.
            store.ReprotectAllQueries(oldKeys: null, newKeys: ring.Keys);
            var recheck = ReadDatabaseText(dbPath);
            Check("re-enable protects again", !recheck.Contains("proprietary_calc"));

            // ---- end to end: the server must never hold plaintext -------------
            var server = Environment.GetEnvironmentVariable("QPLUS_TEST_SERVER");
            var serverDb = Environment.GetEnvironmentVariable("QPLUS_TEST_SERVER_DB");
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(serverDb))
            {
                Console.WriteLine("SKIP  server-side plaintext check — QPLUS_TEST_SERVER/_DB not set.");
            }
            else
            {
                var sync = new QuerySyncService(store) { ServerUrl = server };
                var result = await sync.SyncAsync(full: true, CancellationToken.None);
                Check("encrypted library syncs", result.Ok, result.Message);

                await Task.Delay(300);
                var serverText = ReadDatabaseText(serverDb);
                Check("server database holds no plaintext query text",
                    !serverText.Contains("proprietary_calc"));
                Check("server database holds no plaintext query name",
                    !serverText.Contains("Confidential model"));
                Check("server database holds no plaintext folder name",
                    !serverText.Contains("SecretProject"));
                Check("server database does hold ciphertext",
                    serverText.Contains(QueryCipher.Prefix));

                // A second machine with the passphrase must be able to read it back.
                var store2 = new CatalogStore(Path.Combine(dir, "machine2.db"));
                var ring2b = new QueryKeyRing(store2);
                ring2b.Enable("correct horse battery staple");
                store2.ProtectionKeys = ring2b.Keys;
                var sync2 = new QuerySyncService(store2) { ServerUrl = server };
                await sync2.SyncAsync(full: true, CancellationToken.None);

                var onMachine2 = store2.GetSavedQuery(id);
                Check("second machine with the passphrase reads it",
                    onMachine2?.Sql == secret, onMachine2?.Name ?? "not received");

                // A machine without the passphrase must get ciphertext only.
                var store3 = new CatalogStore(Path.Combine(dir, "machine3.db"));
                var sync3 = new QuerySyncService(store3) { ServerUrl = server };
                await sync3.SyncAsync(full: true, CancellationToken.None);
                var onMachine3 = store3.GetSavedQuery(id);
                Check("machine without the passphrase cannot read it",
                    onMachine3 is not null && !onMachine3.Sql.Contains("proprietary_calc"),
                    onMachine3 is null ? "not received" : "ciphertext only");
            }

            return _failures;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads a SQLite database as text for plaintext scanning. Opens with full sharing
    /// because the handle may still be pooled here or held by the running server, and
    /// includes the -wal sidecar, where recent writes live before a checkpoint.
    /// </summary>
    private static string ReadDatabaseText(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var sb = new StringBuilder();
        foreach (var candidate in new[] { path, path + "-wal", path + "-journal" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                sb.Append(Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch (IOException)
            {
                // Skip a file we genuinely cannot open; the others still get scanned.
            }
        }
        return sb.ToString();
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (CryptographicException) { return true; }
        catch { return false; }
    }
}
