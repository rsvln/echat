using Android.App;
using Android.Runtime;
using Android.Util;

namespace EChat.Maui;

[Application]
public class MainApplication : MauiApplication
{
    private const string Tag = "eChat";

    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
        // Catch unhandled exceptions from background Tasks before they
        // become unobserved and silently kill the process on Android.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(Tag, $"Unobserved task exception: {args.Exception}");
            args.SetObserved(); // prevent process termination
        };

        // Catch any remaining unhandled exceptions on non-UI threads.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject?.ToString() ?? "unknown";
            Log.Error(Tag, $"Unhandled exception (terminating={args.IsTerminating}): {ex}");
        };
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}