using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EChat.Core.Protocol;

public class MessageDeduplicator
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenHashes = new();
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly int _maxCacheSize = 10000;
    
    public bool IsDuplicate(ParsedMessage message)
    {
        var hash = ComputeHash(message);
        
        if (_seenHashes.TryGetValue(hash, out var timestamp))
        {
            return true;
        }
        
        _seenHashes.TryAdd(hash, DateTimeOffset.UtcNow);
        
        if (_seenHashes.Count > _maxCacheSize)
        {
            Cleanup();
        }
        
        return false;
    }
    
    private string ComputeHash(ParsedMessage message)
    {
        var input = $"{message.Headers.MessageId}|{message.Sender}|{message.Headers.Timestamp:O}";
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