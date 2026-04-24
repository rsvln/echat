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

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("EChat")
            .SetContentText("Running in background")
            .SetSmallIcon(Resource.Drawable.notification_icon)
            .SetOngoing(true)
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
        
        Task.Run(async () =>
        {
            try
            {
                var serviceProvider = IPlatformApplication.Current?.Services;
                if (serviceProvider == null) return;
                
                _logger = serviceProvider.GetService<ILogger<EmailSyncService>>();
                _transportService = serviceProvider.GetService<EmailTransportService>();
                
                if (_transportService != null && !_transportService.IsConnected)
                {
                    _logger?.LogInformation("Starting email sync service");
                    // IDLE is managed inside ReconnectAsync; if already connected, nothing to do
                    _logger?.LogInformation("Transport already initialised by app startup");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Email sync service error");
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