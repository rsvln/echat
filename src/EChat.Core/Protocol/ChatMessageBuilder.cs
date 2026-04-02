using MimeKit;
using EChat.Core.Models;
using EChat.Core.Crypto;
using Microsoft.Extensions.Logging;

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

    public MimeMessage BuildSingle(OutgoingMessage message)
    {
        var email = new MimeMessage();

        email.From.Add(new MailboxAddress(_accountConfig.Email, _accountConfig.Email));
        foreach (var recipient in message.Recipients)
            email.To.Add(new MailboxAddress("", recipient));

        email.Subject = BuildSubject(message);

        AddChatHeaders(email, message);

        // Always advertise our public key so the other party can start encrypting to us
        if (_accountConfig.PublicKey != null)
            email.Headers.Add("Autocrypt", $"addr={_accountConfig.Email}; keydata={_accountConfig.PublicKey}");

        email.Body = BuildBody(message);

        return email;
    }

    public async Task<MimeMessage> BuildSingleAsync(OutgoingMessage message)
    {
        var email = BuildSingle(message);

        // Encrypt body if the recipient's public key is known
        if (message.RecipientPublicKey != null && message.Encrypt)
        {
            try
            {
                var plainText = ExtractPlainText(email.Body);
                var encrypted = await _pgpService.EncryptAsync(plainText, message.RecipientPublicKey);

                email.Body = new TextPart("plain") { Text = encrypted };
                // Mark as encrypted so the receiver knows to decrypt
                email.Headers.Remove("Chat-Encryption");
                email.Headers.Add("Chat-Encryption", "pgp-inline");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Encryption failed, sending unencrypted");
            }
        }

        return email;
    }

    public MimeMessage BuildBatch(List<OutgoingMessage> messages, BatchTier tier)
    {
        var email = new MimeMessage();

        var firstMsg = messages.First();
        email.From.Add(new MailboxAddress(_accountConfig.Email, _accountConfig.Email));
        foreach (var recipient in firstMsg.Recipients)
            email.To.Add(new MailboxAddress("", recipient));

        email.Subject = "[Chat Batch]";
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

    private void AddChatHeaders(MimeMessage email, OutgoingMessage message)
    {
        email.Headers.Add("Chat-Version", "2.0-batching");
        email.Headers.Add("Chat-Message-ID", message.MessageId);
        email.Headers.Add("Chat-Timestamp", message.Timestamp.ToString("O"));

        if (message.InReplyTo != null)
            email.Headers.Add("In-Reply-To", message.InReplyTo);

        if (message.GroupId != null)
            email.Headers.Add("Chat-Group-ID", message.GroupId);

        if (message.Encrypt && message.RecipientPublicKey != null)
            email.Headers.Add("Chat-Encryption", "pgp-inline");

        switch (message.Type)
        {
            case MessageType.Reaction:
                email.Headers.Add("Chat-Reaction", message.Reaction!);
                email.Headers.Add("Chat-Reaction-To", message.ReactionTo!);
                break;
            case MessageType.Edit:
                email.Headers.Add("Chat-Edit-Of", message.EditOf!);
                email.Headers.Add("Chat-Edit-Version", message.EditVersion!.ToString());
                break;
            case MessageType.Delete:
                email.Headers.Add("Chat-Delete-Of", message.DeleteOf!);
                break;
            case MessageType.ReadReceipt:
                email.Headers.Add("Chat-Disposition", "read-notification");
                if (message.ReadOf != null && message.ReadOf.Count > 0)
                    email.Headers.Add("Chat-Read-Of", string.Join(",", message.ReadOf));
                break;
        }
    }

    private MimeEntity BuildBody(OutgoingMessage message)
    {
        if (message.Attachments == null || message.Attachments.Count == 0)
            return new TextPart("plain") { Text = message.Content };

        var multipart = new Multipart("mixed");
        multipart.Add(new TextPart("plain") { Text = message.Content });

        foreach (var attachment in message.Attachments)
        {
            var mimePart = new MimePart(attachment.ContentType)
            {
                Content = new MimeContent(new MemoryStream(attachment.Data)),
                FileName = attachment.FileName,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
            };
            multipart.Add(mimePart);
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

    private string BuildSubject(OutgoingMessage message)
    {
        if (message.Subject != null) return message.Subject;
        if (message.GroupId != null) return "[eChat] Group Chat";
        return "[eChat]";
    }
}
