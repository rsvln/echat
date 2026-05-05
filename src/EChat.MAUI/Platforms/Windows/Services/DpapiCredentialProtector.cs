using System.Security.Cryptography;
using System.Text;
using EChat.Core.Services;

namespace EChat.Maui.Platforms.Windows.Services;

/// <summary>
/// Windows DPAPI credential protector.
/// Encrypts IMAP passwords and PGP private keys with the current Windows user's identity.
/// Only the same user account on the same machine can decrypt — stolen DB files are useless.
/// Transparent migration: values stored as plaintext before encryption was introduced
/// are returned as-is from Unprotect() and will be re-encrypted on the next save.
/// </summary>
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    private const string Prefix = "dpapi:";

    // App-specific entropy — prevents another process running as the same user
    // from using the Windows DPAPI API to decrypt our data.
    private static readonly byte[] Entropy = "echat-cred-v1"u8.ToArray();

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already protected
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext;
        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal)) return ciphertext; // legacy plaintext
        var encrypted = Convert.FromBase64String(ciphertext[Prefix.Length..]);
        var decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    public bool IsProtected(string storedValue) =>
        !string.IsNullOrEmpty(storedValue) &&
        storedValue.StartsWith(Prefix, StringComparison.Ordinal);
}
