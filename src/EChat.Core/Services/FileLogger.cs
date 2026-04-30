using System.Text;

namespace EChat.Core.Services;

public enum AppLogLevel
{
    None  = 0,   // nothing written
    Error = 1,   // errors only
    Warn  = 2,   // errors + warnings
    Info  = 3,   // errors + warnings + info
    Debug = 4    // everything
}

/// <summary>
/// Simple file-based logger for debug diagnostics.
/// Writes to a timestamped .log file in the "log" subdirectory next to the database.
/// Each application launch creates a new file.
/// Only messages at or below <see cref="MinLevel"/> verbosity are written.
/// </summary>
public class FileLogger
{
    private readonly string _logDir;
    private string _logPath;
    private readonly object _lock = new();
    private const int MaxFileSize = 5 * 1024 * 1024; // 5 MB
    private const int MaxLogFiles = 20;

    /// <summary>Minimum severity to write. Messages more verbose than this are silently dropped.</summary>
    public AppLogLevel MinLevel { get; set; } = AppLogLevel.Info;

    public FileLogger(string dbPath)
    {
        var dbDir = Path.GetDirectoryName(dbPath) ?? ".";
        var appDir = Path.GetDirectoryName(dbDir) ?? dbDir;

        // Fallback to LocalApplicationData if appDir is empty, ".", same as dbDir,
        // or a system directory (Windows packaged app edge case)
        var isSystemDir = appDir.StartsWith("C:\\Windows", StringComparison.OrdinalIgnoreCase)
                          || appDir.StartsWith("C:\\Program Files", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(appDir) || appDir == "." || appDir == dbDir || isSystemDir)
            appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "echat");

        _logDir = Path.Combine(appDir, "log");
        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch
        {
            // Last resort: use temp directory
            _logDir = Path.Combine(Path.GetTempPath(), "echat", "log");
            Directory.CreateDirectory(_logDir);
        }

        _logPath = Path.Combine(_logDir, $"echat_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log");
        CleanupOldFiles();
    }

    private static AppLogLevel ParseLevel(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" => AppLogLevel.Error,
        "WARN"  => AppLogLevel.Warn,
        "INFO"  => AppLogLevel.Info,
        "DEBUG" => AppLogLevel.Debug,
        _       => AppLogLevel.Debug
    };

    /// <summary>
    /// When true, each log line is also written to stdout (Console.Out).
    /// Set this on server / Docker deployments so <c>docker logs</c> mirrors the file log.
    /// </summary>
    public bool WriteToConsole { get; set; }

    public void Write(string level, string category, string message)
    {
        if (MinLevel == AppLogLevel.None) return;
        if (ParseLevel(level) > MinLevel) return;

        var line = $"[{DateTimeOffset.Now:O}] [{level}] [{category}] {message}";

        if (WriteToConsole)
            Console.WriteLine(line);

        lock (_lock)
        {
            try
            {
                // Rotate if too large
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxFileSize)
                {
                    var oldPath = _logPath + ".old";
                    if (File.Exists(oldPath)) File.Delete(oldPath);
                    File.Move(_logPath, oldPath);
                }

                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* never crash the app */ }
        }
    }

    public string LogPath => _logPath;

    /// <summary>Platform-correct base app data directory (parent of the "log" folder).</summary>
    public string AppDir => Path.GetDirectoryName(_logDir) ?? ".";

    public byte[] ReadAllBytes()
    {
        lock (_lock)
        {
            var latestLog = GetLatestLogFile();
            if (latestLog == null) return Array.Empty<byte>();
            return File.ReadAllBytes(latestLog);
        }
    }

    public string SuggestedFileName
    {
        get
        {
            var latestLog = GetLatestLogFile();
            return latestLog != null ? Path.GetFileName(latestLog) : "echat.log";
        }
    }

    private string? GetLatestLogFile()
    {
        try
        {
            var files = Directory.GetFiles(_logDir, "echat_*.log")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .ToList();
            return files.FirstOrDefault();
        }
        catch
        {
            return _logPath; // fallback to current session log
        }
    }

    private void CleanupOldFiles()
    {
        try
        {
            var files = Directory.GetFiles(_logDir, "echat_*.log")
                .OrderByDescending(f => f)
                .Skip(MaxLogFiles)
                .ToList();
            foreach (var f in files)
                try { File.Delete(f); } catch { }
        }
        catch { /* never crash the app */ }
    }
}