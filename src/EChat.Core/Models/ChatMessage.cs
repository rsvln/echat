namespace EChat.Core.Models;

public enum MessageStatus
{
    Sending = 0,  // saved locally, SMTP not yet confirmed
    Sent    = 1,  // confirmed sent via SMTP
    Read    = 2,  // recipient opened the chat
    Failed  = 3   // permanent SMTP error (5xx) — will not retry automatically
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string MessageId { get; set; }
    public required string ChatId { get; set; }
    public required string Sender { get; set; }
    public required string Content { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public required DateTimeOffset DisplayTimestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool Encrypted { get; set; }
    public string? AttachmentPath { get; set; }
    public string? InReplyTo { get; set; }
    public bool IsEdited { get; set; }
    public int EditVersion { get; set; }
    public bool ClockSkewDetected { get; set; }
    public bool IsSystem { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Sent;

    /// <summary>IMAP UID of the source email — used to delete the email from the server when the chat is deleted.</summary>
    public long? ImapUid { get; set; }
    /// <summary>IMAP folder the source email lives in (e.g. "eChat").</summary>
    public string? ImapFolder { get; set; }

    // Navigation
    public Chat? Chat { get; set; }
}