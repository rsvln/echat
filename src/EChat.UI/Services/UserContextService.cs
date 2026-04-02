namespace EChat.UI.Services;

public class UserContextService
{
    public string AccountId { get; private set; } = string.Empty;
    public string UserEmail { get; private set; } = string.Empty;
    public string DeviceId { get; private set; } = string.Empty;

    public void Initialize(string accountId, string email, string deviceId)
    {
        AccountId = accountId;
        UserEmail = email;
        DeviceId = deviceId;
    }
}
