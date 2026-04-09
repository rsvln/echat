using MimeKit;
using EChat.Core.Models;
using EChat.Core.Crypto;
using Microsoft.Extensions.Logging;
using System.Text;

namespace EChat.Core.Protocol;

public class ChatMessageBuilder
{
    private readonly AccountConfig _accountConfig;
    private readonly PgpService _pgpService;
    private readonly ILogger<ChatMessageBuilder> _logger;

    public ChatMessageBuilder(AccountConfig accountConfig, PgpService pgpService, ILogger<ChatMessageBuilder> logger)
    {
        _accountConfig = accountConfig;
        _pgpService = pgpService;
        _logger = logger;
    }

    // ── Unencrypted single message (used for batch items and fallback) ────────

    public MimeMessage BuildSingle(OutgoingMessage message)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(_accountConfig.Email, _accountConfig.Email));
        foreach (var recipient in message.Recipients)
            email.To.Add(new MailboxAddress("", recipient));
        email.Subject = "[eChat]";

        AddOuterChatHeaders(email, message);

        if (_accountConfig.PublicKey != null)
            email.Headers.Add("Autocrypt", $"addr={_accountConfig.Email}; keydata={_accountConfig.PublicKey}");

        email.Body = BuildBody(message);
        return email;
    }

    // ── Encrypted single message ──────────────────────────────────────────────

    public async Task<MimeMessage> BuildSingleAsync(OutgoingMessage message)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(_accountConfig.Email, _accountConfig.Email));
        foreach (var recipient in message.Recipients)
            email.To.Add(new MailboxAddress("", recipient));
        email.Subject = "[eChat]";

        // Autocrypt key advertisement — always in outer headers
        if (_accountConfig.PublicKey != null)
            email.Headers.Add("Autocrypt", $"addr={_accountConfig.Email}; keydata={_accountConfig.PublicKey}");

        email.Headers.Add("Chat-Version", "2.0");

        // Chat-Group-ID must stay in outer headers so the receiver knows
        // which group private key to use for decryption BEFORE decrypting.
        if (message.GroupId != null)
            email.Headers.Add("Chat-Group-ID", message.GroupId);

        if (message.RecipientPublicKey != null && message.Encrypt)
        {
            try
            {
                // All metadata goes INSIDE the encrypted body — invisible to mail providers
                var plainText = ExtractPlainText(BuildBody(message));
                var innerContent = BuildInnerContent(message, plainText);

                // For 1:1 messages, encrypt for BOTH the recipient AND ourselves,
                // so self-copy can be decrypted by other devices of the same account.
                // For group messages, only the group key is needed — all members
                // already have the group private key.
                var pubKeys = new List<string> { message.RecipientPublicKey };
                if (message.GroupId == null && !string.IsNullOrEmpty(_accountConfig.PublicKey))
                    pubKeys.Add(_accountConfig.PublicKey);

                var encrypted = await _pgpService.EncryptAsync(innerContent, pubKeys);

                email.Body = new TextPart("plain") { Text = encrypted };
                email.Headers.Add("Chat-Encryption", "pgp-inline");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Encryption failed — message will NOT be sent as plaintext");
                throw; // Do NOT fallback to unencrypted — fail the send instead
            }
        }
        else
        {
            // No recipient key — send unencrypted, metadata stays in outer headers
            AddOuterChatHeaders(email, message);
            email.Body = BuildBody(message);
        }

        return email;
    }

    // ── Batch envelope (wraps multiple BuildSingle items) ────────────────────

    public MimeMessage BuildBatch(List<OutgoingMessage> messages, BatchTier tier)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(_accountConfig.Email, _accountConfig.Email));

        // All messages in a batch share the same recipients (enforced by BatchKey).
        // Use the first message's recipients + self for cross-device sync.
        var firstMsg = messages.First();
        var allRecipients = new HashSet<string>(firstMsg.Recipients, StringComparer.OrdinalIgnoreCase);
        var selfEmail = _accountConfig.Email;
        if (!string.IsNullOrEmpty(selfEmail))
            allRecipients.Add(selfEmail);

        foreach (var recipient in allRecipients)
            email.To.Add(new MailboxAddress("", recipient));

        email.Subject = "[eChat]";
        email.Headers.Add("Chat-Version", "2.0-batching");
        email.Headers.Add("Chat-Batch", "true");
        email.Headers.Add("Chat-Batch-Count", messages.Count.ToString());
        email.Headers.Add("Chat-Batch-Tier", tier.ToString().ToLowerInvariant());

        if (_accountConfig.PublicKey != null)
            email.Headers.Add("Autocrypt", $"addr={_accountConfig.Email}; keydata={_accountConfig.PublicKey}");

        var multipart = new Multipart("mixed");
        for (int i = 0; i < messages.Count; i++)
        {
            var nestedEmail = BuildSingle(messages[i]);
            nestedEmail.Headers.Add("Chat-Batch-Item-Index", i.ToString());
            multipart.Add(new MessagePart { Message = nestedEmail });
        }

        email.Body = multipart;
        return email;
    }

    // ── Inner content (embedded inside encrypted body) ────────────────────────

    /// <summary>
    /// Builds the plaintext that will be PGP-encrypted.
    /// Format: Chat-* header lines, blank line, then the actual message text.
    /// The receiver parses these inner headers after decryption.
    /// </summary>
    private string BuildInnerContent(OutgoingMessage message, string bodyText)
    {
        var sb = new StringBuilder();
        sb.Append("Chat-Message-ID: ").AppendLine(message.MessageId);
        sb.Append("Chat-Timestamp: ").AppendLine(message.Timestamp.ToString("O"));

        if (message.InReplyTo != null)
            sb.Append("Chat-In-Reply-To: ").AppendLine(message.InReplyTo);

        switch (message.Type)
        {
            case MessageType.Reaction:
                sb.Append("Chat-Reaction: ").AppendLine(message.Reaction!);
                sb.Append("Chat-Reaction-To: ").AppendLine(message.ReactionTo!);
                break;
            case MessageType.Edit:
                sb.Append("Chat-Edit-Of: ").AppendLine(message.EditOf!);
                sb.Append("Chat-Edit-Version: ").AppendLine((message.EditVersion ?? 1).ToString());
                break;
            case MessageType.Delete:
                sb.Append("Chat-Delete-Of: ").AppendLine(message.DeleteOf!);
                break;
            case MessageType.ReadReceipt:
                sb.AppendLine("Chat-Disposition: read-notification");
                if (message.ReadOf?.Count > 0)
                    sb.Append("Chat-Read-Of: ").AppendLine(string.Join(",", message.ReadOf));
                break;
            case MessageType.System:
                if (message.SystemType != null)
                    sb.Append("Chat-System-Type: ").AppendLine(message.SystemType);
                break;
        }

        if (!string.IsNullOrEmpty(message.SyncType))
            sb.Append("Chat-Sync-Type: ").AppendLine(message.SyncType);
        if (!string.IsNullOrEmpty(message.SyncDeviceId))
            sb.Append("Chat-Sync-Device-ID: ").AppendLine(message.SyncDeviceId);

        sb.AppendLine(); // blank line — MIME-style separator between headers and body
        sb.Append(bodyText);
        return sb.ToString();
    }

    // ── Outer headers (used for unencrypted / batch items) ───────────────────

    private void AddOuterChatHeaders(MimeMessage email, OutgoingMessage message)
    {
        email.Headers.Add("Chat-Version", "2.0");
        email.Headers.Add("Chat-Message-ID", message.MessageId);
        email.Headers.Add("Chat-Timestamp", message.Timestamp.ToString("O"));

        if (message.InReplyTo != null)
            email.Headers.Add("In-Reply-To", message.InReplyTo);

        if (message.GroupId != null)
            email.Headers.Add("Chat-Group-ID", message.GroupId);

        if (!string.IsNullOrEmpty(message.SyncType))
            email.Headers.Add("Chat-Sync-Type", message.SyncType);
        if (!string.IsNullOrEmpty(message.SyncDeviceId))
            email.Headers.Add("Chat-Sync-Device-ID", message.SyncDeviceId);

        switch (message.Type)
        {
            case MessageType.Reaction:
                email.Headers.Add("Chat-Reaction", message.Reaction!);
                email.Headers.Add("Chat-Reaction-To", message.ReactionTo!);
                break;
            case MessageType.Edit:
                email.Headers.Add("Chat-Edit-Of", message.EditOf!);
                email.Headers.Add("Chat-Edit-Version", (message.EditVersion ?? 1).ToString());
                break;
            case MessageType.Delete:
                email.Headers.Add("Chat-Delete-Of", message.DeleteOf!);
                break;
            case MessageType.ReadReceipt:
                email.Headers.Add("Chat-Disposition", "read-notification");
                if (message.ReadOf?.Count > 0)
                    email.Headers.Add("Chat-Read-Of", string.Join(",", message.ReadOf));
                break;
            case MessageType.System:
                if (message.SystemType != null)
                    email.Headers.Add("Chat-System-Type", message.SystemType);
                break;
        }
    }

    // ── Body helpers ─────────────────────────────────────────────────────────

    private MimeEntity BuildBody(OutgoingMessage message)
    {
        if (message.Attachments == null || message.Attachments.Count == 0)
            return new TextPart("plain") { Text = message.Content };

        var multipart = new Multipart("mixed");
        multipart.Add(new TextPart("plain") { Text = message.Content });

        foreach (var attachment in message.Attachments)
        {
            multipart.Add(new MimePart(attachment.ContentType)
            {
                Content = new MimeContent(new MemoryStream(attachment.Data)),
                FileName = attachment.FileName,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
            });
        }

        return multipart;
    }

    private static string ExtractPlainText(MimeEntity body)
    {
        if (body is TextPart textPart) return textPart.Text;
        if (body is Multipart mp)
        {
            foreach (var part in mp)
                if (part is TextPart t && t.IsPlain) return t.Text;
        }
        return string.Empty;
    }
}
