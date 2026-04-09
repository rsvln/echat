namespace EChat.Core.Services;

/// <summary>Simple in-process event bus that notifies UI components when a chat is updated.</summary>
public class ChatEventService
{
    /// <summary>Fired whenever new messages are saved for a chat. Argument is the ChatId.</summary>
    public event Action<string>? ChatUpdated;

    public void NotifyChatUpdated(string chatId) => ChatUpdated?.Invoke(chatId);

    /// <summary>Fired when the user switches active accounts. Arguments: (oldAccountId, newAccountId).</summary>
    public event Action<string, string>? AccountSwitched;

    public void NotifyAccountSwitched(string oldAccountId, string newAccountId) =>
        AccountSwitched?.Invoke(oldAccountId, newAccountId);
}
