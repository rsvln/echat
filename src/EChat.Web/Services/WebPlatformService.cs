using EChat.UI.Services;

namespace EChat.Web.Services;

public class WebPlatformService : IPlatformService
{
    public bool IsDesktop => true;
    public bool IsMobile => false;

    // On Web all file operations are handled via JS/InputFile in the component
    public bool SupportsMauiFilePicker => false;

    public Task SaveFileAsync(string filename, byte[] content, CancellationToken ct = default, string? mimeType = null, string? title = null)
        => Task.CompletedTask; // Web: download via JS (see Settings.razor)

    public Task<Stream?> PickFileAsync(CancellationToken ct = default)
        => Task.FromResult<Stream?>(null); // Web: upload via <InputFile>

    public void RestartApp() { } // Web: call NavigationManager.NavigateTo in the component

    public Task OpenAttachmentAsync(string filePath, string fileName, string mimeType)
        => Task.CompletedTask; // Web: JS download in the component

    public Task<bool> SaveToDownloadsAsync(string fileName, byte[] content, string mimeType)
        => Task.FromResult(false); // Web: JS download in the component

    public bool SupportsPickFolder => false; // Web: no SAF

    public Task<bool> SaveToPickedFolderAsync(string filename, byte[] content, string mimeType, CancellationToken ct = default)
        => Task.FromResult(false); // Web: not supported

    public void UpdateBadge(int totalUnread) { } // Web: no app icon badge

    public bool SupportsBackgroundNotificationToggle => false;
    public Task SetBackgroundNotificationVisibleAsync(bool visible) => Task.CompletedTask;
    public Task OpenBatteryOptimizationSettingsAsync() => Task.CompletedTask;

    // Updates — Web users pull a new Docker image manually; no in-app install
    public bool SupportsInAppUpdate => false;
    public Task ApplyUpdateAsync(string downloadUrl, string version, Action<double>? onProgress = null)
        => Task.CompletedTask;
}
