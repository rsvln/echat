using EChat.Core.Protocol;
using EChat.Core.Services;

namespace EChat.Core.Transport;

/// <summary>
/// Debounces incoming message batches so that rapid IMAP sync bursts
/// are aggregated into a single SaveAsync call instead of causing
/// multiple SaveChangesAsync + UI updates (which makes the UI "jump").
/// </summary>
public class BatchSyncProcessor
{
    private readonly IncomingMessageService _incomingMessages;
    private readonly FileLogger _fileLogger;

    // Per-account queues: accountId → list of parsed messages
    private readonly Dictionary<string, List<ParsedMessage>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Timers.Timer> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Debounce window in milliseconds. All messages arriving within this
    /// window are batched into a single SaveAsync call.
    /// </summary>
    public int DebounceMs { get; set; } = 2000;

    public BatchSyncProcessor(IncomingMessageService incomingMessages, FileLogger fileLogger)
    {
        _incomingMessages = incomingMessages;
        _fileLogger = fileLogger;
    }

    /// <summary>
    /// Queue messages for batch processing. The actual save is delayed
    /// by DebounceMs milliseconds to allow more messages to accumulate.
    /// </summary>
    public void Queue(string accountId, List<ParsedMessage> messages)
    {
        if (messages == null || messages.Count == 0) return;

        lock (_lock)
        {
            if (!_queues.TryGetValue(accountId, out var queue))
            {
                queue = new List<ParsedMessage>();
                _queues[accountId] = queue;
            }
            queue.AddRange(messages);

            // Reset or create the timer
            if (_timers.TryGetValue(accountId, out var timer))
            {
                timer.Stop();
                timer.Interval = DebounceMs;
                timer.Start();
            }
            else
            {
                timer = new System.Timers.Timer(DebounceMs) { AutoReset = false };
                timer.Elapsed += async (s, e) => await FlushAsync(accountId);
                _timers[accountId] = timer;
                timer.Start();
            }

            _fileLogger.Write("DEBUG", "BatchSyncProcessor", $"[{accountId}] Queued {messages.Count} messages. Queue size: {queue.Count}");
        }
    }

    /// <summary>
    /// Immediately flush all queued messages for all accounts.
    /// Call this before shutdown or when you need synchronous processing.
    /// </summary>
    public async Task FlushAllAsync()
    {
        List<string> accountIds;
        lock (_lock)
        {
            accountIds = _queues.Keys.ToList();
        }
        foreach (var accountId in accountIds)
        {
            await FlushAsync(accountId);
        }
    }

    private async Task FlushAsync(string accountId)
    {
        List<ParsedMessage> batch;
        lock (_lock)
        {
            if (!_queues.TryGetValue(accountId, out var queue) || queue.Count == 0)
            {
                _timers.Remove(accountId);
                return;
            }
            batch = queue.ToList();
            queue.Clear();
            _timers.Remove(accountId);
        }

        _fileLogger.Write("INFO", "BatchSyncProcessor", $"[{accountId}] Flushing batch of {batch.Count} messages");
        await _incomingMessages.SaveAsync(accountId, batch);
    }
}
