using System.Text;

namespace EChat.Core.Services;

/// <summary>
/// Simple file-based logger for debug diagnostics.
/// Writes to a timestamped .log file in the "log" subdirectory next to the database.
/// Each application launch creates a new file.
/// </summary>
public class FileLogger
{
    private readonly string _logDir;
    private string _logPath;
    private readonly object _lock = new();
    private const int MaxFileSize = 5 * 1024 * 1024; // 5 MB
    private const int MaxLogFiles = 20;

    public FileLogger(string dbPath)
    {
        var dbDir = Path.GetDirectoryName(dbPath) ?? ".";
        var appDir = Path.GetDirectoryName(dbDir) ?? dbDir;
        _logDir = Path.Combine(appDir, "log");
        Directory.CreateDirectory(_logDir);
        _logPath = Path.Combine(_logDir, $"echat_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log");
        CleanupOldFiles();
    }

    public void Write(string level, string category, string message)
    {
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

                var line = $"[{DateTimeOffset.Now:O}] [{level}] [{category}] {message}";
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* never crash the app */ }
        }
    }

    public string LogPath => _logPath;

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