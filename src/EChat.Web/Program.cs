using EChat.Core;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Transport;
using EChat.UI.Services;
using EChat.Web.Components;
using EChat.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Explicitly bind to all interfaces so Docker port mapping works.
// ASPNETCORE_URLS env var can still override this if needed.
var port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080");
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));

// Data directory: defaults to <app_root>/data so Docker can map it as a volume.
// Override via ECHAT_DATA_DIR env var or EChat:DataDir in appsettings.
var dataDir = Environment.GetEnvironmentVariable("ECHAT_DATA_DIR")
    ?? builder.Configuration["EChat:DataDir"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);

var dbPath = Path.Combine(dataDir, "echat.db");

// Device ID — generated once and persisted in the Settings table via DbAppPreferences.
// On first run before the DB is ready, generate a new ID and pass it to AddEChatCore;
// InitializeEChatDatabaseAsync will seed it into the DB if absent.
var deviceId = Guid.NewGuid().ToString();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddEChatCore(dbPath, deviceId);
builder.Services.AddSingleton<UserContextService>();
builder.Services.AddSingleton<IPlatformService, WebPlatformService>();

var app = builder.Build();

// No HTTPS redirect — SSL termination is handled by nginx/reverse proxy upstream.
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(EChat.UI.Pages.Index).Assembly);

// Initialise DB and start transport on startup
_ = Task.Run(async () =>
{
    await app.Services.InitializeEChatDatabaseAsync();

    // After DB init, device_id is seeded; read it back for transport use
    var resolvedDeviceId = app.Services.GetRequiredService<EChat.Core.Services.IAppPreferences>()
        .Get("device_id", deviceId);

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    var accounts = await db.Accounts.ToListAsync();
    var account = accounts.FirstOrDefault(a => a.IsActive) ?? accounts.FirstOrDefault();
    if (account == null) return;

    var userCtx = app.Services.GetRequiredService<UserContextService>();
    userCtx.Initialize(account.AccountId, account.Email, resolvedDeviceId);

    var syncEngine = app.Services.GetRequiredService<EChat.Core.Sync.SyncEngine>();
    await syncEngine.LoadSettingsAsync(account.AccountId);

    var transport = app.Services.GetRequiredService<EmailTransportService>();
    var incomingMessages = app.Services.GetRequiredService<IncomingMessageService>();
    var accountConfig = app.Services.GetRequiredService<AccountConfig>();
    var multiImap = app.Services.GetRequiredService<MultiAccountImapManager>();

    transport.MessagesReceived += async messages =>
        await incomingMessages.SaveAsync(accountConfig.AccountId, messages);

    multiImap.MessagesReceived += async (accountId, messages) =>
        await incomingMessages.SaveAsync(accountId, messages);

    try { await transport.ReconnectAsync(account, resolvedDeviceId); }
    catch (Exception ex) { app.Logger.LogError(ex, "Transport connect failed"); }

    await multiImap.StartBackgroundAccountsAsync(accounts, account.AccountId);
});

app.Run();
