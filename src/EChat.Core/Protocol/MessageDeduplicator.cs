using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EChat.Core.Protocol;

public class MessageDeduplicator
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenHashes = new();
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly int _maxCacheSize = 10000;
    
    public bool IsDuplicate(string accountId, ParsedMessage message)
    {
        var hash = ComputeHash(accountId, message);

        // TryAdd is atomic: returns false if the key already existed (= duplicate).
        // This avoids the TryGetValue + TryAdd race where two concurrent callers
        // both see "not in cache" and both proceed as non-duplicates.
        if (!_seenHashes.TryAdd(hash, DateTimeOffset.UtcNow))
            return true;

        if (_seenHashes.Count > _maxCacheSize)
            Cleanup();

        return false;
    }
    
    private string ComputeHash(string accountId, ParsedMessage message)
    {
        var input = $"{accountId}|{message.Headers.MessageId}|{message.Sender}|{message.Headers.Timestamp:O}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes);
    }
    
    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(24));
        
        var toRemove = _seenHashes
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var key in toRemove)
        {
            _seenHashes.TryRemove(key, out _);
        }
    }
}