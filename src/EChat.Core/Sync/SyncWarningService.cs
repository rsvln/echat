using EChat.Core.Models;
using EChat.Core.Services;

namespace EChat.Core.Sync;

public class SyncWarningService
{
    private readonly FileLogger _fileLogger;

    public event Action<string>? WarningRaised;
    public event Action<string>? InfoRaised;

    public SyncWarningService(FileLogger fileLogger)
    {
        _fileLogger = fileLogger;
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

    private void ShowWarning(string message)
    {
        _fileLogger.Write("WARN", "SyncWarningService", message);
        WarningRaised?.Invoke(message);
    }

    private void ShowInfo(string message)
    {
        _fileLogger.Write("INFO", "SyncWarningService", message);
        InfoRaised?.Invoke(message);
    }
}
