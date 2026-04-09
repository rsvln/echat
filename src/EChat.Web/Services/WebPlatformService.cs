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
}
