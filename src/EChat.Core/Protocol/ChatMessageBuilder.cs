using MimeKit;
using EChat.Core.Models;
using EChat.Core.Crypto;
using EChat.Core.Services;
using System.Text;
using System.Linq;

namespace EChat.Core.Protocol;

public class ChatMessageBuilder
{
    private readonly AccountConfig _accountConfig;
    private readonly PgpService _pgpService;
    private readonly FileLogger _fileLogger;

    public ChatMessageBuilder(AccountConfig accountConfig, PgpService pgpService, FileLogger fileLogger)
    {
        _accountConfig = accountConfig;
        _pgpService = pgpService;
        _fileLogger = fileLogger;
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

        // Autocrypt key advertisement — skip for invite messages (pubKey is encrypted instead)
        bool isInviteMessage = !string.IsNullOrEmpty(message.InviteToken);
        if (_accountConfig.PublicKey != null && !isInviteMessage)
            email.Headers.Add("Autocrypt", $"addr={_accountConfig.Email}; keydata={_accountConfig.PublicKey}");

        email.Headers.Add("Chat-Version", "2.0");

        // Invite token — outer headers so Alice can verify BEFORE decrypting.
        // pubKey is encrypted with AES-256-GCM(key=SHA256(token)) — only Alice can decrypt.
        if (isInviteMessage && !string.IsNullOrEmpty(_accountConfig.PublicKey))
        {
            var enc = InviteService.EncryptPubKey(_accountConfig.PublicKey, message.InviteToken!);
            email.Headers.Add("Chat-Invite-Token",           message.InviteToken);
            email.Headers.Add("Initial-Contact-Key-Exchange", enc);
        }

        // Chat-Group-ID must stay in outer headers so the receiver knows
        // which group private key to use for decryption BEFORE decrypting.
        if (message.GroupId != null)
            email.Headers.Add("Chat-Group-ID", message.GroupId);

        // Chat-System-Type must be in outer headers even for encrypted messages so that
        // sync copies arriving at the sender's own device are correctly identified as system
        // messages and not treated as regular chat messages (which would render as a PGP blob).
        if (message.Type == MessageType.System && message.SystemType != null)
            email.Headers.Add("Chat-System-Type", message.SystemType);

        if (message.RecipientPublicKey != null && message.Encrypt)
        {
            try
            {
                // All metadata goes INSIDE the encrypted body — invisible to mail providers
                var plainText = ExtractPlainText(BuildBody(message));
                var innerContent = BuildInnerContent(message, plainText);

                // Always encrypt for self so sync copies arriving on other devices can be decrypted:
                //   - 1:1 messages: no GroupId — always add self.
                //   - System messages (group-create, group-member-add, group-member-remove, etc.):
                //     sent individually with the recipient's personal key, so self must be added too.
                //   - Regular group messages: GroupId set, not System — the group private key is shared
                //     with all members (including sender), so self-CC is already decryptable; don't add self.
                var pubKeys = new List<string> { message.RecipientPublicKey };
                bool isRegularGroupMessage = message.GroupId != null && message.Type != MessageType.System;
                if (!isRegularGroupMessage && !string.IsNullOrEmpty(_accountConfig.PublicKey))
                    pubKeys.Add(_accountConfig.PublicKey);

                _fileLogger.Write("DEBUG", "ChatMessageBuilder", $"Encrypting for {pubKeys.Count} key(s), recipientKey length={message.RecipientPublicKey?.Length ?? 0}, selfKey length={_accountConfig.PublicKey?.Length ?? 0}, groupId={message.GroupId}, type={message.Type}");
                var encrypted = await _pgpService.EncryptAsync(innerContent, pubKeys);

                email.Body = new TextPart("plain") { Text = encrypted };
                email.Headers.Add("Chat-Encryption", "pgp-inline");
            }
            catch (Exception ex)
            {
                _fileLogger.Write("ERROR", "ChatMessageBuilder", $"Encryption failed — message will NOT be sent as plaintext: {ex.Message}");
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

        sb.AppendLine(); // blank line — MIME-style separator between headers and body
        sb.Append(bodyText);

        // Embed binary attachments as base64 sections so they survive PGP encryption
        if (message.Attachments != null)
        {
            foreach (var att in message.Attachments)
            {
                sb.AppendLine();
                sb.AppendLine("--echat-att--");
                sb.Append("Content-Type: ").AppendLine(att.ContentType);
                sb.Append("Content-Filename: ").AppendLine(att.FileName);
                sb.Append("Content-Size: ").AppendLine(att.Data.Length.ToString());
                sb.AppendLine();
                sb.AppendLine(Convert.ToBase64String(att.Data));
                sb.AppendLine("--echat-att-end--");
            }
        }

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
