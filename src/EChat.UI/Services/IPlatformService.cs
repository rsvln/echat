namespace EChat.UI.Services;

public interface IPlatformService
{
    bool IsDesktop { get; }

    /// <summary>
    /// true on Android/iOS — use file:// URLs for local media.
    /// </summary>
    bool IsMobile { get; }

    /// <summary>
    /// true на MAUI (FileSaver + FilePicker доступны).
    /// false на Web (экспорт через JS download, импорт через &lt;InputFile&gt;).
    /// </summary>
    bool SupportsMauiFilePicker { get; }

    /// <summary>Сохраняет байты как файл через нативный диалог (MAUI).</summary>
    Task SaveFileAsync(string filename, byte[] content, CancellationToken ct = default, string? mimeType = null, string? title = null);

    /// <summary>
    /// Открывает нативный файловый пикер для выбора .zip (MAUI).
    /// На Web возвращает null — импорт ведётся через &lt;InputFile&gt; в UI.
    /// </summary>
    Task<Stream?> PickFileAsync(CancellationToken ct = default);

    /// <summary>
    /// Перезапускает приложение (MAUI).
    /// На Web — no-op; компонент должен вызвать NavigationManager.NavigateTo("/", forceLoad:true).
    /// </summary>
    void RestartApp();

    /// <summary>
    /// Открывает файл через нативное приложение (MAUI).
    /// На Android показывает системный шаринг/открытие.
    /// На Web — no-op (используй JS download в компоненте).
    /// </summary>
    Task OpenAttachmentAsync(string filePath, string fileName, string mimeType);

    /// <summary>
    /// Сохраняет файл в папку Загрузки (Android) или через диалог (Windows).
    /// На Web — no-op.
    /// </summary>
    Task<bool> SaveToDownloadsAsync(string fileName, byte[] content, string mimeType);

    /// <summary>
    /// true на Android (SAF ACTION_CREATE_DOCUMENT доступен).
    /// false на Windows (там SaveFileAsync уже показывает диалог) и Web.
    /// </summary>
    bool SupportsPickFolder { get; }

    /// <summary>
    /// Открывает системный диалог выбора папки/имени файла (Android SAF) и сохраняет байты туда.
    /// Возвращает true при успехе, false при отмене или ошибке.
    /// </summary>
    Task<bool> SaveToPickedFolderAsync(string filename, byte[] content, string mimeType, CancellationToken ct = default);

    /// <summary>
    /// Обновляет счётчик непрочитанных на иконке приложения.
    /// Windows: overlay-иконка на кнопке таскбара.
    /// Другие платформы и Web: no-op (пока).
    /// </summary>
    void UpdateBadge(int totalUnread);

    /// <summary>
    /// true только на Android — там есть постоянное уведомление foreground-сервиса.
    /// </summary>
    bool SupportsBackgroundNotificationToggle { get; }

    /// <summary>
    /// Показывает или скрывает уведомление "Running in background" (Android).
    /// Сервис продолжает работать в любом случае.
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
