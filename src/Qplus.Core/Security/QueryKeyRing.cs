using System.Security.Cryptography;
using System.Text;
using Qplus.Core.Storage;

namespace Qplus.Core.Security;

/// <summary>
/// Manages the query-encryption key: derives it from a passphrase, caches it for this
/// Windows account, and reports whether the library is currently readable.
///
/// The passphrase itself is never stored. The derived keys are cached under DPAPI so the
/// user is not prompted on every launch; on a new machine the passphrase must be entered
/// once, which is what makes the library portable while staying unreadable to the server.
/// </summary>
public sealed class QueryKeyRing
{
    public const string EnabledKey = "crypto.enabled";
    public const string VerifierKey = "crypto.verifier";
    public const string CachedKeyKey = "crypto.cachedKeys";

    /// <summary>
    /// PBKDF2 iterations. Deliberately high: the passphrase is the only secret protecting
    /// the library if ciphertext is ever obtained.
    /// </summary>
    public const int Iterations = 210_000;

    /// <summary>
    /// A fixed application salt. Every machine must derive the same key from the same
    /// passphrase, so the salt cannot be random per install. This trades some resistance to
    /// precomputation for portability — which is why passphrase length is what matters here.
    /// </summary>
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("Qplus.query-encryption.v1.salt");

    private readonly CatalogStore _store;
    private QueryKeys? _keys;

    public QueryKeyRing(CatalogStore store)
    {
        _store = store;
        TryUnlockFromCache();
    }

    /// <summary>True when the library is configured to be encrypted.</summary>
    public bool IsEnabled =>
        string.Equals(_store.GetSetting(EnabledKey), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the key is available and queries can be read.</summary>
    public bool IsUnlocked => _keys is not null;

    /// <summary>The active keys, or null when locked.</summary>
    public QueryKeys? Keys => _keys;

    /// <summary>Derives keys from a passphrase. Pure — no state is changed.</summary>
    public static QueryKeys Derive(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase is required.", nameof(passphrase));

        var bytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), Salt, Iterations, HashAlgorithmName.SHA256, 64);

        var aes = new byte[32];
        var mac = new byte[32];
        Buffer.BlockCopy(bytes, 0, aes, 0, 32);
        Buffer.BlockCopy(bytes, 32, mac, 0, 32);
        return new QueryKeys(aes, mac);
    }

    /// <summary>
    /// Turns encryption on for this library. Existing rows are left to the caller to
    /// migrate — see <see cref="CatalogStore.ReprotectAllQueries"/>.
    /// </summary>
    public void Enable(string passphrase)
    {
        var keys = Derive(passphrase);
        _store.SetSetting(VerifierKey, keys.Verifier);
        _store.SetSetting(EnabledKey, "true");
        _keys = keys;
        CacheKeys(keys);
    }

    /// <summary>
    /// Unlocks with a passphrase. Returns false when it does not match the stored verifier,
    /// so a typo is reported immediately instead of producing unreadable output later.
    /// </summary>
    public bool Unlock(string passphrase)
    {
        QueryKeys keys;
        try { keys = Derive(passphrase); }
        catch (ArgumentException) { return false; }

        var expected = _store.GetSetting(VerifierKey);
        if (!string.IsNullOrEmpty(expected))
        {
            var a = Encoding.UTF8.GetBytes(keys.Verifier);
            var b = Encoding.UTF8.GetBytes(expected);
            if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
                return false;
        }

        _keys = keys;
        CacheKeys(keys);
        return true;
    }

    /// <summary>Forgets the key for this session and clears the cached copy.</summary>
    public void Lock()
    {
        _keys = null;
        _store.SetSetting(CachedKeyKey, "");
    }

    /// <summary>Switches encryption off. The caller must decrypt existing rows first.</summary>
    public void Disable()
    {
        _store.SetSetting(EnabledKey, "false");
        _store.SetSetting(VerifierKey, "");
        _store.SetSetting(CachedKeyKey, "");
        _keys = null;
    }

    // ---- key caching (DPAPI, current Windows user) --------------------------

    private void CacheKeys(QueryKeys keys)
    {
        var combined = new byte[64];
        Buffer.BlockCopy(keys.AesKey, 0, combined, 0, 32);
        Buffer.BlockCopy(keys.MacKey, 0, combined, 32, 32);
        _store.SetSetting(CachedKeyKey, SecretProtector.Protect(Convert.ToBase64String(combined)));
    }

    private void TryUnlockFromCache()
    {
        if (!IsEnabled) return;

        var cached = _store.GetSetting(CachedKeyKey);
        if (string.IsNullOrEmpty(cached)) return;

        try
        {
            var raw = Convert.FromBase64String(SecretProtector.Unprotect(cached));
            if (raw.Length != 64) return;

            var aes = new byte[32];
            var mac = new byte[32];
            Buffer.BlockCopy(raw, 0, aes, 0, 32);
            Buffer.BlockCopy(raw, 32, mac, 0, 32);
            var keys = new QueryKeys(aes, mac);

            // Only accept the cache if it still matches the stored verifier.
            var expected = _store.GetSetting(VerifierKey);
            if (!string.IsNullOrEmpty(expected) && keys.Verifier != expected) return;

            _keys = keys;
        }
        catch
        {
            // Unreadable cache (e.g. catalog copied from another profile) — stay locked.
        }
    }
}
