using EChat.Core.Models;

namespace EChat.Core.Protocol;

public class OutgoingMessage
{
    public required string MessageId { get; set; }
    public required string Content { get; set; }
    public required List<string> Recipients { get; set; }
    public string? GroupId { get; set; }
    public BatchTier Tier { get; set; } = BatchTier.Immediate;
    public DateTimeOffset Timestamp { get; set; }
    public string? InReplyTo { get; set; }
    public List<AttachmentInfo>? Attachments { get; set; }
    public bool Encrypt { get; set; } = true;
    /// <summary>Recipient's base64 public key. If set, the message body will be encrypted.</summary>
    public string? RecipientPublicKey { get; set; }
    public MessageType Type { get; set; } = MessageType.Regular;
    /// <summary>Pre-built email subject line. If null, ChatMessageBuilder uses its default logic.</summary>
    public string? Subject { get; set; }

    // For system messages
    public string? Reaction { get; set; }
    public string? ReactionTo { get; set; }
    public string? EditOf { get; set; }
    public int? EditVersion { get; set; }
    public string? DeleteOf { get; set; }
    public List<string>? ReadOf { get; set; }  // MessageIds confirmed read (ReadReceipt type)
    public string? SystemType { get; set; }

    // Device sync headers
    public string? SyncType { get; set; }
    public string? SyncDeviceId { get; set; }
}

public enum MessageType
{
    Regular,
    Reaction,
    Edit,
    Delete,
    ReadReceipt,
    DeliveryReceipt,
    System
}