namespace EChat.Core.Protocol;

public class ParsedMessage
{
    public required ChatHeaders Headers { get; set; }
    public required string Content { get; set; }
    public List<AttachmentInfo>? Attachments { get; set; }
    public bool IsEncrypted { get; set; }
    public required string Sender { get; set; }
    public List<string> Recipients { get; set; } = new();
}

public class AttachmentInfo
{
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required byte[] Data { get; set; }
}