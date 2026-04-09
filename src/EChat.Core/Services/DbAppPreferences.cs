using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using static EChat.Core.ServiceCollectionExtensions;

namespace EChat.Core.Services;

public class DbAppPreferences : IAppPreferences
{
    private readonly string _dbPath;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public DbAppPreferences(DatabasePathInfo dbPathInfo)
    {
        _dbPath = dbPathInfo.Path;
    }

    /// <summary>Called from InitializeEChatDatabaseAsync after migrations.</summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(_dbPath)) return;
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=false");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Load only app preference keys (exclude imap sync timestamps and account-scoped settings)
            cmd.CommandText = "SELECT Key, Value FROM Settings WHERE Key NOT LIKE 'imap_sync_%'";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                _cache[reader.GetString(0)] = reader.GetString(1);
        }
        catch { /* DB not ready yet */ }
    }

    public string Get(string key, string defaultValue) =>
        _cache.TryGetValue(key, out var v) ? v : defaultValue;

    public void Set(string key, string value)
    {
        _cache[key] = value;
        _ = PersistAsync(key, value);
    }

    private async Task PersistAsync(string key, string value)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=false");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Settings (Key, Value, UpdatedAt)
                VALUES (@k, @v, @t)
                ON CONFLICT(Key) DO UPDATE SET Value = @v, UpdatedAt = @t
                """;
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    public IDictionary<string, string> ExportAll() =>
        new Dictionary<string, string>(
            _cache.Where(kv => kv.Key != "device_id" && !kv.Key.StartsWith("imap_sync_")));

    public void ImportAll(IDictionary<string, string> data)
    {
        foreach (var (key, value) in data)
            if (key != "device_id")
                Set(key, value);
    }
}
