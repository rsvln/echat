namespace EChat.Core.Models;

public class EmailAccountConfig
{
    public required string Email { get; set; }
    public required string ImapServer { get; set; }
    public int ImapPort { get; set; } = 993;
    public required string SmtpServer { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public int MaxAttachmentSizeMb { get; set; } = 25;
    public string? DisplayName { get; set; }
    
    // Folder & subject settings
    public ChatSettings ChatSettings { get; set; } = new();
}