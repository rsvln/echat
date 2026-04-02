using Microsoft.Extensions.Logging;
using PgpCore;
using System.Text;

namespace EChat.Core.Crypto;

public class PgpService
{
    private readonly ILogger<PgpService> _logger;

    public PgpService(ILogger<PgpService> logger)
    {
        _logger = logger;
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

        _logger.LogInformation("Generated PGP key pair for {Identity}", identity);
        return (publicKey, privateKey);
    }

    public async Task<string> EncryptAsync(string plainText, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
            throw new ArgumentException("Public key is empty", nameof(publicKeyBase64));

        var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        using var publicKeyStream = new MemoryStream(publicKeyBytes);

        var encryptionKeys = new EncryptionKeys(publicKeyStream);
        using var pgp = new PGP(encryptionKeys);

        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(plainText));
        using var outputStream = new MemoryStream();

        await pgp.EncryptAsync(inputStream, outputStream);
        return Convert.ToBase64String(outputStream.ToArray());
    }

    public async Task<string> DecryptAsync(string encryptedBase64, string privateKeyBase64, string password)
    {
        var encryptedBytes = Convert.FromBase64String(encryptedBase64);
        var privateKeyBytes = Convert.FromBase64String(privateKeyBase64);

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
        var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        using var publicKeyStream = new MemoryStream(publicKeyBytes);

        var encryptionKeys = new EncryptionKeys(publicKeyStream);
        var fingerprint = encryptionKeys.PublicKey.GetFingerprint();
        return BitConverter.ToString(fingerprint).Replace("-", "");
    }
}
