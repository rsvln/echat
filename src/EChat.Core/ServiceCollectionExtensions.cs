using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Groups;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Services;
using EChat.Core.Sync;
using EChat.Core.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EChat.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEChatCore(
        this IServiceCollection services,
        string dbPath,
        string deviceId)
    {
        // Database
        services.AddDbContext<ChatDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}",
                o => o.CommandTimeout(30)));

        // AccountConfig — mutable singleton; Email filled later via ReconnectAsync
        services.AddSingleton(new AccountConfig { DeviceId = deviceId });

        // Protocol
        services.AddSingleton<ChatMessageParser>();
        services.AddSingleton<ChatMessageBuilder>(); // takes AccountConfig via DI
        services.AddSingleton<MessageDeduplicator>();
        services.AddSingleton<MessageOrderCorrector>();

        // Transport
        services.AddSingleton<ImapService>();
        services.AddSingleton<SmtpService>();
        services.AddSingleton<EmailTransportService>(); // takes AccountConfig via DI

        // Sync
        services.AddSingleton<NtpTimeService>();
        services.AddSingleton<SyncEngine>(); // loaded from DB at runtime
        services.AddSingleton<SyncWarningService>();
        services.AddSingleton<DeviceSyncService>(); // takes AccountConfig via DI

        // Groups
        services.AddScoped<GroupStateManager>();
        services.AddSingleton<GroupMergeEngine>();

        // Crypto
        services.AddSingleton<PgpService>();
        services.AddSingleton<KeyVerificationService>();

        // Event bus & app-level services
        services.AddSingleton<ChatEventService>();
        services.AddSingleton<IncomingMessageService>();
        services.AddSingleton<MultiAccountImapManager>();

        // File logger — created after dbPath is known
        // (registered below in InitializeEChatDatabaseAsync)

        // Expose dbPath so UI can display it and BackupService can access it
        var dbPathInfo = new DatabasePathInfo { Path = dbPath };
        services.AddSingleton(dbPathInfo);
        services.AddSingleton<BackupService>();

        // File logger
        var fileLogger = new FileLogger(dbPath);
        services.AddSingleton(fileLogger);

        // Preferences backed by the SQLite Settings table
        services.AddSingleton<DbAppPreferences>();
        services.AddSingleton<IAppPreferences>(sp => sp.GetRequiredService<DbAppPreferences>());

        return services;
    }

    public class DatabasePathInfo
    {
        public string Path { get; set; } = string.Empty;
    }

    public static async Task InitializeEChatDatabaseAsync(this IServiceProvider serviceProvider)
    {
        // ── Step 1: resolve the DB path without keeping any EF connection open ──
        string dbPath;
        using (var scope = serviceProvider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            dbPath = ctx.Database.GetDbConnection().DataSource;
        } // scope + DbContext disposed → no EF connection alive

        // ── Step 2: check for stale (pre-migrations) DB file ──
        if (File.Exists(dbPath))
        {
            bool hasMigrationsTable = false;
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false       // no pool → file released immediately on Dispose
            }.ToString();

            using (var probe = new SqliteConnection(cs))
            {
                await probe.OpenAsync();
                using var cmd = probe.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master " +
                    "WHERE type='table' AND name='__EFMigrationsHistory'";
                hasMigrationsTable = (long)(await cmd.ExecuteScalarAsync() ?? 0L) > 0;
            } // probe disposed, file unlocked

            if (!hasMigrationsTable)
            {
                SqliteConnection.ClearAllPools(); // flush any leftover pools
                File.Delete(dbPath);
            }
        }

        // ── Step 3: apply migrations in a fresh scope ──
        using (var scope = serviceProvider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await ctx.Database.MigrateAsync();
            // Enable WAL mode for better concurrency (readers don't block writers)
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");

            // Clean up data for deleted chats, but keep the Chat row itself as a tombstone.
            // The tombstone (Deleted = true) prevents HandleGroupCreateAsync from re-creating
            // the group when the original group-create email is re-synced from IMAP.
            var deletedChatIds = await ctx.Chats
                .Where(c => c.Deleted)
                .Select(c => c.ChatId)
                .ToListAsync();
            if (deletedChatIds.Count > 0)
            {
                await ctx.Messages.Where(m => deletedChatIds.Contains(m.ChatId)).ExecuteDeleteAsync();
                await ctx.GroupMembers.Where(m => deletedChatIds.Contains(m.GroupId)).ExecuteDeleteAsync();
                await ctx.Groups.Where(g => deletedChatIds.Contains(g.GroupId)).ExecuteDeleteAsync();
                await ctx.GroupKeyPairs.Where(g => deletedChatIds.Contains(g.GroupId)).ExecuteDeleteAsync();
                // Chat rows are intentionally kept as tombstones — do NOT delete them.
            }
        }

        // ── Step 4: load app preferences into cache ──
        if (serviceProvider.GetService<DbAppPreferences>() is { } prefs)
        {
            await prefs.LoadAsync();

            // Seed device_id from AccountConfig if not already in preferences
            var accountConfig = serviceProvider.GetRequiredService<AccountConfig>();
            if (string.IsNullOrEmpty(prefs.Get("device_id", "")))
                prefs.Set("device_id", accountConfig.DeviceId);
        }
    }
}
