using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Transport;

/// <summary>
/// Manages one IMAP worker per non-active account.
/// When the user switches accounts, the old active account gets a worker
/// and the new active account's worker is stopped (EmailTransportService takes over).
/// </summary>
public class MultiAccountImapManager
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<MultiAccountImapManager> _logger;
    private readonly Dictionary<string, AccountImapWorker> _workers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fired when any background account receives new messages. accountId + messages.</summary>
    public event Func<string, List<ParsedMessage>, Task>? MessagesReceived;

    public MultiAccountImapManager(
        IServiceProvider sp,
        ILogger<MultiAccountImapManager> logger,
        ChatEventService chatEvents)
    {
        _sp = sp;
        _logger = logger;
        chatEvents.AccountSwitched += (oldAccountId, newAccountId) =>
            _ = Task.Run(() => OnAccountSwitchedAsync(oldAccountId, newAccountId));
    }

    /// <summary>
    /// Call on startup: starts IMAP workers for all accounts EXCEPT the active one
    /// (the active account is handled by EmailTransportService).
    /// </summary>
    public async Task StartBackgroundAccountsAsync(IEnumerable<Account> accounts, string activeAccountId)
    {
        foreach (var account in accounts)
        {
            if (account.AccountId == activeAccountId) continue;
            await EnsureWorkerStartedAsync(account);
        }
    }

    /// <summary>
    /// Call when the user switches from <paramref name="oldActiveAccountId"/> to a new account.
    /// Starts a worker for the old account (it's now background) and stops any worker for the new account
    /// (EmailTransportService will take over for the new active account).
    /// </summary>
    public async Task OnAccountSwitchedAsync(string oldActiveAccountId, string newActiveAccountId)
    {
        // Stop worker for new active (EmailTransportService takes over)
        StopWorker(newActiveAccountId);

        // Start worker for old active (it becomes background)
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var oldAccount = await db.Accounts.FindAsync(oldActiveAccountId);
        if (oldAccount != null)
            await EnsureWorkerStartedAsync(oldAccount);
    }

    private void StopWorker(string accountId)
    {
        if (_workers.TryGetValue(accountId, out var worker))
        {
            worker.Stop();
            _workers.Remove(accountId);
        }
    }

    private async Task EnsureWorkerStartedAsync(Account account)
    {
        if (_workers.ContainsKey(account.AccountId))
            return;

        var since = DateTimeOffset.UtcNow.AddDays(-4);
        HashSet<string> knownIds;
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var ids = await db.Messages
                .Where(m => m.MessageId != null && m.ReceivedAt >= since)
                .Select(m => m.MessageId!)
                .ToListAsync();
            knownIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load known IDs for {Email}", account.Email);
            knownIds = new HashSet<string>();
        }

        var worker = new AccountImapWorker(
            account,
            _sp.GetRequiredService<ILogger<AccountImapWorker>>(),
            _sp.GetRequiredService<ILogger<EChat.Core.Transport.ImapService>>(),
            _sp.GetRequiredService<ChatMessageParser>(),
            _sp.GetRequiredService<PgpService>(),
            _sp.GetRequiredService<MessageDeduplicator>());

        worker.MessagesReceived += async (accountId, msgs) =>
        {
            if (MessagesReceived != null)
                await MessagesReceived(accountId, msgs);
        };

        _workers[account.AccountId] = worker;
        worker.Start(knownIds, since);
    }
}
