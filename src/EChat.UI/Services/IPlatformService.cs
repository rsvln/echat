namespace EChat.UI.Services;

public interface IPlatformService
{
    bool IsDesktop { get; }

    /// <summary>
    /// true on Android/iOS — use file:// URLs for local media.
    /// </summary>
    bool IsMobile { get; }

    /// <summary>
    /// true on MAUI (FileSaver + FilePicker are available).
    /// false on Web (export via JS download, import via &lt;InputFile&gt;).
    /// </summary>
    bool SupportsMauiFilePicker { get; }

    /// <summary>Saves bytes as a file via the native save dialog (MAUI).</summary>
    Task SaveFileAsync(string filename, byte[] content, CancellationToken ct = default, string? mimeType = null, string? title = null);

    /// <summary>
    /// Opens the native file picker to select a .zip file (MAUI).
    /// On Web returns null — import is handled via &lt;InputFile&gt; in the UI.
    /// </summary>
    Task<Stream?> PickFileAsync(CancellationToken ct = default);

    /// <summary>
    /// Restarts the application (MAUI).
    /// On Web — no-op; the component should call NavigationManager.NavigateTo("/", forceLoad:true).
    /// </summary>
    void RestartApp();

    /// <summary>
    /// Opens a file with the native application (MAUI).
    /// On Android shows the system share/open dialog.
    /// On Web — no-op (use JS download in the component).
    /// </summary>
    Task OpenAttachmentAsync(string filePath, string fileName, string mimeType);

    /// <summary>
    /// Saves a file to the Downloads folder (Android) or via a save dialog (Windows).
    /// On Web — no-op.
    /// </summary>
    Task<bool> SaveToDownloadsAsync(string fileName, byte[] content, string mimeType);

    /// <summary>
    /// true on Android (SAF ACTION_CREATE_DOCUMENT is available).
    /// false on Windows (SaveFileAsync already shows a dialog there) and Web.
    /// </summary>
    bool SupportsPickFolder { get; }

    /// <summary>
    /// Opens the system folder/filename picker (Android SAF) and saves bytes there.
    /// Returns true on success, false on cancel or error.
    /// </summary>
    Task<bool> SaveToPickedFolderAsync(string filename, byte[] content, string mimeType, CancellationToken ct = default);

    /// <summary>
    /// Updates the unread badge on the app icon.
    /// Windows: overlay icon on the taskbar button.
    /// Other platforms and Web: no-op (for now).
    /// </summary>
    void UpdateBadge(int totalUnread);

    /// <summary>
    /// true only on Android — it has a persistent foreground-service notification.
    /// </summary>
    bool SupportsBackgroundNotificationToggle { get; }

    /// <summary>
    /// Shows or hides the "Running in background" notification (Android).
    /// The service keeps running either way.
    /// </summary>
    Task SetBackgroundNotificationVisibleAsync(bool visible);

    /// <summary>
    /// Opens the system battery optimisation dialog for this app (Android).
    /// No-op on other platforms.
    /// </summary>
    Task OpenBatteryOptimizationSettingsAsync();

    /// <summary>
    /// True on Windows and Android — the app can download and install its own update.
    /// False on iOS (App Store policy) and Web (pull a new Docker image instead).
    /// </summary>
    bool SupportsInAppUpdate { get; }

    /// <summary>
    /// Downloads the update from <paramref name="downloadUrl"/> and installs it.
    /// Windows: extracts the ZIP next to the running exe, launches an updater batch script, then quits.
    /// Android: downloads the APK and fires the system install intent.
    /// <paramref name="onProgress"/> receives a 0–1 fraction as bytes arrive.
    /// </summary>
    Task ApplyUpdateAsync(string downloadUrl, string version, Action<double>? onProgress = null);
}
