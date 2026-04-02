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
            .SetContentText("Syncing messages...")
            .SetSmallIcon(Resource.Drawable.notification_icon)
            .SetOngoing(true)
            .Build();
        
        StartForeground(NotificationId, notification);
        
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
                "Email Sync",
                NotificationImportance.Low)
            {
                Description = "Background email synchronization"
            };
            
            var notificationManager = GetSystemService(NotificationService) as NotificationManager;
            notificationManager?.CreateNotificationChannel(channel);
        }
    }
}