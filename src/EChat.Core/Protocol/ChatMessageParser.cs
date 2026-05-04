using MimeKit;
using MimeKit.Cryptography;

namespace EChat.Core.Protocol;

public class ChatMessageParser
{
    private string _myEmail;

    public ChatMessageParser(string myEmail = "")
    {
        _myEmail = myEmail;
    }

    public List<ParsedMessage> Parse(MimeMessage email)
    {
        var headers = ParseHeaders(email);

        if (headers.IsBatch)
        {
            return ParseBatch(email);
        }

        return new List<ParsedMessage> { ParseSingle(email, headers) };
    }

    public void SetMyEmail(string email)
    {
        _myEmail = email;
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

        headers.SystemType = email.Headers["Chat-System-Type"];
        
        return headers;
    }
    
    private ParsedMessage ParseSingle(MimeMessage email, ChatHeaders headers)
    {
        var content = ExtractContent(email.Body);
        var attachments = ExtractAttachments(email);
        System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] ParseSingle: msgId={headers.MessageId}, contentLen={content?.Length ?? -1}, attachments={attachments?.Count ?? -1}, encryption={headers.Encryption}");
        
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
        var count = email.Attachments.Count();
        System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] ExtractAttachments: count={count}");
        
        foreach (var attachment in email.Attachments)
        {
            if (attachment is MimePart mimePart)
            {
                using var stream = new MemoryStream();
                mimePart.Content.DecodeTo(stream);
                
                System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] MimePart extracted: filename={mimePart.FileName}, size={stream.Length}");
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

    /// <summary>
    /// After PGP decryption, parses the inner header block from the decrypted text
    /// and applies the values to the already-created ParsedMessage.
    /// Format: "Chat-X: value\n…\n\nbody text"
    /// Inner headers always take precedence over outer email headers.
    /// </summary>
    public void ApplyDecryptedContent(ParsedMessage msg, string decryptedText)
    {
        // Normalise line endings
        var lines = decryptedText.Replace("\r\n", "\n").Split('\n');

        var innerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int contentStart = lines.Length; // default: no body

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line))
            {
                contentStart = i + 1;
                break;
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0 && line.StartsWith("Chat-", StringComparison.OrdinalIgnoreCase))
                innerHeaders[line[..colonIdx].Trim()] = line[(colonIdx + 1)..].Trim();
        }

        // If there were no inner headers at all, treat the whole text as body
        if (innerHeaders.Count == 0)
        {
            msg.Content = decryptedText;
            return;
        }

        // Override headers from inner block
        if (innerHeaders.TryGetValue("Chat-Message-ID", out var mid) && !string.IsNullOrEmpty(mid))
            msg.Headers.MessageId = mid;

        if (innerHeaders.TryGetValue("Chat-Timestamp", out var ts)
            && DateTimeOffset.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
            msg.Headers.Timestamp = dto;

        if (innerHeaders.TryGetValue("Chat-In-Reply-To", out var irt))
            msg.Headers.InReplyTo = irt;

        if (innerHeaders.TryGetValue("Chat-System-Type", out var st))
            msg.Headers.SystemType = st;

        if (innerHeaders.TryGetValue("Chat-Reaction", out var rxn))
            msg.Headers.Reaction = rxn;

        if (innerHeaders.TryGetValue("Chat-Reaction-To", out var rxnt))
            msg.Headers.ReactionTo = rxnt;

        if (innerHeaders.TryGetValue("Chat-Edit-Of", out var eof))
            msg.Headers.EditOf = eof;

        if (innerHeaders.TryGetValue("Chat-Edit-Version", out var evs)
            && int.TryParse(evs, out var evi))
            msg.Headers.EditVersion = evi;

        if (innerHeaders.TryGetValue("Chat-Delete-Of", out var dof))
            msg.Headers.DeleteOf = dof;

        if (innerHeaders.TryGetValue("Chat-Disposition", out var disp))
            msg.Headers.Disposition = disp;

        if (innerHeaders.TryGetValue("Chat-Read-Of", out var rof))
            msg.Headers.ReadOf = rof
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();

        if (innerHeaders.TryGetValue("Chat-Sync-Type", out var syncType))
            msg.Headers.SyncType = syncType;
        if (innerHeaders.TryGetValue("Chat-Sync-Device-ID", out var syncDevId))
            msg.Headers.SyncDeviceId = syncDevId;

        // Body is everything after the blank separator line
        var fullBody = contentStart < lines.Length
            ? string.Join('\n', lines[contentStart..])
            : string.Empty;

        // Extract attachment sections embedded by BuildInnerContent (encrypted messages)
        const string attBegin = "--echat-att--";
        const string attEnd   = "--echat-att-end--";
        var firstAtt = fullBody.IndexOf(attBegin, StringComparison.Ordinal);
        System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] ApplyDecryptedContent: msgId={msg.Headers.MessageId}, fullBodyLen={fullBody.Length}, firstAtt={firstAtt}");
        if (firstAtt >= 0)
        {
            msg.Content = fullBody[..firstAtt].TrimEnd('\n', '\r');
            System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] Content set, remaining length: {fullBody.Length - firstAtt}");
            var attachments = new List<AttachmentInfo>();
            var pos = firstAtt;
            while (pos < fullBody.Length)
            {
                var begin = fullBody.IndexOf(attBegin, pos, StringComparison.Ordinal);
                if (begin < 0) break;
                var endPos = fullBody.IndexOf(attEnd, begin, StringComparison.Ordinal);
                var actualBlockLen = endPos > begin ? endPos - begin - attBegin.Length : -1;
                System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] Att block: begin={begin}, endPos={endPos}, blockLen={actualBlockLen}");
                if (endPos < 0) break;

                // Parse headers within the attachment block
                var block = fullBody[(begin + attBegin.Length)..endPos];
                System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] Block raw (len={block.Length}): {(block.Length > 200 ? block[..200] + "..." : block)}");
                var blockLines = block.Replace("\r\n", "\n").Split('\n');
                System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] BlockLines count={blockLines.Length}, lines: {string.Join("|", blockLines.Take(5))}");
                var attHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                
                // First 3 lines are headers (Content-Type, Content-Filename, Content-Size)
                for (int k = 0; k < Math.Min(3, blockLines.Length); k++)
                {
                    var colon = blockLines[k].IndexOf(':');
                    if (colon > 0)
                        attHeaders[blockLines[k][..colon].Trim()] = blockLines[k][(colon + 1)..].Trim();
                }
                
                // Find actual data start - look for first line without colon (after headers)
                // Or use index 4+ (after expected empty line after Content-Size)
                int dataStart = 4;
                for (int k = 3; k < blockLines.Length; k++)
                {
                    if (string.IsNullOrWhiteSpace(blockLines[k])) continue; // skip empty lines
                    if (blockLines[k].IndexOf(':') < 0) { dataStart = k; break; } // first non-header line = data
                    dataStart = k + 1;
                }

                var base64Lines = blockLines[dataStart..];
                if (base64Lines.Length == 0 || string.IsNullOrWhiteSpace(string.Join("", base64Lines)))
                {
                    System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] Attachment block has no data. Filename: {attHeaders.GetValueOrDefault("Content-Filename", "unknown")}, dataStart: {dataStart}, blockLinesCount: {blockLines.Length}");
                }
                else
                {
                    var base64 = string.Join("", base64Lines).Trim();
                    if (string.IsNullOrEmpty(base64))
                    {
                        System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] Base64 is empty after join. Filename: {attHeaders.GetValueOrDefault("Content-Filename", "unknown")}");
                    }
                    else
                    {
                        try
                        {
                            var cleanBase64 = new string(base64.Where(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=').ToArray());
                            var data = Convert.FromBase64String(cleanBase64);
                            System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] Attachment parsed: {attHeaders.GetValueOrDefault("Content-Filename", "unknown")}, size={data.Length}");
                            attachments.Add(new AttachmentInfo
                            {
                                ContentType = attHeaders.GetValueOrDefault("Content-Type", "application/octet-stream"),
                                FileName    = attHeaders.GetValueOrDefault("Content-Filename", "attachment"),
                                Size        = data.Length,
                                Data        = data
                            });
                        }
                        catch (Exception ex)
                        {
                            var preview = base64.Length > 100 ? base64[..100] + "..." : base64;
                            System.Diagnostics.Debug.WriteLine($"[ChatMessageParser] Failed to decode base64 attachment '{attHeaders.GetValueOrDefault("Content-Filename", "unknown")}': {ex.Message}, base64Length: {base64.Length}, preview: {preview}");
                        }
                    }
                }

                pos = endPos + attEnd.Length;
            }
            if (attachments.Count > 0)
                msg.Attachments = attachments;
        }
        else
        {
            msg.Content = fullBody;
        }
    }
}