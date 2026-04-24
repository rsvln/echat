using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace EChat.Maui.Platforms.Android.Services;

/// <summary>
/// Posts per-message notifications via the Android Notification Manager.
/// Uses a separate high-importance channel from the background sync service.
/// </summary>
internal static class MessageNotificationHelper
{
    // v2 forces Android to create a fresh channel with High importance — the old channel may have
    // been created with wrong settings in earlier builds and Android preserves channel settings
    // between reinstalls.
    private const string ChannelId   = "echat_messages_v2";
    private const string ChannelName = "New Messages";

    /// <summary>
    /// Derives a stable, per-chat notification ID from the chatId string.
    /// Stays in range [3000, 103000) so it never collides with the sync service (1001)
    /// or old increment-based IDs (2000-range).
    /// </summary>
    private static int ChatNotificationId(string chatId) =>
        Math.Abs(chatId.GetHashCode()) % 100_000 + 3000;

    public static void Show(Context ctx, string chatId, string chatName, string body, int totalUnread)
    {
        try
        {
            global::Android.Util.Log.Debug("eChat", $"Show() called: chat={chatName}, body={body}");

            // Respect system notification permission (Android 13+).
            var compat = NotificationManagerCompat.From(ctx);
            if (!compat.AreNotificationsEnabled())
            {
                global::Android.Util.Log.Warn("eChat", "POST_NOTIFICATIONS not granted — cannot show message notification");
                return;
            }

            EnsureChannel(ctx);

            // Tap → bring app to foreground.
            var launchIntent = ctx.PackageManager!.GetLaunchIntentForPackage(ctx.PackageName!);
            launchIntent?.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var pendingIntent = PendingIntent.GetActivity(
                ctx, 0, launchIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            var notification = new NotificationCompat.Builder(ctx, ChannelId)
                .SetContentTitle(chatName)
                .SetContentText(body)
                .SetSmallIcon(Resource.Drawable.notification_icon)
                .SetAutoCancel(true)
                .SetContentIntent(pendingIntent)
                .SetNumber(totalUnread)
                .SetPriority(NotificationCompat.PriorityMax)   // MAX = heads-up popup
                .SetCategory(NotificationCompat.CategoryMessage)
                .SetDefaults(NotificationCompat.DefaultAll)    // sound + vibration + lights
                .SetVisibility(NotificationCompat.VisibilityPublic)
                .Build();

            // Use stable per-chat ID: Notify() with the same ID replaces the existing
            // notification for that chat instead of stacking new ones.
            compat.Notify(ChatNotificationId(chatId), notification);
            global::Android.Util.Log.Debug("eChat", $"Notification posted (id={ChatNotificationId(chatId)}): {chatName} / {body}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("eChat", $"Failed to post notification: {ex}");
        }
    }

    private static void EnsureChannel(Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = ctx.GetSystemService(Context.NotificationService) as NotificationManager;

        var existing = mgr?.GetNotificationChannel(ChannelId);
        if (existing != null)
        {
            global::Android.Util.Log.Debug("eChat", $"Channel {ChannelId} exists, importance={existing.Importance}");
            return;
        }

        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.High)
        {
            Description = "Incoming chat message notifications",
            LockscreenVisibility = NotificationVisibility.Public
        };
        channel.EnableVibration(true);
        channel.SetShowBadge(true);
        mgr?.CreateNotificationChannel(channel);
        global::Android.Util.Log.Debug("eChat", $"Channel {ChannelId} created with High importance");
    }
}
