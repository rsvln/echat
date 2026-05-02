namespace EChat.Core.Sync;

/// <summary>
/// Ambient clock backed by <see cref="NtpTimeService"/>.
/// Falls back to <see cref="DateTimeOffset.UtcNow"/> until NTP sync completes.
/// </summary>
public static class NtpClock
{
    private static NtpTimeService? _service;

    internal static void SetService(NtpTimeService service) => _service = service;

    public static DateTimeOffset UtcNow => _service?.UtcNow ?? DateTimeOffset.UtcNow;
}
