using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using EChat.UI.Services;

namespace EChat.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, HardwareAccelerated = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // True once the process has fully started the app normally.
    // Stays false if the process was killed by Android and this Activity is being
    // restored from savedInstanceState — in that case Blazor WebView can't restore
    // its state and we get a blank screen. We detect this and restart fresh.
    private static bool _processProperlyStarted;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // If Android is trying to restore the Activity after killing our process,
        // savedInstanceState will be non-null but _processProperlyStarted will be false
        // (static field was reset when the process was killed).
        // Blazor WebView cannot restore from saved state — force a clean restart.
        if (savedInstanceState != null && !_processProperlyStarted)
        {
            var fresh = new Intent(this, typeof(MainActivity));
            fresh.AddFlags(ActivityFlags.ClearTask | ActivityFlags.NewTask);
            StartActivity(fresh);
            Finish();
            return;
        }
        _processProperlyStarted = true;

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

        // Use OnBackPressedDispatcher (works with both button and swipe gesture on all API levels)
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == EChat.Maui.Services.PlatformService.SafRequestCode)
        {
            var uri = resultCode == Result.Ok ? data?.Data : null;
            EChat.Maui.Services.PlatformService.OnSafResult(uri);
        }
    }

    private sealed class BackCallback : OnBackPressedCallback
    {
        private readonly MainActivity _host;
        public BackCallback(MainActivity host) : base(enabled: true) => _host = host;

        public override void HandleOnBackPressed()
        {
            if (!AndroidBackHandler.TriggerBack())
            {
                // Nothing in Blazor handled it — let the system proceed (exit / home)
                Enabled = false;
                _host.OnBackPressedDispatcher.OnBackPressed();
                Enabled = true;
            }
        }
    }
}