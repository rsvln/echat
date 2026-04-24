namespace EChat.Core.Services;

/// <summary>
/// Tracks app startup phases so UI components can wait for migrations
/// to complete before querying the database.
/// </summary>
public class AppStartupService
{
    private TaskCompletionSource _migrationsDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Awaitable task that completes once all EF Core migrations have been applied.</summary>
    public Task MigrationsReady => _migrationsDone.Task;

    internal void SignalMigrationsComplete() => _migrationsDone.TrySetResult();

    /// <summary>
    /// Resets the startup signal so <see cref="InitializeEChatDatabaseAsync"/> can be
    /// awaited again — used after a backup restore on platforms that do not restart the process.
    /// </summary>
    public void Reset() =>
        _migrationsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}
