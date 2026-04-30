using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using EChat.Core.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EChat.Maui.Platforms.Android.Services;

[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class EmailSyncService : Service
{
    private const int NotificationId = 1001;
    private const string ChannelId = "echat_sync_channel";
    private ILogger<EmailSyncService>? _logger;
    private EmailTransportService? _transportService;
    private CancellationTokenSource? _cancellationTokenSource;
    
    public override IBinder? OnBind(Intent? intent)
    {
        return null;
    }
    
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();

        var launchIntent = PackageManager!.GetLaunchIntentForPackage(PackageName!);
        launchIntent?.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        var pendingIntent = PendingIntent.GetActivity(
            this, 0, launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("EChat")
            .SetContentText("Running in background")
            .SetSmallIcon(Resource.Drawable.notification_icon)
            .SetOngoing(true)
            .SetContentIntent(pendingIntent)
            .SetPriority(NotificationCompat.PriorityMin)        // collapsed into overflow; no heads-up
            .SetVisibility(NotificationCompat.VisibilitySecret) // hidden on lock screen
            .Build();

        StartForeground(NotificationId, notification);

        // If the user turned off the persistent notification, remove it immediately.
        // The service keeps running — Android only kills foreground services aggressively,
        // and with battery optimisation disabled the service stays alive either way.
        var prefs = IPlatformApplication.Current?.Services
            ?.GetService<EChat.Core.Services.IAppPreferences>();
        var showNotification = prefs?.Get("bg_notification_visible", "true") != "false";
        if (!showNotification)
            StopForeground(StopForegroundFlags.Remove);

        _cancellationTokenSource = new CancellationTokenSource();

        // IPlatformApplication.Current can be null when Android restarts the Sticky
        // service after a process kill, before MAUI finishes initializing.
        // Retry for up to 10 seconds to give MAUI time to come up.
        Task.Run(async () =>
        {
            try
            {
                IServiceProvider? serviceProvider = null;
                for (var i = 0; i < 20; i++)
                {
                    serviceProvider = IPlatformApplication.Current?.Services;
                    if (serviceProvider != null) break;
                    await Task.Delay(500, _cancellationTokenSource.Token);
                }

                if (serviceProvider == null)
                {
                    global::Android.Util.Log.Warn("eChat", "EmailSyncService: MAUI not ready after 10 s, giving up");
                    return;
                }

                _logger = serviceProvider.GetService<ILogger<EmailSyncService>>();
                _transportService = serviceProvider.GetService<EmailTransportService>();

                if (_transportService != null && !_transportService.IsConnected)
                {
                    _logger?.LogInformation("Starting email sync service");
                    _logger?.LogInformation("Transport already initialised by app startup");
                }
            }
            catch (System.OperationCanceledException) { /* service was stopped */ }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Email sync service error");
                global::Android.Util.Log.Error("eChat", $"EmailSyncService error: {ex}");
            }
        });

        return StartCommandResult.Sticky;
    }
    
    public override void OnDestroy()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        base.OnDestroy();
    }
    
    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "Background Sync",
                NotificationImportance.Min)   // Min = no sound/vibration, collapsed by default
            {
                Description = "Background email synchronization"
            };
            
            var notificationManager = GetSystemService(NotificationService) as NotificationManager;
            notificationManager?.CreateNotificationChannel(channel);
        }
    }
}