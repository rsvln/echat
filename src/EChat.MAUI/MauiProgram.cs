using EChat.Core;
using EChat.Maui.Services;
using EChat.UI.Services;
using Microsoft.Extensions.Logging;

namespace EChat.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

//#if DEBUG
//        builder.Services.AddBlazorWebViewDeveloperTools();
//        builder.Logging.SetMinimumLevel(LogLevel.Debug);
//#endif

        var dbDir = Path.Combine(FileSystem.AppDataDirectory, "db");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "echat.db");
        var deviceId = Preferences.Get("device_id", Guid.NewGuid().ToString());
        Preferences.Set("device_id", deviceId);

        builder.Services.AddEChatCore(dbPath, deviceId);
        builder.Services.AddSingleton<UserContextService>();
        builder.Services.AddSingleton<ChatEventService>();
        builder.Services.AddSingleton<IPlatformService, PlatformService>();
        builder.Services.AddSingleton<IAppPreferences, AppPreferences>();
        builder.Services.AddSingleton<IncomingMessageService>();
        builder.Services.AddSingleton<MultiAccountImapManager>();

        return builder.Build();
    }
}
