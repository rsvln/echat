using EChat.Core.Models;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Sync;

public class SyncWarningService
{
    private readonly ILogger<SyncWarningService> _logger;

    public event Action<string>? WarningRaised;
    public event Action<string>? InfoRaised;

    public SyncWarningService(ILogger<SyncWarningService> logger)
    {
        _logger = logger;
    }

    public void ValidateSettings(SyncSettings settings)
    {
        if (!settings.UseImapIdle && settings.PollingInterval > TimeSpan.FromMinutes(30))
        {
            ShowWarning(
                $"Messages may be delayed by up to {settings.PollingInterval.TotalMinutes:F0} minutes. " +
                "Consider enabling IMAP IDLE for important chats.");
        }

        if (settings.LowPriorityBatchWindow > TimeSpan.FromMinutes(10))
        {
            ShowInfo(
                $"Read receipts will be sent with up to {settings.LowPriorityBatchWindow.TotalMinutes:F0} minutes delay.");
        }

        if (settings.Profile == SyncProfile.Manual)
        {
            ShowWarning(
                "Manual sync mode: messages will only arrive when you open the app.");
        }

        if (settings.QuietHours != null &&
            settings.QuietHoursProfile == SyncProfile.Manual)
        {
            ShowInfo(
                $"During quiet hours ({settings.QuietHours.StartHour:00}:{settings.QuietHours.EndHour:00}), " +
                "you will only receive messages when opening the app.");
        }
    }

    public void ValidateChatPriority(ChatPriority priority)
    {
        if (priority == ChatPriority.Muted)
        {
            ShowInfo("Muted chats will only sync when you open them.");
        }
    }

    private void ShowWarning(string message)
    {
        _logger.LogWarning(message);
        WarningRaised?.Invoke(message);
    }

    private void ShowInfo(string message)
    {
        _logger.LogInformation(message);
        InfoRaised?.Invoke(message);
    }
}
