using System.Security.Cryptography;
using System.Text;

namespace Qplus.Core.Security;

/// <summary>
/// Protects secrets (connection passwords) at rest using Windows DPAPI scoped to the
/// current user. Ciphertext is meaningless to any other user/machine, so it is safe to
/// keep in the local SQLite catalog.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Qplus.v1.secret");

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return "";
        try
        {
            var protectedBytes = Convert.FromBase64String(cipherBase64);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            // Corrupt blob or moved to a different user profile — treat as no password.
            return "";
        }
    }
}
