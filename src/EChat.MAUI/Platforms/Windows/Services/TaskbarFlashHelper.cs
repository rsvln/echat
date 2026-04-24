using System.Runtime.InteropServices;

namespace EChat.Maui.Platforms.Windows.Services;

/// <summary>
/// Flashes the taskbar button when a new message arrives and the window is not focused.
/// Uses FlashWindowEx (user32) — works on any unpackaged Win32 / WinUI3 process.
/// </summary>
internal static class TaskbarFlashHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    // dwFlags values
    private const uint FLASHW_STOP      = 0;
    private const uint FLASHW_CAPTION   = 1;
    private const uint FLASHW_TRAY      = 2;
    private const uint FLASHW_ALL       = FLASHW_CAPTION | FLASHW_TRAY;
    private const uint FLASHW_TIMERNOFG = 12; // flash until the window comes to the foreground

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pfwi);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public static void Flash()
    {
        try
        {
            var hwnd = GetWindowHandle();
            if (hwnd == nint.Zero) return;

            // Don't flash if we are already the foreground window.
            if (GetForegroundWindow() == hwnd) return;

            var info = new FLASHWINFO
            {
                cbSize   = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd     = hwnd,
                dwFlags  = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount   = 5,
                dwTimeout = 0
            };
            FlashWindowEx(ref info);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Stop flashing (call when window is focused).</summary>
    public static void StopFlash()
    {
        try
        {
            var hwnd = GetWindowHandle();
            if (hwnd == nint.Zero) return;
            var info = new FLASHWINFO
            {
                cbSize  = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd    = hwnd,
                dwFlags = FLASHW_STOP
            };
            FlashWindowEx(ref info);
        }
        catch { }
    }

    private static nint GetWindowHandle()
    {
        var win = Microsoft.Maui.Controls.Application.Current
            ?.Windows.FirstOrDefault()
            ?.Handler
            ?.PlatformView as Microsoft.UI.Xaml.Window;
        if (win == null) return nint.Zero;
        return WinRT.Interop.WindowNative.GetWindowHandle(win);
    }
}
