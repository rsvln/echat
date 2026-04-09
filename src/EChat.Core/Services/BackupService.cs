using System.IO.Compression;
using Microsoft.Data.Sqlite;
using static EChat.Core.ServiceCollectionExtensions;

namespace EChat.Core.Services;

public class BackupService
{
    private readonly DatabasePathInfo _dbPathInfo;

    public BackupService(DatabasePathInfo dbPathInfo)
    {
        _dbPathInfo = dbPathInfo;
    }

    public string SuggestedFileName =>
        $"echat_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip";

    /// <summary>
    /// Создаёт ZIP-архив с консистентным снапшотом DB через VACUUM INTO.
    /// VACUUM INTO — атомарная операция SQLite: чекпоинтит WAL, создаёт
    /// дефрагментированную копию в одной транзакции, без блокировки writer'а.
    /// </summary>
    public async Task<byte[]> CreateBackupAsync(CancellationToken ct = default)
    {
        var tempPath = _dbPathInfo.Path + ".backup_tmp";
        try
        {
            // VACUUM INTO работает внутри SQLite — никаких гонок с WAL
            if (File.Exists(tempPath)) File.Delete(tempPath);

            await using var conn = new SqliteConnection($"Data Source={_dbPathInfo.Path}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            // Экранируем путь на случай кавычек в имени файла
            cmd.CommandText = $"VACUUM INTO '{EscapeSqlitePath(tempPath)}';";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }

        try
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("echat.db", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(tempPath);
                await fileStream.CopyToAsync(entryStream, ct);
            }
            return ms.ToArray();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Проверяет, является ли поток валидным бэкапом EChat.
    /// </summary>
    public static bool IsValidBackup(Stream stream)
    {
        var pos = stream.CanSeek ? stream.Position : -1L;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return archive.GetEntry("echat.db") != null;
        }
        catch { return false; }
        finally { if (pos >= 0 && stream.CanSeek) stream.Position = pos; }
    }

    /// <summary>
    /// Восстанавливает DB из ZIP.
    /// Записывает во временный файл → валидирует SQLite-сигнатуру →
    /// закрывает все соединения → удаляет WAL/SHM → атомарно заменяет DB.
    /// После вызова — перезапустить приложение.
    /// </summary>
    public async Task RestoreBackupAsync(Stream backupStream, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(backupStream, ZipArchiveMode.Read);
        var dbEntry = archive.GetEntry("echat.db")
            ?? throw new InvalidDataException("Invalid backup: echat.db not found");

        var tempPath = _dbPathInfo.Path + ".restore_tmp";
        try
        {
            await using var entryStream = dbEntry.Open();
            await using var fileStream = File.Create(tempPath);
            await entryStream.CopyToAsync(fileStream, ct);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }

        // Проверяем, что это действительно SQLite-файл (магические байты)
        await ValidateSqliteFileAsync(tempPath, ct);

        // Закрываем все пулы соединений
        SqliteConnection.ClearAllPools();

        // Удаляем WAL и SHM текущей DB — иначе они применятся к новой базе
        DeleteIfExists(_dbPathInfo.Path + "-wal");
        DeleteIfExists(_dbPathInfo.Path + "-shm");

        // Атомарная замена
        File.Move(tempPath, _dbPathInfo.Path, overwrite: true);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// SQLite файлы начинаются с "SQLite format 3\0" (16 байт)
    private static async Task ValidateSqliteFileAsync(string path, CancellationToken ct)
    {
        // "SQLite format 3" = 15 ASCII chars + null byte
        ReadOnlyMemory<byte> magic = "SQLite format 3\0"u8.ToArray();
        var header = new byte[16];
        await using var fs = File.OpenRead(path);
        var read = await fs.ReadAsync(header, ct);
        if (read < 16 || !header.AsSpan().SequenceEqual(magic.Span))
        {
            File.Delete(path);
            throw new InvalidDataException(
                "The backup does not contain a valid SQLite database.");
        }
    }

    private static string EscapeSqlitePath(string path) =>
        path.Replace("'", "''");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
