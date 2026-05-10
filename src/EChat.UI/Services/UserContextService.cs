namespace EChat.UI.Services;

public class UserContextService
{
    public string AccountId { get; private set; } = string.Empty;
    public string UserEmail { get; private set; } = string.Empty;

    public void Initialize(string accountId, string email)
    {
        AccountId = accountId;
        UserEmail = email;
    }
}
