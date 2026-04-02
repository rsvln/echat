namespace EChat.Core.Models;

public class Account
{
    public string AccountId { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string ImapServer { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;

    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;

    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Avatar
    public string? AvatarColor { get; set; }

    // Invite / key exchange
    public string? InviteToken { get; set; }
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }
    public string? KeyFingerprint { get; set; }
}
