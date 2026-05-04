using System.Reflection;
using EChat.Core;
using EChat.Core.Services;
using EChat.Maui.Services;
using EChat.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EChat.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // WebView2 по умолчанию пишет данные рядом с exe-шником.
        // Если приложение установлено в Program Files — нет прав.
        // Явно перенаправляем в %LocalAppData%\echat\WebView2.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "echat", "WebView2"));
#endif

        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif


#if WINDOWS
        // Windows: %LocalAppData%\echat\db
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "echat", "db");
#elif ANDROID
        // Android: use external storage so user can access via file manager
        // /storage/emulated/0/Android/data/com.echat.app/files/db
        var externalDir = Android.App.Application.Context.GetExternalFilesDir(null);
        var dbDir = Path.Combine(externalDir!.AbsolutePath, "db");
#else
        // iOS: app-private storage
        var dbDir = Path.Combine(FileSystem.AppDataDirectory, "db");
#endif
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "echat.db");
        var deviceId = Preferences.Get("device_id", Guid.NewGuid().ToString());
        Preferences.Set("device_id", deviceId);

        // Register platform-specific credential protector BEFORE AddEChatCore
        // so TryAddSingleton inside it finds our implementation already registered.
#if WINDOWS
        builder.Services.AddSingleton<ICredentialProtector,
            EChat.Maui.Platforms.Windows.Services.DpapiCredentialProtector>();
#elif ANDROID
        builder.Services.AddSingleton<ICredentialProtector,
            EChat.Maui.Platforms.Android.Services.SecureStorageCredentialProtector>();
#endif

        builder.Services.AddEChatCore(dbPath, deviceId);
        builder.Services.AddSingleton<UserContextService>();
        builder.Services.AddSingleton<IPlatformService, PlatformService>();

        // Report this host's version in the About screen (not EChat.Core's version).
        var hostVersion = typeof(MauiProgram).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrEmpty(hostVersion))
            EChat.Core.Services.VersionInfo.VersionOverride = hostVersion;

        return builder.Build();
    }
}
