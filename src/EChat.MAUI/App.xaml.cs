using EChat.Core;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Transport;
using EChat.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EChat.Maui;

public partial class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
#if WINDOWS
        try
        {
            window.TitleBar = new TitleBar
            {
                Icon = ImageSource.FromFile("echat_icon.png"),
                Title = "εChat"
            };
        }
        catch { /* TitleBar not supported on this OS version — fall back to default title bar */ }
#endif
        return window;
    }

    public App(IServiceProvider serviceProvider)
    {
        InitializeComponent();

#pragma warning disable CS0618
        MainPage = new MainPage();
#pragma warning restore CS0618

        _ = Task.Run(async () =>
        {
            await serviceProvider.InitializeEChatDatabaseAsync();

            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            var accounts = await db.Accounts.ToListAsync();
            var account = accounts.FirstOrDefault(a => a.IsActive) ?? accounts.FirstOrDefault();
            if (account == null) return;

            var deviceId = Microsoft.Maui.Storage.Preferences.Get("device_id", string.Empty);

            var userCtx = serviceProvider.GetRequiredService<UserContextService>();
            userCtx.Initialize(account.AccountId, account.Email, deviceId);

            // Load sync settings for this account
            var syncEngine = serviceProvider.GetRequiredService<EChat.Core.Sync.SyncEngine>();
            await syncEngine.LoadSettingsAsync(account.AccountId);

            var transport = serviceProvider.GetRequiredService<EmailTransportService>();
            var incomingMessages = serviceProvider.GetRequiredService<IncomingMessageService>();
            var accountConfig = serviceProvider.GetRequiredService<AccountConfig>();
            var multiImap = serviceProvider.GetRequiredService<MultiAccountImapManager>();

            // Active account — EmailTransportService handles IMAP + SMTP
            transport.MessagesReceived += async (messages) =>
                await incomingMessages.SaveAsync(accountConfig.AccountId, messages);

            // Background accounts — MultiAccountImapManager handles IMAP only
            multiImap.MessagesReceived += async (accountId, messages) =>
                await incomingMessages.SaveAsync(accountId, messages);

            try
            {
                await transport.ReconnectAsync(account, deviceId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[eChat] Transport connect failed: {ex.Message}");
            }

            // Start IMAP workers for all non-active accounts
            await multiImap.StartBackgroundAccountsAsync(accounts, account.AccountId);
        });
    }
}
