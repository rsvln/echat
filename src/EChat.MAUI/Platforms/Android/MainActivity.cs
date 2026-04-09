using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace EChat.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, HardwareAccelerated = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Disable edge-to-edge: content must not go behind the system navigation bar
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            Window?.SetDecorFitsSystemWindows(true);

        // Resize WebView when keyboard opens so fixed input bar stays above keyboard
        Window?.SetSoftInputMode(SoftInput.AdjustResize);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 0);
        }
    }
}