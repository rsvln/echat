using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Crypto;

public class KeyVerificationService
{
    private readonly ILogger<KeyVerificationService> _logger;
    private readonly PgpService _pgpService;
    
    public KeyVerificationService(ILogger<KeyVerificationService> logger, PgpService pgpService)
    {
        _logger = logger;
        _pgpService = pgpService;
    }
    
    public string GenerateVerificationQrData(string email, string publicKey)
    {
        var fingerprint = _pgpService.GetFingerprint(publicKey);
        
        var payload = new
        {
            email,
            fingerprint,
            key = publicKey
        };
        
        return JsonSerializer.Serialize(payload);
    }
    
    public (string email, string fingerprint, string publicKey) ParseVerificationQrData(string qrData)
    {
        var data = JsonSerializer.Deserialize<JsonElement>(qrData);
        
        return (
            data.GetProperty("email").GetString()!,
            data.GetProperty("fingerprint").GetString()!,
            data.GetProperty("key").GetString()!
        );
    }
    
    public string GenerateVerificationCode(string fingerprint)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fingerprint));
        
        var code = BitConverter.ToUInt64(hash, 0) % 100000_00000;
        
        return $"{code / 100000:D5}-{code % 100000:D5}";
    }
    
    public bool VerifyFingerprint(string publicKey, string expectedFingerprint)
    {
        var actualFingerprint = _pgpService.GetFingerprint(publicKey);
        
        return string.Equals(actualFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase);
    }
}