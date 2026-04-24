using System.Reflection;

namespace EChat.Core.Services;

public class VersionInfo
{
    public static string AppVersion
    {
        get
        {
            var v = FullVersion;
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    public static string BuildDate
    {
        get
        {
            var v = FullVersion;
            var plus = v.IndexOf('+');
            if (plus <= 0) return "?";
            var raw = v[(plus + 1)..]; // e.g. "202604241523" or legacy "20260424"
            // yyyyMMddHHmm (12 chars) → "20260424 15:23"
            if (raw.Length == 12
                && DateTime.TryParseExact(raw, "yyyyMMddHHmm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt.ToString("yyyyMMdd HH:mm");
            return raw; // fallback: return as-is
        }
    }

    /// <summary>
    /// Set at startup by each host project to report the host app's own version
    /// (e.g. MAUI 0.1.134, Web 0.1.11) rather than EChat.Core's version.
    /// Format: "major.minor.patch+YYYYMMDD" (same as InformationalVersion).
    /// </summary>
    public static string? VersionOverride { get; set; }

    private static string FullVersion
    {
        get
        {
            if (!string.IsNullOrEmpty(VersionOverride))
                return VersionOverride;
            // typeof(VersionInfo).Assembly is always EChat.Core.dll — reliable on all
            // platforms including MAUI Android where GetEntryAssembly() returns an
            // internal runtime assembly rather than the app assembly.
            var asm = typeof(VersionInfo).Assembly;
            var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion ?? "0.1.0+?";
        }
    }

    /// <summary>
    /// Set at startup by each host project to override OS-detected platform name.
    /// E.g. EChat.Web sets this to "Web" since it runs server-side on Linux/Windows.
    /// </summary>
    public static string? PlatformOverride { get; set; }

    public static string Platform
    {
        get
        {
            if (PlatformOverride != null)
                return PlatformOverride;
            if (OperatingSystem.IsAndroid())
                return "Android";
            if (OperatingSystem.IsIOS())
                return "iOS";
            if (OperatingSystem.IsWindows())
                return "Windows";
            if (OperatingSystem.IsMacOS())
                return "macOS";
            if (OperatingSystem.IsBrowser())
                return "Web";
            return "Unknown";
        }
    }

    public static string RuntimeVersion =>
        Environment.Version.ToString();

    public static string ProjectUrl => "https://github.com/rsvln/echat";

    public static List<LibraryVersion> Libraries
    {
        get
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MailKit", "MimeKit", "PgpCore", "BouncyCastle.Cryptography",
                "Net.Codecrete.QrCodeGenerator",
                "EChat.Core", "EChat.UI"
            };

            var libs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
                    continue;

                var name = asm.GetName().Name ?? "";
                if (names.Contains(name))
                {
                    var ver = asm.GetName().Version;
                    libs.TryAdd(name, ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "?");
                }
            }

            return libs
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new LibraryVersion(kv.Key, kv.Value))
                .ToList();
        }
    }
}

public class LibraryVersion
{
    public string Name { get; }
    public string Version { get; }

    public LibraryVersion(string name, string version)
    {
        Name = name;
        Version = version;
    }
}