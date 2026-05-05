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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EChat.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEChatCore(
        this IServiceCollection services,
        string dbPath,
        string deviceId)
    {
        // Credential protector — platform-specific code can register a stronger implementation
        // (e.g. DpapiCredentialProtector) BEFORE calling AddEChatCore; TryAdd skips this default.
        services.TryAddSingleton<ICredentialProtector, PlaintextCredentialProtector>();

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

        // Groups
        services.AddScoped<GroupStateManager>();
        services.AddSingleton<GroupMergeEngine>();

        // Crypto
        services.AddSingleton<PgpService>();
        services.AddSingleton<KeyVerificationService>();

        // Invite / key-exchange
        services.AddSingleton<InviteService>();

        // Event bus & app-level services
        services.AddSingleton<ChatEventService>();
        services.AddSingleton<IncomingMessageService>();
        services.AddSingleton<BatchSyncProcessor>();
        services.AddSingleton<MultiAccountImapManager>();

        // File logger — created after dbPath is known
        // (registered below in InitializeEChatDatabaseAsync)

        // Expose dbPath so UI can display it and BackupService can access it
        // Mirror FileLogger's appDir logic: attachments live at <appDir>/attachments,
        // where appDir = parent of the db/ subdirectory (same level as log/).
        var dbDir  = Path.GetDirectoryName(dbPath) ?? ".";
        var appDir = Path.GetDirectoryName(dbDir)  ?? dbDir;
        var dbPathInfo = new DatabasePathInfo
        {
            Path = dbPath,
            AttachmentsDir = Path.Combine(appDir, "attachments")
        };
        services.AddSingleton(dbPathInfo);
        services.AddSingleton<BackupService>();

        // File logger
        var fileLogger = new FileLogger(dbPath);
        services.AddSingleton(fileLogger);

        // Preferences backed by the SQLite Settings table
        services.AddSingleton<DbAppPreferences>();
        services.AddSingleton<IAppPreferences>(sp => sp.GetRequiredService<DbAppPreferences>());

        // Startup signal — UI waits on MigrationsReady before querying DB
        services.AddSingleton<AppStartupService>();

        return services;
    }

    public class DatabasePathInfo
    {
        public string Path { get; set; } = string.Empty;
        public string AttachmentsDir { get; set; } = string.Empty;

        /// <summary>
        /// Resolves a stored FilePath to an absolute path.
        /// New records store only the filename (relative); old records stored absolute paths — both handled.
        /// </summary>
        public string ResolveFilePath(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return string.Empty;
            if (System.IO.Path.IsPathRooted(stored)) return stored; // legacy absolute path
            return System.IO.Path.Combine(AttachmentsDir, stored);
        }
    }

    public static async Task InitializeEChatDatabaseAsync(this IServiceProvider serviceProvider)
    {
        var startup = serviceProvider.GetService<AppStartupService>();
        try
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
        bool migrationFailed = false;
        using (var scope = serviceProvider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            try
            {
                await ctx.Database.MigrateAsync();
            }
            catch
            {
                migrationFailed = true;
                // Schema repair runs below after this scope is disposed.
            }
        } // scope disposed → EF connection released

        // ── Step 3b: defensive schema repair ──
        // Runs only when MigrateAsync failed (common on Android when a migration's DropIndex
        // targets an index that does not exist on older DB files, aborting the transaction).
        // We patch the Chats table directly and record the migration as applied so that
        // subsequent launches skip it.
        if (migrationFailed)
        {
            SqliteConnection.ClearAllPools();
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
            using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();

            // Discover existing Chats columns
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info('Chats')";
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync()) cols.Add(rdr.GetString(0));
            }

            // Add columns introduced by the failed migration
            if (!cols.Contains("ContactEmail"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Chats ADD COLUMN ContactEmail TEXT";
                await cmd.ExecuteNonQueryAsync();
            }
            if (!cols.Contains("GroupId"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Chats ADD COLUMN GroupId TEXT";
                await cmd.ExecuteNonQueryAsync();
            }
            if (!cols.Contains("TombstoneVersion"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Chats ADD COLUMN TombstoneVersion INTEGER";
                await cmd.ExecuteNonQueryAsync();
            }

            // Discover existing Messages columns
            var msgCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info('Messages')";
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync()) msgCols.Add(rdr.GetString(0));
            }

            if (!msgCols.Contains("IsSystem"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Messages ADD COLUMN IsSystem INTEGER NOT NULL DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }

            // Backfill: group chats → GroupId = ChatId; 1:1 chats → ContactEmail from Contacts
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE Chats SET GroupId = ChatId WHERE Type = 1 AND GroupId IS NULL";
                await cmd.ExecuteNonQueryAsync();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE Chats SET ContactEmail = " +
                    "(SELECT c.Email FROM Contacts c WHERE c.DisplayName = Chats.Name OR c.Email = Chats.Name LIMIT 1) " +
                    "WHERE Type = 0 AND ContactEmail IS NULL";
                await cmd.ExecuteNonQueryAsync();
            }

            // Create indexes introduced by the migration (IF NOT EXISTS = idempotent)
            foreach (var idxSql in new[]
            {
                "CREATE INDEX IF NOT EXISTS IX_Chats_AccountId_ContactEmail ON Chats (AccountId, ContactEmail)",
                "CREATE INDEX IF NOT EXISTS IX_Chats_ContactEmail ON Chats (ContactEmail)",
                "CREATE INDEX IF NOT EXISTS IX_Chats_GroupId ON Chats (GroupId)",
            })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = idxSql;
                await cmd.ExecuteNonQueryAsync();
            }

            // Discover existing Attachments columns
            var attCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info('Attachments')";
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync()) attCols.Add(rdr.GetString(0));
            }

            if (attCols.Contains("IsImage"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Attachments DROP COLUMN IsImage";
                await cmd.ExecuteNonQueryAsync();
            }
            if (attCols.Contains("IsVideo"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Attachments DROP COLUMN IsVideo";
                await cmd.ExecuteNonQueryAsync();
            }

            // Mark migrations as applied so they are never retried
            foreach (var migId in new[]
            {
                "20260411204537_ReplacePartnerEmailWithContactEmailAndGroupId",
                "20260412120000_BackfillContactEmailAndGroupId",
                "20260425120000_AddIsSystemToMessages",
                "20260426160000_AddChatTombstoneVersion",
                "20260427221822_RemoveIsImageIsVideoFromAttachments",
            })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) " +
                    $"VALUES ('{migId}', '9.0.4')";
                await cmd.ExecuteNonQueryAsync();
            }
        } // raw connection disposed

        // ── Step 3c: post-migration setup ──
        SqliteConnection.ClearAllPools();

        using (var scope = serviceProvider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            // Enable WAL mode for better concurrency (readers don't block writers)
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");

            // Normalize Attachment.FilePath: old records stored absolute paths; convert to filename only.
            // Split on both / and \ so Windows paths are handled correctly on any platform.
            var absPathAtts = await ctx.Attachments
                .Where(a => a.FilePath != null && (a.FilePath.Contains("/") || a.FilePath.Contains("\\")))
                .ToListAsync();
            if (absPathAtts.Count > 0)
            {
                foreach (var att in absPathAtts)
                    att.FilePath = att.FilePath!.Split('/', '\\').Last();
                await ctx.SaveChangesAsync();
            }

            // Clean up data for deleted chats, but keep the Chat row itself as a tombstone.
            // The tombstone (Deleted = true) prevents HandleGroupCreateAsync from re-creating
            // the group when the original group-create email is re-synced from IMAP.
            var deletedChats = await ctx.Chats
                .Where(c => c.Deleted)
                .Select(c => new { c.ChatId, c.GroupId })
                .ToListAsync();
            var deletedChatIds = deletedChats.Select(c => c.ChatId).ToList();

            // Only purge group data for GroupIds where NO active (non-deleted) chat still references them.
            // A GroupId shared between a deleted and an active chat must be kept intact.
            var candidateGroupIds = deletedChats
                .Where(c => c.GroupId != null)
                .Select(c => c.GroupId!)
                .Distinct()
                .ToList();
            var activeGroupIds = candidateGroupIds.Count > 0
                ? await ctx.Chats
                    .Where(c => !c.Deleted && c.GroupId != null && candidateGroupIds.Contains(c.GroupId!))
                    .Select(c => c.GroupId!)
                    .Distinct()
                    .ToListAsync()
                : new List<string>();
            var deletedGroupIds = candidateGroupIds.Except(activeGroupIds).ToList();

            if (deletedChatIds.Count > 0)
            {
                await ctx.Messages.Where(m => deletedChatIds.Contains(m.ChatId)).ExecuteDeleteAsync();
                if (deletedGroupIds.Count > 0)
                {
                    await ctx.GroupMembers.Where(m => deletedGroupIds.Contains(m.GroupId)).ExecuteDeleteAsync();
                    // Null out GroupId on tombstone chats BEFORE deleting Groups.
                    // Tombstone Chat rows are kept (not deleted) but still hold GroupId,
                    // which creates a FK reference that blocks DELETE from Groups
                    // (Chats→Groups ON DELETE RESTRICT). Clearing it unblocks the delete
                    // while preserving the tombstone semantics (Deleted=true is the marker).
                    await ctx.Chats
                        .Where(c => c.Deleted && c.GroupId != null && deletedGroupIds.Contains(c.GroupId!))
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.GroupId, (string?)null));
                    await ctx.Groups.Where(g => deletedGroupIds.Contains(g.GroupId)).ExecuteDeleteAsync();
                    await ctx.GroupKeyPairs.Where(g => deletedGroupIds.Contains(g.GroupId)).ExecuteDeleteAsync();
                }
                // Chat rows are intentionally kept as tombstones — do NOT delete them.
            }
        }

        }
        catch (Exception stepEx)
        {
            // Swallow — let callers handle their own errors.
            // Fall through to Step 3d so credentials are always re-encrypted.
            serviceProvider.GetService<FileLogger>()?.Write("ERROR", "Init",
                $"Steps 1–3c failed: {stepEx.GetType().Name}: {stepEx.Message}");
        }

        // ── Step 3d: re-encrypt legacy plaintext credentials ──────────────────
        // Runs OUTSIDE the main try/catch so a failed migration never skips this.
        // EF Value Converters only run on write. Existing accounts that were saved
        // before encryption was introduced still have plaintext in the DB.
        // We read raw stored values (bypassing the EF converter) via raw SQL,
        // check whether they are already protected, and skip the write entirely
        // when all credentials are up to date — avoiding a pointless DB write on
        // every startup once migration has been performed.
        try
        {
            var log3d     = serviceProvider.GetService<FileLogger>();
            var protector = serviceProvider.GetService<ICredentialProtector>()
                            ?? PlaintextCredentialProtector.Instance;

            using var scope = serviceProvider.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            // Read raw (possibly-plaintext) stored values without the EF converter.
            var rawRows = new List<(string Id, string RawPwd, string? RawKey)>();
            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                ctx.Database.GetConnectionString()))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT AccountId, Password, PrivateKey FROM Accounts";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rawRows.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            if (rawRows.Count > 0)
            {
                var needsMigration = rawRows.Any(r =>
                    !protector.IsProtected(r.RawPwd) ||
                    (r.RawKey != null && !protector.IsProtected(r.RawKey)));

                if (needsMigration)
                {
                    log3d?.Write("INFO", "Init",
                        $"Step 3d: re-encrypting credentials for {rawRows.Count} account(s).");

                    var accounts = await ctx.Accounts.ToListAsync();
                    foreach (var acc in accounts)
                    {
                        ctx.Entry(acc).Property(a => a.Password).IsModified = true;
                        if (acc.PrivateKey != null)
                            ctx.Entry(acc).Property(a => a.PrivateKey).IsModified = true;
                    }
                    await ctx.SaveChangesAsync();
                    log3d?.Write("INFO", "Init", "Step 3d: done.");
                }
                else
                {
                    log3d?.Write("DEBUG", "Init",
                        $"Step 3d: all {rawRows.Count} account(s) already encrypted — skipping.");
                }
            }
        }
        catch (Exception e3d)
        {
            serviceProvider.GetService<FileLogger>()?.Write("ERROR", "Init",
                $"Step 3d (credential re-encryption) failed: {e3d.GetType().Name}: {e3d.Message}");
        }

        // ── Step 4: load app preferences into cache ──
        // Runs unconditionally — outside the main try/catch so that an exception
        // in Steps 1–3c (e.g. a failed migration) never prevents MinLevel from
        // being applied, which would leave the logger at the default Info level
        // regardless of what the user chose in Settings.
        try
        {
            if (serviceProvider.GetService<DbAppPreferences>() is { } prefs)
            {
                await prefs.LoadAsync();

                // Apply stored log level to FileLogger so it takes effect immediately on startup.
                if (serviceProvider.GetService<FileLogger>() is { } fileLogger)
                {
                    var stored = prefs.Get("log_level", "");
                    if (!string.IsNullOrEmpty(stored) &&
                        Enum.TryParse<AppLogLevel>(stored, ignoreCase: true, out var level))
                        fileLogger.MinLevel = level;
                }

                // Seed device_id from AccountConfig if not already in preferences
                var accountConfig = serviceProvider.GetRequiredService<AccountConfig>();
                if (string.IsNullOrEmpty(prefs.Get("device_id", "")))
                    prefs.Set("device_id", accountConfig.DeviceId);
            }
        }
        catch { /* preferences are best-effort */ }
        finally
        {
            // Guarantee the UI is never left waiting regardless of exceptions.
            startup?.SignalMigrationsComplete();
        }
    }
}
