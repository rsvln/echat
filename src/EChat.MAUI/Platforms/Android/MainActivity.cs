using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using AndroidX.Core.View;
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

        // Disable edge-to-edge: content must not go behind the system navigation bar.
        // Only honoured up to Android 15 — see BottomInsetsListener for Android 16+.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            Window?.SetDecorFitsSystemWindows(true);

        // Resize WebView when keyboard opens so fixed input bar stays above keyboard
        Window?.SetSoftInputMode(SoftInput.AdjustResize);

        var content = FindViewById(Android.Resource.Id.Content);
        if (content != null)
            ViewCompat.SetOnApplyWindowInsetsListener(content, new BottomInsetsListener());

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

    /// <summary>
    /// Keeps the WebView above the button navigation bar and the keyboard.
    /// Android 16+ ignores the edge-to-edge opt-out for apps targeting API 36, so the
    /// WebView is drawn under the system bars and AdjustResize no longer resizes it.
    /// TappableElement is the three-button bar's height and 0 with gesture navigation,
    /// so the padding appears only when there is a bar to tap. On older Android the
    /// decor already fits the system windows and these insets arrive as 0.
    /// </summary>
    private sealed class BottomInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v == null || insets == null) return insets;

            var navBar = insets.GetInsets(WindowInsetsCompat.Type.TappableElement()).Bottom;
            var ime    = insets.GetInsets(WindowInsetsCompat.Type.Ime()).Bottom;
            var bottom = Math.Max(navBar, ime);

            v.SetPadding(v.PaddingLeft, v.PaddingTop, v.PaddingRight, bottom);

            // Pass the rest down: the WebView still sees the top inset for
            // env(safe-area-inset-top), and no longer counts the bottom twice.
            return insets.Inset(0, 0, 0, bottom);
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