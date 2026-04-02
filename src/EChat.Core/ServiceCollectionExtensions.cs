using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Groups;
using EChat.Core.Models;
using EChat.Core.Protocol;
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
            options.UseSqlite($"Data Source={dbPath}"));

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
        services.AddSingleton(sp => new SyncEngine(
            sp.GetRequiredService<ILogger<SyncEngine>>(),
            new SyncSettings()));
        services.AddSingleton<DeviceSyncService>(); // takes AccountConfig via DI

        // Groups
        services.AddScoped<GroupStateManager>();
        services.AddSingleton<GroupMergeEngine>();

        // Crypto
        services.AddSingleton<PgpService>();
        services.AddSingleton<KeyVerificationService>();

        return services;
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
        }
    }
}
