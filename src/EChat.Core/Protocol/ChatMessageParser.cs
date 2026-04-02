using MimeKit;
using MimeKit.Cryptography;

namespace EChat.Core.Protocol;

public class ChatMessageParser
{
    public List<ParsedMessage> Parse(MimeMessage email)
    {
        var headers = ParseHeaders(email);
        
        if (headers.IsBatch)
        {
            return ParseBatch(email);
        }
        
        return new List<ParsedMessage> { ParseSingle(email, headers) };
    }
    
    private ChatHeaders ParseHeaders(MimeMessage email)
    {
        var headers = new ChatHeaders
        {
            MessageId = email.Headers["Chat-Message-ID"] ?? email.MessageId,
            Timestamp = ParseTimestamp(email.Headers["Chat-Timestamp"]) ?? email.Date
        };
        
        headers.Version = email.Headers["Chat-Version"] ?? "1.0";
        headers.InReplyTo = email.Headers["In-Reply-To"];
        headers.Sequence = ParseInt(email.Headers["Chat-Sequence"]);
        
        headers.IsBatch = email.Headers["Chat-Batch"] == "true";
        headers.BatchCount = ParseInt(email.Headers["Chat-Batch-Count"]);
        headers.BatchTier = email.Headers["Chat-Batch-Tier"];
        headers.BatchItemIndex = ParseInt(email.Headers["Chat-Batch-Item-Index"]);
        
        headers.GroupId = email.Headers["Chat-Group-ID"];
        headers.GroupVersion = ParseInt(email.Headers["Chat-Group-Version"]);
        headers.GroupName = email.Headers["Chat-Group-Name"];
        headers.GroupMembers = ParseList(email.Headers["Chat-Group-Members"]);
        headers.GroupAdmins = ParseList(email.Headers["Chat-Group-Admins"]);
        headers.GroupOperation = email.Headers["Chat-Group-Operation"];
        headers.GroupOperationActor = email.Headers["Chat-Group-Operation-Actor"];
        headers.GroupOperationTarget = email.Headers["Chat-Group-Operation-Target"];
        
        headers.Encryption = email.Headers["Chat-Encryption"];
        headers.KeyFingerprint = email.Headers["Chat-Key-Fingerprint"];
        
        headers.Disposition = email.Headers["Chat-Disposition"];
        headers.DispositionId = email.Headers["Chat-Disposition-ID"];
        headers.Reaction = email.Headers["Chat-Reaction"];
        headers.ReactionTo = email.Headers["Chat-Reaction-To"];
        headers.EditOf = email.Headers["Chat-Edit-Of"];
        headers.EditVersion = ParseInt(email.Headers["Chat-Edit-Version"]);
        headers.DeleteOf = email.Headers["Chat-Delete-Of"];
        headers.ReadOf = ParseList(email.Headers["Chat-Read-Of"]);

        headers.SyncType = email.Headers["Chat-Sync-Type"];
        headers.SyncDeviceId = email.Headers["Chat-Sync-Device-ID"];
        
        return headers;
    }
    
    private ParsedMessage ParseSingle(MimeMessage email, ChatHeaders headers)
    {
        var content = ExtractContent(email.Body);
        var attachments = ExtractAttachments(email);
        
        return new ParsedMessage
        {
            Headers = headers,
            Content = content,
            Attachments = attachments,
            IsEncrypted = email.Body is MultipartEncrypted,
            Sender = email.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            Recipients = email.To.Mailboxes.Select(m => m.Address).ToList()
        };
    }
    
    private List<ParsedMessage> ParseBatch(MimeMessage email)
    {
        var messages = new List<ParsedMessage>();
        
        if (email.Body is not Multipart multipart) return messages;
        
        foreach (var part in multipart)
        {
            if (part is MessagePart msgPart)
            {
                var nestedHeaders = ParseHeaders(msgPart.Message);
                messages.Add(ParseSingle(msgPart.Message, nestedHeaders));
            }
        }
        
        return messages;
    }
    
    private string ExtractContent(MimeEntity body)
    {
        if (body is TextPart textPart)
            return textPart.Text;
            
        if (body is Multipart multipart)
        {
            foreach (var part in multipart)
            {
                if (part is TextPart text && text.IsPlain)
                    return text.Text;
            }
        }
        
        return string.Empty;
    }
    
    private List<AttachmentInfo>? ExtractAttachments(MimeMessage email)
    {
        var attachments = new List<AttachmentInfo>();
        
        foreach (var attachment in email.Attachments)
        {
            if (attachment is MimePart mimePart)
            {
                using var stream = new MemoryStream();
                mimePart.Content.DecodeTo(stream);
                
                attachments.Add(new AttachmentInfo
                {
                    FileName = mimePart.FileName ?? "unnamed",
                    ContentType = mimePart.ContentType.MimeType,
                    Size = stream.Length,
                    Data = stream.ToArray()
                });
            }
        }
        
        return attachments.Count > 0 ? attachments : null;
    }
    
    private DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }
    
    private int? ParseInt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return int.TryParse(value, out var result) ? result : null;
    }
    
    private List<string>? ParseList(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToList();
    }
}