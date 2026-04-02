namespace EChat.Core.Models;

public class Contact
{
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? PublicKey { get; set; }
    public string? KeyFingerprint { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string? ProtocolVersion { get; set; }
    public bool SupportsBatching { get; set; }
}