using EChat.Core;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Transport;
using EChat.Maui.Services;
using EChat.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EChat.Maui;

public partial class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
        window.TitleBar = new TitleBar
        {
            Icon = ImageSource.FromFile("echat_icon.png"),
            Title = "EChat"
        };
        return window;
    }

    public App(IServiceProvider serviceProvider)
    {
        InitializeComponent();

        Task.Run(async () => await serviceProvider.InitializeEChatDatabaseAsync())
            .GetAwaiter().GetResult();

        MainPage = new MainPage();

        Task.Run(async () =>
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            var accounts = await db.Accounts.ToListAsync();
            var account = accounts.FirstOrDefault(a => a.IsActive) ?? accounts.FirstOrDefault();
            if (account == null) return;

            var deviceId = Microsoft.Maui.Storage.Preferences.Get("device_id", string.Empty);

            var userCtx = serviceProvider.GetRequiredService<UserContextService>();
            userCtx.Initialize(account.AccountId, account.Email, deviceId);

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
            }

            // Start IMAP workers for all non-active accounts
            await multiImap.StartBackgroundAccountsAsync(accounts, account.AccountId);
        });
    }
}
