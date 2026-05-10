using System.Net.Http;
using System.Text.Json;

namespace EChat.Core.Services;

public record UpdateInfo(
    string Version,
    string? WindowsDownloadUrl,
    string? AndroidDownloadUrl,
    string ReleaseNotes);

/// <summary>
/// Checks GitHub Releases for a newer version of the app.
/// Result is cached for the session lifetime; call InvalidateCache() to force a re-check.
/// </summary>
public class UpdateService
{
    private static string ApiUrl =>
        VersionInfo.ProjectUrl
            .Replace("https://github.com/", "https://api.github.com/repos/")
        + "/releases/latest";

    private UpdateInfo? _cached;
    private bool _checked;

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (_checked) return _cached;

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("echat-app/1.0");
            http.Timeout = TimeSpan.FromSeconds(10);

            var json = await http.GetStringAsync(ApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProp))
                return Finish(null);

            var latestRaw  = (tagProp.GetString() ?? "").TrimStart('v');
            var currentRaw = VersionInfo.AppVersion.Split('+')[0];

            if (!Version.TryParse(latestRaw,  out var latest) ||
                !Version.TryParse(currentRaw, out var current) ||
                latest <= current)
                return Finish(null);

            string? winUrl = null, apkUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == "EChat-win.zip") winUrl = url;
                    else if (name == "EChat.apk")  apkUrl = url;
                }
            }

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            return Finish(new UpdateInfo(latestRaw, winUrl, apkUrl, notes));
        }
        catch
        {
            // Don't hammer the API on errors; cache the null result
            return Finish(null);
        }
    }

    public void InvalidateCache()
    {
        _checked = false;
        _cached  = null;
    }

    private UpdateInfo? Finish(UpdateInfo? info)
    {
        _checked = true;
        return _cached = info;
    }
}
