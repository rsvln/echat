using Foundation;
using UIKit;
using UserNotifications;

namespace EChat.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        // Global exception handlers — mirrors Android's MainApplication.cs
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Console.WriteLine($"[eChat] Unobserved task exception: {args.Exception}");
            args.SetObserved(); // prevent process termination
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject?.ToString() ?? "unknown";
            Console.WriteLine($"[eChat] Unhandled exception (terminating={args.IsTerminating}): {ex}");
        };

        var result = base.FinishedLaunching(application, launchOptions);

        // Request local notification permission early — before the first message arrives.
        UNUserNotificationCenter.Current.RequestAuthorization(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge,
            (granted, error) =>
            {
                if (!granted)
                    Console.WriteLine("[eChat] iOS notification permission denied");
            });

        // Show notification banner even when the app is in the foreground.
        UNUserNotificationCenter.Current.Delegate = new EchatNotificationDelegate();

        return result;
    }
}

/// <summary>
/// Presents alert + sound + badge when a notification arrives while the app is foregrounded.
/// </summary>
internal sealed class EchatNotificationDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(
        UNUserNotificationCenter center,
        UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler)
    {
        completionHandler(
            UNNotificationPresentationOptions.Banner |
            UNNotificationPresentationOptions.Sound  |
            UNNotificationPresentationOptions.Badge);
    }
}
