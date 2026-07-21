using System.Security.Cryptography;
using System.Text;

namespace Qplus.Core.Security;

/// <summary>The two independent keys used to protect query text.</summary>
public sealed class QueryKeys
{
    public QueryKeys(byte[] aesKey, byte[] macKey)
    {
        if (aesKey.Length != 32) throw new ArgumentException("AES key must be 256-bit.", nameof(aesKey));
        if (macKey.Length != 32) throw new ArgumentException("MAC key must be 256-bit.", nameof(macKey));
        AesKey = aesKey;
        MacKey = macKey;
    }

    public byte[] AesKey { get; }
    public byte[] MacKey { get; }

    /// <summary>Proves a passphrase produced these keys, without storing the passphrase.</summary>
    public string Verifier
    {
        get
        {
            using var mac = new HMACSHA256(MacKey);
            return Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(VerifierMessage)));
        }
    }

    private const string VerifierMessage = "qplus-key-verifier-v1";
}

/// <summary>
/// Authenticated encryption for query text, using AES-256-CBC with HMAC-SHA256 in
/// encrypt-then-MAC order.
///
/// Design notes:
///  * The MAC covers the IV as well as the ciphertext, and is verified in constant time
///    <em>before</em> any decryption is attempted, so tampered data is rejected outright.
///  * Encryption and authentication use separate keys — reusing one key for both is a
///    classic way to weaken the construction.
///  * A fresh random IV per record means identical queries do not produce identical
///    ciphertext, so the server cannot tell which rows match.
///  * Values carry a version prefix, so encrypted and plaintext rows can coexist while a
///    library is being migrated.
/// </summary>
public static class QueryCipher
{
    /// <summary>Marks a value as protected. Bumped if the construction ever changes.</summary>
    public const string Prefix = "QP1:";

    private const int IvSize = 16;
    private const int MacSize = 32;

    public static bool IsEncrypted(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypts a value. Empty input stays empty so blank fields aren't padded out.</summary>
    public static string Encrypt(string? plaintext, QueryKeys keys)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        var iv = RandomNumberGenerator.GetBytes(IvSize);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keys.AesKey;
        aes.IV = iv;

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        using var encryptor = aes.CreateEncryptor();
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Encrypt-then-MAC over IV || ciphertext.
        var signed = new byte[iv.Length + cipherBytes.Length];
        Buffer.BlockCopy(iv, 0, signed, 0, iv.Length);
        Buffer.BlockCopy(cipherBytes, 0, signed, iv.Length, cipherBytes.Length);

        using var hmac = new HMACSHA256(keys.MacKey);
        var tag = hmac.ComputeHash(signed);

        var blob = new byte[signed.Length + tag.Length];
        Buffer.BlockCopy(signed, 0, blob, 0, signed.Length);
        Buffer.BlockCopy(tag, 0, blob, signed.Length, tag.Length);

        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a value produced by <see cref="Encrypt"/>. Values without the prefix are
    /// returned unchanged, which is what allows a partially-migrated library to be read.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The value was altered, truncated, or encrypted under a different key.
    /// </exception>
    public static string Decrypt(string? value, QueryKeys keys)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!IsEncrypted(value)) return value;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(value[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Protected value is not valid Base64.");
        }

        if (blob.Length < IvSize + MacSize + 1)
            throw new CryptographicException("Protected value is truncated.");

        var signedLength = blob.Length - MacSize;
        var tag = new byte[MacSize];
        Buffer.BlockCopy(blob, signedLength, tag, 0, MacSize);

        var signed = new byte[signedLength];
        Buffer.BlockCopy(blob, 0, signed, 0, signedLength);

        using var hmac = new HMACSHA256(keys.MacKey);
        var expected = hmac.ComputeHash(signed);

        // Constant-time: never leak how much of the tag matched.
        if (!CryptographicOperations.FixedTimeEquals(expected, tag))
            throw new CryptographicException(
                "Query failed its integrity check — it was modified, or the encryption key is wrong.");

        var iv = new byte[IvSize];
        Buffer.BlockCopy(signed, 0, iv, 0, IvSize);
        var cipherBytes = new byte[signedLength - IvSize];
        Buffer.BlockCopy(signed, IvSize, cipherBytes, 0, cipherBytes.Length);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keys.AesKey;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>Decrypts, or returns a placeholder rather than throwing — for display paths.</summary>
    public static string DecryptOrPlaceholder(string? value, QueryKeys? keys, string placeholder = "🔒 (locked)")
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!IsEncrypted(value)) return value;
        if (keys is null) return placeholder;
        try { return Decrypt(value, keys); }
        catch (CryptographicException) { return placeholder; }
    }
}
