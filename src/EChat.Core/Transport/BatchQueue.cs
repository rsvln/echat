using System.Collections.Concurrent;
using EChat.Core.Models;
using EChat.Core.Protocol;

namespace EChat.Core.Transport;

public class BatchQueue
{
    private readonly ConcurrentDictionary<BatchKey, List<OutgoingMessage>> _batches = new();
    private readonly Timer _flushTimer;
    private readonly Func<List<OutgoingMessage>, Task> _sendBatchFunc;
    private readonly Func<OutgoingMessage, Task> _sendSingleFunc;
    private TimeSpan _currentBatchWindow = TimeSpan.FromSeconds(10);
    
    public BatchQueue(
        Func<List<OutgoingMessage>, Task> sendBatchFunc,
        Func<OutgoingMessage, Task> sendSingleFunc,
        TimeSpan flushInterval)
    {
        _sendBatchFunc = sendBatchFunc;
        _sendSingleFunc = sendSingleFunc;
        _flushTimer = new Timer(OnFlushTimer, null, flushInterval, flushInterval);
    }
    
    public async Task Enqueue(OutgoingMessage message)
    {
        if (message.Tier == BatchTier.Immediate)
        {
            await _sendSingleFunc(message);
            return;
        }
        
        var key = new BatchKey
        {
            Recipients = message.Recipients.ToHashSet(),
            GroupId = message.GroupId,
            Tier = message.Tier
        };
        
        _batches.AddOrUpdate(key,
            new List<OutgoingMessage> { message },
            (k, list) =>
            {
                list.Add(message);
                return list;
            });
        
        if (_batches[key].Count >= 10)
        {
            await FlushBatch(key);
        }
    }
    
    private async void OnFlushTimer(object? state)
    {
        var keys = _batches.Keys.ToList();
        
        foreach (var key in keys)
        {
            await FlushBatch(key);
        }
    }
    
    private async Task FlushBatch(BatchKey key)
    {
        if (!_batches.TryRemove(key, out var messages) || messages.Count == 0)
            return;
        
        if (messages.Count == 1)
        {
            await _sendSingleFunc(messages[0]);
        }
        else
        {
            await _sendBatchFunc(messages);
        }
    }
    
    public void UpdateBatchWindow(TimeSpan window)
    {
        _currentBatchWindow = window;
    }
    
    public void Dispose()
    {
        _flushTimer?.Dispose();
    }
}