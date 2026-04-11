using EChat.Core.Services;
using PgpCore;
using System.Text;

namespace EChat.Core.Crypto;

public class PgpService
{
    private readonly FileLogger _fileLogger;

    public PgpService(FileLogger fileLogger)
    {
        _fileLogger = fileLogger;
    }

    /// <summary>
    /// Generates an RSA-2048 key pair. Returns (publicKeyBase64, privateKeyBase64).
    /// Both keys are ASCII-armored and then base64-encoded for storage.
    /// </summary>
    public (string publicKey, string privateKey) GenerateKeyPair(string identity, string password)
    {
        using var pgp = new PGP();
        using var publicKeyStream = new MemoryStream();
        using var privateKeyStream = new MemoryStream();

        // Pass the actual output streams — the old code was passing new MemoryStream() here
        pgp.GenerateKey(
            publicKeyStream,
            privateKeyStream,
            identity,
            password,
            strength: 2048,
            certainty: 8
        );

        var publicKey = Convert.ToBase64String(publicKeyStream.ToArray());
        var privateKey = Convert.ToBase64String(privateKeyStream.ToArray());

        _fileLogger.Write("INFO", "PgpService", $"Generated PGP key pair for {identity}");
        return (publicKey, privateKey);
    }

    public async Task<string> EncryptAsync(string plainText, string publicKeyBase64)
    {
        return await EncryptAsync(plainText, new[] { publicKeyBase64 });
    }

    public async Task<string> EncryptAsync(string plainText, IEnumerable<string> publicKeyBase64s)
    {
        _fileLogger.Write("DEBUG", "PgpService", $"EncryptAsync START, thread={Thread.CurrentThread.ManagedThreadId}");
        var keys = publicKeyBase64s.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (keys.Count == 0)
            throw new ArgumentException("No public keys provided", nameof(publicKeyBase64s));

        var streams = new List<MemoryStream>();
        foreach (var k in keys)
        {
            try
            {
                streams.Add(new MemoryStream(Convert.FromBase64String(k)));
            }
            catch (FormatException ex)
            {
                _fileLogger.Write("WARN", "PgpService", $"Invalid base64 in public key, skipping (length={k.Length}): {ex.Message}");
            }
        }

        if (streams.Count == 0)
            throw new ArgumentException("No valid public keys after filtering", nameof(publicKeyBase64s));

        var encryptionKeys = new EncryptionKeys(streams);
        using var pgp = new PGP(encryptionKeys);

        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(plainText));
        using var outputStream = new MemoryStream();

        await pgp.EncryptAsync(inputStream, outputStream);
        var result = Convert.ToBase64String(outputStream.ToArray());
        _fileLogger.Write("DEBUG", "PgpService", $"EncryptAsync END, thread={Thread.CurrentThread.ManagedThreadId}, outputLen={result.Length}");
        return result;
    }

    public async Task<string> DecryptAsync(string encryptedBase64, string privateKeyBase64, string password)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
            throw new ArgumentException("Encrypted content is empty", nameof(encryptedBase64));
        if (string.IsNullOrWhiteSpace(privateKeyBase64))
            throw new ArgumentException("Private key is empty", nameof(privateKeyBase64));

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException ex)
        {
            _fileLogger.Write("WARN", "PgpService", $"Invalid base64 in encrypted content (length={encryptedBase64.Length}): {ex.Message}");
            throw;
        }

        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
        }
        catch (FormatException ex)
        {
            _fileLogger.Write("WARN", "PgpService", $"Invalid base64 in private key (length={privateKeyBase64.Length}): {ex.Message}");
            throw;
        }

        using var inputStream = new MemoryStream(encryptedBytes);
        using var privateKeyStream = new MemoryStream(privateKeyBytes);
        using var outputStream = new MemoryStream();

        var encryptionKeys = new EncryptionKeys(privateKeyStream, password);
        using var pgp = new PGP(encryptionKeys);

        await pgp.DecryptAsync(inputStream, outputStream);
        return Encoding.UTF8.GetString(outputStream.ToArray());
    }

    public string GetFingerprint(string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
            throw new ArgumentException("Public key is empty", nameof(publicKeyBase64));

        byte[] publicKeyBytes;
        try
        {
            publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException ex)
        {
            _fileLogger.Write("WARN", "PgpService", $"Invalid base64 in public key for fingerprint (length={publicKeyBase64.Length}): {ex.Message}");
            throw;
        }

        using var publicKeyStream = new MemoryStream(publicKeyBytes);

        var encryptionKeys = new EncryptionKeys(publicKeyStream);
        var fingerprint = encryptionKeys.PublicKey.GetFingerprint();
        return BitConverter.ToString(fingerprint).Replace("-", "");
    }
}
