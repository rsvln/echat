using System.Runtime.InteropServices;

namespace EChat.Maui.Platforms.Windows.Services;

/// <summary>
/// Draws a numeric unread-count badge (overlay icon) on the Windows taskbar button.
/// Uses ITaskbarList3::SetOverlayIcon with a GDI-rendered red circle + white text.
/// </summary>
internal static class TaskbarBadgeHelper
{
    private static nint _prevIcon = nint.Zero;

    // ── COM ──────────────────────────────────────────────────────────────────

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"),
     ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListCoClass { }

    /// <summary>
    /// ITaskbarList3 vtable (must match exactly — inherited methods listed first).
    /// IUnknown (3) → ITaskbarList (5) → ITaskbarList2 (1) → ITaskbarList3 (10+)
    /// </summary>
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool full);
        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
        void RegisterTab(nint hwnd, nint hwndMdi);
        void UnregisterTab(nint hwnd);
        void SetTabOrder(nint hwnd, nint insertBefore);
        void SetTabActive(nint hwnd, nint hwndMdi, uint reserved);
        void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarSetImageList(nint hwnd, nint himl);
        void SetOverlayIcon(nint hwnd, nint hIcon,
            [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

    // ── GDI / User32 P/Invoke ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint   biSize;
        public int    biWidth, biHeight;   // biHeight negative → top-down DIB
        public ushort biPlanes, biBitCount;
        public uint   biCompression;       // 0 = BI_RGB
        public uint   biSizeImage;
        public int    biXPelsPerMeter, biYPelsPerMeter;
        public uint   biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool  fIcon;
        public uint  xHotspot, yHotspot;
        public nint  hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("gdi32.dll")] static extern nint  CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] static extern bool  DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] static extern nint  SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] static extern bool  DeleteObject(nint ho);
    [DllImport("gdi32.dll")] static extern nint  CreateDIBSection(nint hdc,
        ref BITMAPINFOHEADER bmi, uint usage, out nint ppvBits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint  CreateBitmap(int w, int h,
        uint planes, uint bpp, byte[]? bits);
    [DllImport("gdi32.dll")] static extern nint  CreateFont(int cHeight, int cWidth,
        int cEsc, int cOri, int weight, uint italic, uint underline, uint strikeOut,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string? face);
    [DllImport("gdi32.dll")] static extern uint  SetTextColor(nint hdc, uint colorRef);
    [DllImport("gdi32.dll")] static extern int   SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool  GdiFlush();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
    static extern int DrawTextW(nint hdc, string text, int len, ref RECT rc, uint fmt);

    [DllImport("user32.dll")] static extern nint CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint hIcon);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Show badge with <paramref name="count"/>. Pass 0 to clear the badge.
    /// Safe to call from any thread.
    /// </summary>
    public static void SetBadge(int count)
    {
        // ITaskbarList3 is an STA COM object — must be driven from the UI thread.
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var hwnd = GetWindowHandle();
                if (hwnd == nint.Zero) return;

                var taskbar = (ITaskbarList3)new TaskbarListCoClass();
                taskbar.HrInit();

                if (count <= 0)
                {
                    taskbar.SetOverlayIcon(hwnd, nint.Zero, null);
                    FreePrev();
                    return;
                }

                var text  = count > 99 ? "99+" : count.ToString();
                var hIcon = CreateBadgeIcon(text);
                if (hIcon == nint.Zero) return;

                taskbar.SetOverlayIcon(hwnd, hIcon, $"{count} unread");
                FreePrev();
                _prevIcon = hIcon;
            }
            catch { /* best-effort */ }
        });
    }

    /// <summary>Remove the badge overlay (e.g. when window gains focus).</summary>
    public static void ClearBadge() => SetBadge(0);

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void FreePrev()
    {
        if (_prevIcon != nint.Zero)
        {
            DestroyIcon(_prevIcon);
            _prevIcon = nint.Zero;
        }
    }

    private static nint CreateBadgeIcon(string text)
    {
        const int   SIZE = 20;
        const float CX   = SIZE / 2f - 0.5f;  // 9.5 — pixel-centre of a 20-wide bitmap
        const float CY   = SIZE / 2f - 0.5f;
        const float R    = SIZE / 2f - 1.0f;  // 9.0 — 1 px transparent border

        // ── 1. Create 32-bit top-down DIB section ────────────────────────────
        var bmi = new BITMAPINFOHEADER
        {
            biSize        = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth       = SIZE,
            biHeight      = -SIZE,  // negative = top-down (row 0 at top)
            biPlanes      = 1,
            biBitCount    = 32,
            biCompression = 0       // BI_RGB
        };

        nint hdc     = CreateCompatibleDC(0);
        nint hBmp    = CreateDIBSection(hdc, ref bmi, 0 /*DIB_RGB_COLORS*/,
                                        out nint bits, 0, 0);
        nint hOldBmp = SelectObject(hdc, hBmp);

        // ── 2. Fill red circle directly into the pixel buffer ────────────────
        // DIB 32-bit pixel memory layout: [B, G, R, A] per pixel (little-endian).
        // As int32: bit pattern = A<<24 | R<<16 | G<<8 | B
        // Red #E53935 (R=0xE5, G=0x39, B=0x35, A=0xFF):
        //   = (0xFF << 24) | (0xE5 << 16) | (0x39 << 8) | 0x35
        //   Shift ops don't throw in checked context, so this is safe even with sign-bit overflow.
        int redPixel = (0xFF << 24) | (0xE5 << 16) | (0x39 << 8) | 0x35;

        var pixels = new int[SIZE * SIZE];
        for (int y = 0; y < SIZE; y++)
        for (int x = 0; x < SIZE; x++)
        {
            float dx = x - CX, dy = y - CY;
            pixels[y * SIZE + x] = dx * dx + dy * dy <= R * R ? redPixel : 0;
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        // ── 3. Draw text with GDI ────────────────────────────────────────────
        // GDI writes RGB but zeroes the alpha channel in a 32-bit DIB.
        // We will repair alpha in step 4.
        const int  TRANSPARENT_BK   = 1;
        const int  FW_BOLD           = 700;
        const uint CLEARTYPE_QUALITY = 5;
        const uint FF_SWISS          = 32;   // sans-serif family
        const uint DT_CENTER         = 0x01;
        const uint DT_VCENTER        = 0x04;
        const uint DT_SINGLELINE     = 0x20;

        SetBkMode(hdc, TRANSPARENT_BK);
        SetTextColor(hdc, 0x00FFFFFF);  // white — COLORREF = 0x00BBGGRR

        int   fontH    = text.Length >= 3 ? -8 : -11; // smaller for "99+"
        nint  hFont    = CreateFont(fontH, 0, 0, 0, FW_BOLD,
                                    0, 0, 0, 0, 0, 0,
                                    CLEARTYPE_QUALITY, FF_SWISS, "Segoe UI");
        nint  hOldFont = SelectObject(hdc, hFont);

        var rc = new RECT { left = 0, top = 0, right = SIZE, bottom = SIZE };
        DrawTextW(hdc, text, text.Length, ref rc,
                  DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        SelectObject(hdc, hOldFont);
        DeleteObject(hFont);
        GdiFlush();  // flush batched GDI ops before reading pixels back via Marshal

        // ── 4. Read back pixels and restore alpha zeroed by GDI ─────────────
        // For pixels inside the circle: force alpha = 0xFF (GDI zeroed it for text pixels).
        // For pixels outside: keep transparent (alpha = 0).
        Marshal.Copy(bits, pixels, 0, pixels.Length);

        int alphaMask = 0xFF << 24;  // bit pattern 0xFF000000, result = -16777216 as int
        for (int y = 0; y < SIZE; y++)
        for (int x = 0; x < SIZE; x++)
        {
            float dx = x - CX, dy = y - CY;
            int   i  = y * SIZE + x;
            pixels[i] = dx * dx + dy * dy <= R * R
                ? pixels[i] | alphaMask   // force alpha = FF inside circle
                : 0;                       // transparent outside circle
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        // ── 5. Create HICON ──────────────────────────────────────────────────
        // hbmMask: 1bpp all-zero AND-mask (alpha channel drives transparency on Vista+).
        // Stride for a 1bpp bitmap must be WORD-aligned: ceil(SIZE/16)*2 bytes per row.
        int   maskStride = (SIZE + 15) / 16 * 2;
        nint  hMask      = CreateBitmap(SIZE, SIZE, 1, 1,
                                        new byte[maskStride * SIZE]); // all zeros

        var   iconInfo = new ICONINFO { fIcon = true, hbmColor = hBmp, hbmMask = hMask };
        nint  hIcon    = CreateIconIndirect(ref iconInfo);

        // ── 6. Release GDI objects (CreateIconIndirect copies the bitmaps) ───
        SelectObject(hdc, hOldBmp);
        DeleteObject(hBmp);
        DeleteObject(hMask);
        DeleteDC(hdc);

        return hIcon;
    }

    private static nint GetWindowHandle()
    {
        var win = Microsoft.Maui.Controls.Application.Current
            ?.Windows.FirstOrDefault()
            ?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        return win is null ? nint.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(win);
    }
}
