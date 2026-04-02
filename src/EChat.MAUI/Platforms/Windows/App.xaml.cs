using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace EChat.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);

    public App() => InitializeComponent();

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SetTaskbarIcon);
    }

    private static void SetTaskbarIcon()
    {
        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);

        var icoPath = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
        if (!File.Exists(icoPath))
            icoPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "appicon.ico");
        if (!File.Exists(icoPath)) return;

        // Taskbar + Alt+Tab icon
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.SetIcon(icoPath);

        var hIcon = LoadImage(IntPtr.Zero, icoPath, 1, 0, 0, 0x10);
        if (hIcon != IntPtr.Zero)
        {
            SendMessage(hwnd, 0x0080, new IntPtr(0), hIcon);
            SendMessage(hwnd, 0x0080, new IntPtr(1), hIcon);
        }
    }
}
