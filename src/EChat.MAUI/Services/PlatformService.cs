using EChat.UI.Services;

namespace EChat.Maui.Services;

public class PlatformService : IPlatformService
{
#if WINDOWS
    public bool IsDesktop => true;
#else
    public bool IsDesktop => false;
#endif

    public bool SupportsMauiFilePicker => true;

    public async Task SaveFileAsync(string filename, byte[] content, CancellationToken ct = default, string? mimeType = null, string? title = null)
    {
        var resolvedMime = mimeType ?? "application/octet-stream";
        var resolvedTitle = title ?? "Save File";

#if WINDOWS
        var savePicker = new Windows.Storage.Pickers.FileSavePicker();
        savePicker.SuggestedStartLocation =
            Windows.Storage.Pickers.PickerLocationId.Downloads;
        // Determine extension from mime type
        var ext = resolvedMime switch
        {
            "application/zip" => ".zip",
            "text/plain" => ".txt",
            "application/json" => ".json",
            _ => Path.GetExtension(filename) ?? ".bin"
        };
        savePicker.FileTypeChoices.Add(resolvedMime.Split('/').Last(), [ext]);
        savePicker.SuggestedFileName = filename;

        // Привязываем к окну (обязательно для unpackaged приложений)
        var platformWindow = (Microsoft.UI.Xaml.Window)
            Microsoft.Maui.Controls.Application.Current!.Windows[0].Handler.PlatformView!;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

        var file = await savePicker.PickSaveFileAsync();
        if (file != null)
            await Windows.Storage.FileIO.WriteBytesAsync(file, content);
#else
        // Android / iOS — системный диалог шаринга/сохранения
        var tempPath = Path.Combine(FileSystem.CacheDirectory, filename);
        await File.WriteAllBytesAsync(tempPath, content, ct);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = resolvedTitle,
            File  = new ShareFile(tempPath, resolvedMime)
        });
#endif
    }

    public async Task<Stream?> PickFileAsync(CancellationToken ct = default)
    {
        var options = new PickOptions
        {
            PickerTitle = "Select EChat backup file",
            FileTypes   = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, ["application/zip"] },
                    { DevicePlatform.WinUI,   [".zip"] },
                    { DevicePlatform.iOS,     ["public.zip-archive"] },
                    { DevicePlatform.macOS,   ["zip"] },
                })
        };

        var file = await FilePicker.Default.PickAsync(options);
        if (file == null) return null;
        return await file.OpenReadAsync();
    }

    public void RestartApp()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        var intent  = context.PackageManager!
            .GetLaunchIntentForPackage(context.PackageName!)!;
        intent.AddFlags(
            Android.Content.ActivityFlags.ClearTop |
            Android.Content.ActivityFlags.NewTask);
        context.StartActivity(intent);
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#elif WINDOWS
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (exe != null) System.Diagnostics.Process.Start(exe);
        Microsoft.Maui.Controls.Application.Current?.Quit();
#endif
    }
}
