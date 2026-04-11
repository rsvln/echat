namespace EChat.UI.Services;

/// <summary>
/// Lets Android's hardware/gesture back button trigger Blazor navigation
/// instead of closing the app. The currently active page subscribes its
/// handler; MainActivity calls <see cref="TriggerBack"/> on back press.
/// </summary>
public static class AndroidBackHandler
{
    private static Action? _handler;

    /// <summary>Register the handler for the current screen. Call Unregister on dispose.</summary>
    public static void Register(Action handler) => _handler = handler;

    /// <summary>Clear the handler (e.g. when the page is disposed).</summary>
    public static void Unregister() => _handler = null;

    /// <summary>
    /// Called by MainActivity. Returns true if a handler consumed the event
    /// (Blazor navigated back), false if the OS should handle it (exit app).
    /// </summary>
    public static bool TriggerBack()
    {
        if (_handler == null) return false;
        _handler.Invoke();
        return true;
    }
}
