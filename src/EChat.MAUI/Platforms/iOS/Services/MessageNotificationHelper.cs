using Foundation;
using UserNotifications;

namespace EChat.Maui.Platforms.iOS.Services;

/// <summary>
/// Posts per-message local notifications via UNUserNotificationCenter.
/// Mirrors Android's MessageNotificationHelper.cs.
/// </summary>
internal static class MessageNotificationHelper
{
    /// <summary>
    /// Schedules a local notification for an incoming message and updates the app-icon badge.
    /// Safe to call from any thread.
    /// </summary>
    public static void Show(string chatId, string chatName, string body, int totalUnread)
    {
        try
        {
            var content = new UNMutableNotificationContent
            {
                Title = chatName,
                Body  = body,
                Sound = UNNotificationSound.Default,
                Badge = new NSNumber(totalUnread)
            };

            // Minimum allowed time-interval trigger delay is 0.1 s; repeating = false.
            var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(0.1, false);

            // Use chatId as the notification identifier so a new message in the same chat
            // replaces (rather than stacks on top of) the previous notification.
            var request = UNNotificationRequest.FromIdentifier(chatId, content, trigger);

            UNUserNotificationCenter.Current.AddNotificationRequest(request, error =>
            {
                if (error != null)
                    Console.WriteLine($"[eChat] iOS notification error: {error}");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[eChat] iOS notification failed: {ex}");
        }
    }

    /// <summary>
    /// Updates the app-icon badge count. Must be called on main thread — wrapped internally.
    /// </summary>
    public static void UpdateBadge(int count)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                UIKit.UIApplication.SharedApplication.ApplicationIconBadgeNumber = count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[eChat] iOS badge update failed: {ex}");
            }
        });
    }
}
