namespace EChat.Core.Services;

/// <summary>
/// Encrypts/decrypts sensitive credential fields (IMAP password, PGP private key)
/// stored in the local SQLite database.
/// Platform implementations use OS-native secure storage: DPAPI on Windows,
/// Android Keystore on Android. The default no-op implementation stores values
/// as plaintext for development environments and unsupported platforms.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>
    /// Encrypts a plaintext value for storage in the database.
    /// Returns a prefixed ciphertext string specific to this platform.
    /// Idempotent: if the value is already protected, it is returned as-is.
    /// </summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a previously protected value back to plaintext.
    /// Transparent migration: if the value has no protection prefix (legacy
    /// plaintext stored before encryption was introduced), it is returned as-is.
    /// </summary>
    string Unprotect(string ciphertext);
}

/// <summary>
/// No-op credential protector — values are stored as plaintext.
/// Used on platforms without native secure storage and during EF migrations.
/// </summary>
public sealed class PlaintextCredentialProtector : ICredentialProtector
{
    public static readonly PlaintextCredentialProtector Instance = new();
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string ciphertext) => ciphertext;
}
