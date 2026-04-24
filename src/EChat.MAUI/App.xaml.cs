using EChat.Core;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Services;
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

#if ANDROID
            // Android NAT/Doze can kill TCP silently. MailKit waits up to Timeout for the
            // server's DONE response after IDLE break — default 5 min is too long.
            // Reduce ImapClient.Timeout to 30 s so a dead connection is detected fast.
            var imapService = serviceProvider.GetRequiredService<EChat.Core.Transport.ImapService>();
            imapService.SetIdleTimeout(TimeSpan.FromSeconds(30));
#endif

            var transport = serviceProvider.GetRequiredService<EmailTransportService>();
            var incomingMessages = serviceProvider.GetRequiredService<IncomingMessageService>();
            var accountConfig = serviceProvider.GetRequiredService<AccountConfig>();
            var multiImap = serviceProvider.GetRequiredService<MultiAccountImapManager>();

            // Subscribe to OS-level notifications FIRST — before ReconnectAsync —
            // so messages processed during the initial sync don't miss the handler.
            var chatEvents = serviceProvider.GetRequiredService<ChatEventService>();
            chatEvents.NewMessageArrived += payload =>
            {
#if ANDROID
                global::Android.Util.Log.Debug("eChat", $"NewMessageArrived: chat={payload.ChatName}, unread={payload.TotalUnread}");
                var ctx = global::Android.App.Application.Context;
                EChat.Maui.Platforms.Android.Services.MessageNotificationHelper.Show(
                    ctx, payload.ChatId, payload.ChatName, payload.Preview, payload.TotalUnread);
#elif WINDOWS
                EChat.Maui.Platforms.Windows.Services.TaskbarFlashHelper.Flash();
                EChat.Maui.Platforms.Windows.Services.TaskbarBadgeHelper.SetBadge(payload.TotalUnread);
#endif
            };

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

#if ANDROID
            // Keep the process alive in background so the IMAP loop isn't killed by Android.
            try
            {
                var ctx = global::Android.App.Application.Context;
                var svcIntent = new global::Android.Content.Intent(ctx,
                    typeof(EChat.Maui.Platforms.Android.Services.EmailSyncService));
                if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
                    ctx.StartForegroundService(svcIntent);
                else
                    ctx.StartService(svcIntent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[eChat] Failed to start EmailSyncService: {ex.Message}");
            }
#endif

            // Start IMAP workers for all non-active accounts
            await multiImap.StartBackgroundAccountsAsync(accounts, account.AccountId);
        });
    }
}
