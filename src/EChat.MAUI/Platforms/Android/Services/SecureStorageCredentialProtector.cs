using System.Security.Cryptography;
using System.Text;
using EChat.Core.Services;

namespace EChat.Maui.Platforms.Android.Services;

/// <summary>
/// Android Keystore-backed credential protector via MAUI SecureStorage.
/// On first launch generates a random 256-bit AES key and stores it in SecureStorage,
/// which uses Android EncryptedSharedPreferences backed by the hardware Keystore.
/// Subsequent launches load the key from SecureStorage.
/// Each Protect() call uses a fresh random nonce — identical plaintexts produce
/// different ciphertexts.
///
/// Call <see cref="InitializeAsync"/> at app startup before the first DB access.
/// If initialization fails (extremely rare), falls back to plaintext (same as before
/// encryption was introduced).
/// </summary>
public sealed class SecureStorageCredentialProtector : ICredentialProtector
{
    private const string KeyAlias  = "echat_cred_key_v1";
    private const string Prefix    = "aes:";
    private const int    NonceSize = 12;
    private const int    TagSize   = 16;

    private byte[]? _key;

    /// <summary>
    /// Loads (or generates) the AES key from Android Keystore via SecureStorage.
    /// Must be called once at startup before the database is opened.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var stored = await SecureStorage.Default.GetAsync(KeyAlias);
            if (!string.IsNullOrEmpty(stored))
            {
                _key = Convert.FromBase64String(stored);
            }
            else
            {
                _key = RandomNumberGenerator.GetBytes(32);
                await SecureStorage.Default.SetAsync(KeyAlias, Convert.ToBase64String(_key));
            }
        }
        catch (Exception ex)
        {
            // Keystore unavailable on this device — fall back to plaintext.
            System.Diagnostics.Debug.WriteLine(
                $"[eChat] SecureStorageCredentialProtector: init failed, falling back to plaintext: {ex.Message}");
            _key = null;
        }
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || _key == null) return plaintext;
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already protected

        var nonce      = RandomNumberGenerator.GetBytes(NonceSize);
        var data       = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length];
        var tag        = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, ciphertext, tag);

        // Layout: nonce (12) | ciphertext (N) | tag (16)
        var packed = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(packed, 0);
        ciphertext.CopyTo(packed, NonceSize);
        tag.CopyTo(packed, NonceSize + ciphertext.Length);

        return Prefix + Convert.ToBase64String(packed);
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext;
        // No prefix = legacy plaintext (stored before encryption was introduced).
        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal)) return ciphertext;

        if (_key == null)
        {
            // Key was lost (Keystore reset after device wipe / backup restore).
            // Return empty string — the user will need to re-enter credentials in Settings.
            return string.Empty;
        }

        var packed     = Convert.FromBase64String(ciphertext[Prefix.Length..]);
        var nonce      = packed[..NonceSize];
        var tag        = packed[^TagSize..];
        var encrypted  = packed[NonceSize..^TagSize];
        var plaintext  = new byte[encrypted.Length];

        using var aes = new AesGcm(_key, TagSize);
        try
        {
            aes.Decrypt(nonce, encrypted, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // Auth tag mismatch — tampered data or wrong key.
            return string.Empty;
        }
    }
}
