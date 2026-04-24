using EChat.UI.Services;

namespace EChat.Web.Services;

public class WebPlatformService : IPlatformService
{
    public bool IsDesktop => true;

    // На Web файловые операции ведутся через JS/InputFile в компоненте
    public bool SupportsMauiFilePicker => false;

    public Task SaveFileAsync(string filename, byte[] content, CancellationToken ct = default, string? mimeType = null, string? title = null)
        => Task.CompletedTask; // Web: скачивание через JS (см. Settings.razor)

    public Task<Stream?> PickFileAsync(CancellationToken ct = default)
        => Task.FromResult<Stream?>(null); // Web: загрузка через <InputFile>

    public void RestartApp() { } // Web: NavigationManager.NavigateTo в компоненте

    public Task OpenAttachmentAsync(string filePath, string fileName, string mimeType)
        => Task.CompletedTask; // Web: JS download в компоненте

    public Task<bool> SaveToDownloadsAsync(string fileName, byte[] content, string mimeType)
        => Task.FromResult(false); // Web: JS download в компоненте

    public bool SupportsPickFolder => false; // Web: нет SAF

    public Task<bool> SaveToPickedFolderAsync(string filename, byte[] content, string mimeType, CancellationToken ct = default)
        => Task.FromResult(false); // Web: нет

    public void UpdateBadge(int totalUnread) { } // Web: нет иконки приложения

    public bool SupportsBackgroundNotificationToggle => false;
    public Task SetBackgroundNotificationVisibleAsync(bool visible) => Task.CompletedTask;
    public Task OpenBatteryOptimizationSettingsAsync() => Task.CompletedTask;
}
