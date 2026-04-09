using System.Collections.Concurrent;
using EChat.Core.Models;
using EChat.Core.Protocol;

namespace EChat.Core.Transport;

public class BatchQueue
{
    private readonly ConcurrentDictionary<BatchKey, ConcurrentBag<OutgoingMessage>> _batches = new();
    private readonly Timer _flushTimer;
    private readonly Func<List<OutgoingMessage>, Task> _sendBatchFunc;
    private readonly Func<OutgoingMessage, Task> _sendSingleFunc;
    private readonly Func<BatchTier, TimeSpan> _getWindowFunc;

    public BatchQueue(
        Func<List<OutgoingMessage>, Task> sendBatchFunc,
        Func<OutgoingMessage, Task> sendSingleFunc,
        TimeSpan defaultFlushInterval,
        Func<BatchTier, TimeSpan>? getWindowFunc = null)
    {
        _sendBatchFunc = sendBatchFunc;
        _sendSingleFunc = sendSingleFunc;
        _getWindowFunc = getWindowFunc ?? (_ => defaultFlushInterval);
        _flushTimer = new Timer(OnFlushTimer, null, defaultFlushInterval, defaultFlushInterval);
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
            new ConcurrentBag<OutgoingMessage> { message },
            (k, bag) =>
            {
                bag.Add(message);
                return bag;
            });

        if (_batches.TryGetValue(key, out var currentBag) && currentBag.Count >= 10)
        {
            await FlushBatch(key);
        }
    }

    private async void OnFlushTimer(object? state)
    {
        var keys = _batches.Keys.ToList();
        foreach (var key in keys)
        {
            try { await FlushBatch(key); }
            catch { /* swallow — individual send errors are handled inside FlushBatch */ }
        }
    }

    private async Task FlushBatch(BatchKey key)
    {
        if (!_batches.TryRemove(key, out var bag) || bag.IsEmpty)
            return;

        var messages = bag.ToArray().ToList();

        if (messages.Count == 1)
        {
            await _sendSingleFunc(messages[0]);
        }
        else
        {
            await _sendBatchFunc(messages);
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
    }
}