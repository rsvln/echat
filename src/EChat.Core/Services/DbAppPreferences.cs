using System.Collections.Concurrent;
using EChat.Core.Data;
using EChat.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EChat.Core.Services;

public class DbAppPreferences : IAppPreferences
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public DbAppPreferences(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>Called from InitializeEChatDatabaseAsync after migrations.</summary>
    public async Task LoadAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var settings = await db.Settings
                .AsNoTracking()
                .Where(s => !s.Key.StartsWith("imap_sync_"))
                .ToListAsync();
            foreach (var s in settings)
                _cache[s.Key] = s.Value;
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
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var existing = await db.Settings.FindAsync(key);
            if (existing == null)
            {
                db.Settings.Add(new Setting
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync();
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
