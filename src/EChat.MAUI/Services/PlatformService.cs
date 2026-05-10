using EChat.UI.Services;

namespace EChat.Maui.Services;

public class PlatformService : IPlatformService
{
#if ANDROID
    // SAF ACTION_CREATE_DOCUMENT request code
    internal const int SafRequestCode = 2001;

    // Completed by MainActivity.OnActivityResult when the user picks a destination
    private static TaskCompletionSource<Android.Net.Uri?>? _safTcs;

    internal static void OnSafResult(Android.Net.Uri? uri) =>
        _safTcs?.TrySetResult(uri);
#endif

#if ANDROID || IOS
    public bool IsMobile => true;
#else
    public bool IsMobile => false;
#endif

#if WINDOWS
    public bool IsDesktop => true;
#else
    public bool IsDesktop => false;
#endif

    public bool SupportsMauiFilePicker => true;

#if ANDROID
    public bool SupportsPickFolder => true;
#else
    public bool SupportsPickFolder => false;
#endif

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
                    // Both .echatbackup (new encrypted) and .zip (legacy) are accepted.
                    { DevicePlatform.Android, ["application/octet-stream", "application/zip"] },
                    { DevicePlatform.WinUI,   [".echatbackup", ".zip"] },
                    { DevicePlatform.iOS,     ["public.data", "public.zip-archive"] },
                    { DevicePlatform.macOS,   ["echatbackup", "zip"] },
                })
        };

        var file = await FilePicker.Default.PickAsync(options);
        if (file == null) return null;
        return await file.OpenReadAsync();
    }

    public async Task OpenAttachmentAsync(string filePath, string fileName, string mimeType)
    {
#if ANDROID
        // Copy to cache with correct name, then share (shows Open With + Save options)
        var cachePath = Path.Combine(FileSystem.CacheDirectory, fileName);
        File.Copy(filePath, cachePath, overwrite: true);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File  = new ShareFile(cachePath, mimeType)
        });
#elif WINDOWS
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
        await Task.CompletedTask;
#elif IOS
        // iOS: copy to cache with correct name, then share sheet (Open With + AirDrop + Save etc.)
        var cachePath = Path.Combine(FileSystem.CacheDirectory, fileName);
        File.Copy(filePath, cachePath, overwrite: true);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File  = new ShareFile(cachePath, mimeType)
        });
#else
        await Task.CompletedTask;
#endif
    }

    public async Task<bool> SaveToDownloadsAsync(string fileName, byte[] content, string mimeType)
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            if ((int)Android.OS.Build.VERSION.SdkInt >= 29)
            {
                var values = new Android.Content.ContentValues();
                values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
                values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, mimeType);
                values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, "Download/");

                var uri = context.ContentResolver!.Insert(
                    Android.Provider.MediaStore.Downloads.ExternalContentUri, values);

                if (uri == null)
                {
                    // Fallback: use share sheet
                    var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);
                    await File.WriteAllBytesAsync(tempPath, content);
                    await Share.Default.RequestAsync(new ShareFileRequest
                    {
                        Title = fileName,
                        File = new ShareFile(tempPath, mimeType)
                    });
                    return true;
                }

                using var stream = context.ContentResolver.OpenOutputStream(uri);
                if (stream == null) return false;

                await stream.WriteAsync(content);
                await stream.FlushAsync();
                stream.Close();
                return true;
            }
            else
            {
                var downloadsDir = Android.OS.Environment.GetExternalStoragePublicDirectory(
                    Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
                var destPath = Path.Combine(downloadsDir, fileName);
                if (File.Exists(destPath))
                {
                    var name = Path.GetFileNameWithoutExtension(fileName);
                    var ext  = Path.GetExtension(fileName);
                    var i = 1;
                    while (File.Exists(destPath))
                        destPath = Path.Combine(downloadsDir, $"{name} ({i++}){ext}");
                }
                await File.WriteAllBytesAsync(destPath, content);
                Android.Media.MediaScannerConnection.ScanFile(
                    context, [destPath], [mimeType], null);
                return true;
            }
        }
        catch
        {
            try
            {
                var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);
                await File.WriteAllBytesAsync(tempPath, content);
                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = fileName,
                    File = new ShareFile(tempPath, mimeType)
                });
                return true;
            }
            catch { return false; }
        }
#elif WINDOWS
        // On Windows use the existing SaveFileAsync dialog
        await SaveFileAsync(fileName, content, mimeType: mimeType);
        return true;
#elif IOS
        // iOS has no user-accessible Downloads folder — use the share sheet instead
        // so the user can save to Files app, iCloud Drive, AirDrop etc.
        var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(tempPath, content);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File  = new ShareFile(tempPath, mimeType)
        });
        return true;
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    public async Task<bool> SaveToPickedFolderAsync(string filename, byte[] content, string mimeType, CancellationToken ct = default)
    {
#if ANDROID
        try
        {
            _safTcs = new TaskCompletionSource<Android.Net.Uri?>();

            var intent = new Android.Content.Intent(Android.Content.Intent.ActionCreateDocument);
            intent.AddCategory(Android.Content.Intent.CategoryOpenable);
            intent.SetType(mimeType);
            intent.PutExtra(Android.Content.Intent.ExtraTitle, filename);

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                           ?? throw new InvalidOperationException("No current Android activity");
            activity.StartActivityForResult(intent, SafRequestCode);

            using var reg = ct.Register(() => _safTcs?.TrySetCanceled());
            var uri = await _safTcs.Task;
            if (uri == null) return false;

            var cr = Android.App.Application.Context.ContentResolver
                     ?? throw new InvalidOperationException("ContentResolver unavailable");
            using var stream = cr.OpenOutputStream(uri)
                               ?? throw new InvalidOperationException("Cannot open output stream for URI");
            await stream.WriteAsync(content, ct);
            await stream.FlushAsync(ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch { return false; }
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    public void UpdateBadge(int totalUnread)
    {
#if WINDOWS
        EChat.Maui.Platforms.Windows.Services.TaskbarBadgeHelper.SetBadge(totalUnread);
#elif IOS
        EChat.Maui.Platforms.iOS.Services.MessageNotificationHelper.UpdateBadge(totalUnread);
#endif
        // Android: could update app-icon badge in future; no-op for now
    }

#if ANDROID
    public bool SupportsBackgroundNotificationToggle => true;
#else
    public bool SupportsBackgroundNotificationToggle => false;
#endif

    public Task OpenBatteryOptimizationSettingsAsync()
    {
#if ANDROID
        var ctx    = Android.App.Application.Context;
        var intent = new Android.Content.Intent(
            Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
        intent.SetData(Android.Net.Uri.Parse("package:" + ctx.PackageName));
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        ctx.StartActivity(intent);
#endif
        return Task.CompletedTask;
    }

    public async Task SetBackgroundNotificationVisibleAsync(bool visible)
    {
#if ANDROID
        // Persist the setting so EmailSyncService reads it on next start
        var prefs = IPlatformApplication.Current!.Services
            .GetRequiredService<EChat.Core.Services.IAppPreferences>();
        prefs.Set("bg_notification_visible", visible ? "true" : "false");

        // Restart the foreground service so it picks up the new setting immediately
        var ctx    = Android.App.Application.Context;
        var intent = new Android.Content.Intent(ctx,
            typeof(EChat.Maui.Platforms.Android.Services.EmailSyncService));
        ctx.StopService(intent);
        await Task.Delay(300); // give OnDestroy time to run
        ctx.StartForegroundService(intent);
#else
        await Task.CompletedTask;
#endif
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
#elif IOS
        // iOS policy prohibits programmatic app restart — no-op.
        // Users restart the app manually via the app switcher.
#endif
    }

    // ── In-app update ─────────────────────────────────────────────────────

#if WINDOWS || ANDROID
    public bool SupportsInAppUpdate => true;
#else
    public bool SupportsInAppUpdate => false;
#endif

    public async Task ApplyUpdateAsync(string downloadUrl, string version, Action<double>? onProgress = null)
    {
#if WINDOWS
        var tempDir    = Path.Combine(Path.GetTempPath(), "echat-update");
        var zipPath    = Path.Combine(tempDir, "EChat-win.zip");
        var extractDir = Path.Combine(tempDir, "extracted");
        Directory.CreateDirectory(tempDir);

        // Download with progress
        using var http = new System.Net.Http.HttpClient();
        using var response = await http.GetAsync(downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src  = await response.Content.ReadAsStreamAsync();
        await using var dest = File.Create(zipPath);
        var buf        = new byte[81920];
        long downloaded = 0;
        int  read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read));
            downloaded += read;
            if (total > 0) onProgress?.Invoke((double)downloaded / total);
        }
        dest.Close();
        onProgress?.Invoke(1.0);

        // Extract
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

        // The ZIP packs files under an "echat\" inner folder (see publish.bat)
        var innerDir = Directory.GetDirectories(extractDir).FirstOrDefault() ?? extractDir;
        var appDir   = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;

        // Write and launch an updater script that replaces files after the app exits
        var script = Path.Combine(tempDir, "update.bat");
        File.WriteAllText(script,
            $"""
            @echo off
            timeout /t 2 /nobreak >nul
            robocopy "{innerDir}" "{appDir}" /e /is /it /r:3 /w:1 >nul 2>&1
            start "" "{Path.Combine(appDir, "echat.exe")}"
            del "%~f0"
            """);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow  = true,
            UseShellExecute = false
        });

        Microsoft.Maui.Controls.Application.Current?.Quit();

#elif ANDROID
        var context = Android.App.Application.Context;
        var pm      = context.PackageManager!;

        // If the user hasn't allowed installs from this source, open the settings page
        if (!pm.CanRequestPackageInstalls())
        {
            var settingsIntent = new Android.Content.Intent(
                Android.Provider.Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + context.PackageName));
            settingsIntent.AddFlags(Android.Content.ActivityFlags.NewTask);
            context.StartActivity(settingsIntent);
            return; // user will come back and tap Install again
        }

        // Download APK with progress
        var apkPath = Path.Combine(FileSystem.CacheDirectory, $"EChat-{version}.apk");
        using var http = new System.Net.Http.HttpClient();
        using var response = await http.GetAsync(downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src  = await response.Content.ReadAsStreamAsync();
        await using var dest = File.Create(apkPath);
        var buf        = new byte[81920];
        long downloaded = 0;
        int  read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read));
            downloaded += read;
            if (total > 0) onProgress?.Invoke((double)downloaded / total);
        }
        dest.Close();
        onProgress?.Invoke(1.0);

        // Fire install intent via FileProvider
        var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(
            context,
            context.PackageName + ".update.provider",
            new Java.IO.File(apkPath));

        var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
        intent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
        intent.AddFlags(
            Android.Content.ActivityFlags.GrantReadUriPermission |
            Android.Content.ActivityFlags.NewTask);
        context.StartActivity(intent);
#else
        await Task.CompletedTask;
#endif
    }
}
