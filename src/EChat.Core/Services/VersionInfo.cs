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
            return plus > 0 ? v[(plus + 1)..] : "?";
        }
    }

    private static string FullVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion ?? "0.1.0+?";
        }
    }

    public static string Platform
    {
        get
        {
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