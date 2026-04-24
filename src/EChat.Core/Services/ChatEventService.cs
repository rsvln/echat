namespace EChat.Core.Services;

/// <summary>Payload for incoming-message OS notifications.</summary>
/// <param name="ChatId">Chat that received the message.</param>
/// <param name="ChatName">Display name of the chat (contact name or group name).</param>
/// <param name="SenderName">Short display name of the sender.</param>
/// <param name="Preview">Truncated message body (≤80 chars).</param>
/// <param name="TotalUnread">Total unread across all non-muted, non-archived chats.</param>
public sealed record NewMessagePayload(
    string ChatId,
    string ChatName,
    string SenderName,
    string Preview,
    int TotalUnread);

/// <summary>Simple in-process event bus that notifies UI components when a chat is updated.</summary>
public class ChatEventService
{
    /// <summary>Fired whenever new messages are saved for a chat. Argument is the ChatId.</summary>
    public event Action<string>? ChatUpdated;

    public void NotifyChatUpdated(string chatId) => ChatUpdated?.Invoke(chatId);

    /// <summary>
    /// Fired for each chat that received at least one new incoming (non-self) message.
    /// Intended for OS-level push notifications — not fired for muted chats.
    /// </summary>
    public event Action<NewMessagePayload>? NewMessageArrived;

    internal void NotifyNewMessage(NewMessagePayload payload) => NewMessageArrived?.Invoke(payload);

    /// <summary>Fired when the user switches active accounts.</summary>
    public event Action<string, string>? AccountSwitched;

    public void NotifyAccountSwitched(string oldAccountId, string newAccountId) =>
        AccountSwitched?.Invoke(oldAccountId, newAccountId);

    /// <summary>
    /// Fired when SMTP is rate-limited. Argument is the earliest time the app should retry.
    /// UI components use this to display a warning badge on the account.
    /// </summary>
    public event Action<DateTimeOffset>? SmtpRateLimited;

    internal void NotifySmtpRateLimited(DateTimeOffset retryAfter) =>
        SmtpRateLimited?.Invoke(retryAfter);

    /// <summary>Fired when the rate-limit window expires and sending is allowed again.</summary>
    public event Action? SmtpRateLimitCleared;

    internal void NotifySmtpRateLimitCleared() => SmtpRateLimitCleared?.Invoke();
}
