using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EChat.Core.Services;
using Microsoft.Data.Sqlite;
using static EChat.Core.ServiceCollectionExtensions;

namespace EChat.Core.Services;

public class BackupService
{
    private readonly DatabasePathInfo _dbPathInfo;
    private readonly ICredentialProtector _protector;

    // AES-256-GCM encrypted backup magic header ("ECHAT1\n")
    private static ReadOnlySpan<byte> EncryptedMagic => "ECHAT1\n"u8;

    private const int SaltSize       = 16;
    private const int NonceSize      = 12;
    private const int TagSize        = 16;
    private const int KdfIterations  = 300_000;

    public BackupService(DatabasePathInfo dbPathInfo, ICredentialProtector protector)
    {
        _dbPathInfo = dbPathInfo;
        _protector  = protector;
    }

    /// <summary>
    /// Suggested file name for the encrypted backup (includes timestamp).
    /// </summary>
    public string SuggestedFileName =>
        $"echat_backup_{DateTime.Now:yyyyMMdd_HHmmss}.echatbackup";

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an AES-256-GCM encrypted backup protected with <paramref name="password"/>.
    /// Credentials (IMAP password, PGP private key) are stored as plaintext inside
    /// the encrypted container so the backup is portable across devices.
    /// </summary>
    public async Task<byte[]> CreateBackupAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Backup password must not be empty.", nameof(password));

        var zipBytes = await CreatePlainZipAsync(ct);
        return EncryptBytes(zipBytes, password);
    }

    /// <summary>
    /// Creates the raw ZIP snapshot with plaintext credentials.
    /// Intermediate temp DB is always deleted on exit.
    /// </summary>
    private async Task<byte[]> CreatePlainZipAsync(CancellationToken ct)
    {
        var tempPath = _dbPathInfo.Path + ".backup_tmp";
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            // VACUUM INTO — atomic SQLite snapshot; no WAL/locking issues.
            // Pooling=false ensures the connection is fully closed afterwards
            // and the temp file isn't held open when we open it next.
            var vacuumCs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPathInfo.Path,
                Pooling    = false
            }.ToString();
            await using var conn = new SqliteConnection(vacuumCs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{EscapeSqlitePath(tempPath)}';";
            await cmd.ExecuteNonQueryAsync(ct);

            // Overwrite credential fields in the temp DB with plaintext values
            // so the backup is decryptable on any device after password entry.
            await UnprotectCredentialsInTempDbAsync(tempPath, ct);
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
                {
                    var dbEntry = archive.CreateEntry("echat.db", CompressionLevel.Optimal);
                    await using var entryStream = dbEntry.Open();
                    await using var fileStream  = File.OpenRead(tempPath);
                    await fileStream.CopyToAsync(entryStream, ct);
                }

                var attDir = _dbPathInfo.AttachmentsDir;
                if (Directory.Exists(attDir))
                {
                    foreach (var file in Directory.EnumerateFiles(attDir, "*", SearchOption.AllDirectories))
                    {
                        var relPath  = Path.GetRelativePath(attDir, file).Replace('\\', '/');
                        var attEntry = archive.CreateEntry($"attachments/{relPath}", CompressionLevel.NoCompression);
                        await using var aes = attEntry.Open();
                        await using var afs = File.OpenRead(file);
                        await afs.CopyToAsync(aes, ct);
                    }
                }
            }
            return ms.ToArray();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Reads encrypted credential fields from the live DB, decrypts them via the protector,
    /// and writes the plaintext back into the temp DB so the backup is portable.
    /// Uses raw SQLite — avoids going through EF and keeps this method dependency-free.
    /// </summary>
    private async Task UnprotectCredentialsInTempDbAsync(string tempDbPath, CancellationToken ct)
    {
        // Step 1: read protected values from the live DB.
        var accounts = new List<(string Id, string Password, string? PrivateKey)>();

        var mainCs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPathInfo.Path,
            Pooling    = false
        }.ToString();

        await using (var mainConn = new SqliteConnection(mainCs))
        {
            await mainConn.OpenAsync(ct);
            await using var sel = mainConn.CreateCommand();
            sel.CommandText = "SELECT AccountId, Password, PrivateKey FROM Accounts";
            await using var reader = await sel.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                accounts.Add((
                    reader.GetString(0),
                    _protector.Unprotect(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : _protector.Unprotect(reader.GetString(2))
                ));
            }
        }

        if (accounts.Count == 0) return;

        // Step 2: write plaintext values into the temp DB.
        var tempCs = new SqliteConnectionStringBuilder
        {
            DataSource = tempDbPath,
            Pooling    = false
        }.ToString();

        await using var tempConn = new SqliteConnection(tempCs);
        await tempConn.OpenAsync(ct);

        foreach (var (id, password, privateKey) in accounts)
        {
            await using var upd = tempConn.CreateCommand();
            upd.CommandText =
                "UPDATE Accounts SET Password = @pwd, PrivateKey = @pk WHERE AccountId = @id";
            upd.Parameters.AddWithValue("@pwd", password);
            upd.Parameters.AddWithValue("@pk",  (object?)privateKey ?? DBNull.Value);
            upd.Parameters.AddWithValue("@id",  id);
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    // ── Import ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a backup from <paramref name="backupStream"/>.
    /// Automatically detects encrypted vs legacy plain-ZIP format.
    /// For encrypted backups <paramref name="password"/> is required.
    /// After the call the caller must restart the application.
    /// </summary>
    public async Task RestoreBackupAsync(
        Stream backupStream,
        string? password    = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await backupStream.CopyToAsync(ms, ct);
        var data = ms.ToArray();

        byte[] zipBytes;
        if (IsEncryptedBackup(data))
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidDataException(
                    "This backup is password-protected. Please enter the backup password.");
            zipBytes = DecryptBytes(data, password);
        }
        else
        {
            // Legacy plain-ZIP backup — restore without password (backward compat).
            zipBytes = data;
        }

        await RestoreZipAsync(zipBytes, ct);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true for both encrypted (.echatbackup) and legacy plain-ZIP backups.
    /// </summary>
    public static bool IsValidBackup(byte[] data)
    {
        if (IsEncryptedBackup(data)) return true;
        try
        {
            using var ms      = new MemoryStream(data);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            return archive.GetEntry("echat.db") != null;
        }
        catch { return false; }
    }

    /// <summary>Stream overload — kept for backward compatibility.</summary>
    public static bool IsValidBackup(Stream stream)
    {
        var pos = stream.CanSeek ? stream.Position : -1L;
        try
        {
            // Peek for encrypted magic first.
            var header = new byte[EncryptedMagic.Length];
            var read   = stream.Read(header, 0, header.Length);
            if (read == header.Length && header.AsSpan().SequenceEqual(EncryptedMagic))
                return true;

            // Reset and try as plain ZIP.
            if (pos >= 0 && stream.CanSeek) stream.Position = pos;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return archive.GetEntry("echat.db") != null;
        }
        catch { return false; }
        finally { if (pos >= 0 && stream.CanSeek) stream.Position = pos; }
    }

    /// <summary>
    /// Returns true when <paramref name="data"/> is an AES-encrypted backup
    /// (starts with the ECHAT1 magic header).
    /// </summary>
    public static bool IsEncryptedBackup(byte[] data) =>
        data.Length > EncryptedMagic.Length &&
        data.AsSpan(0, EncryptedMagic.Length).SequenceEqual(EncryptedMagic);

    // ── ZIP restore (shared between old and new format) ───────────────────────

    private async Task RestoreZipAsync(byte[] zipBytes, CancellationToken ct)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var dbEntry = archive.GetEntry("echat.db")
            ?? throw new InvalidDataException("Invalid backup: echat.db not found.");

        var tempPath = _dbPathInfo.Path + ".restore_tmp";
        try
        {
            await using var entryStream = dbEntry.Open();
            await using var fileStream  = File.Create(tempPath);
            await entryStream.CopyToAsync(fileStream, ct);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }

        await ValidateSqliteFileAsync(tempPath, ct);

        SqliteConnection.ClearAllPools();
        DeleteIfExists(_dbPathInfo.Path + "-wal");
        DeleteIfExists(_dbPathInfo.Path + "-shm");
        File.Move(tempPath, _dbPathInfo.Path, overwrite: true);

        // Restore attachments
        var attDir     = _dbPathInfo.AttachmentsDir;
        var attEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase)
                        && e.Length > 0)
            .ToList();

        var restoredFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (attEntries.Count > 0)
        {
            Directory.CreateDirectory(attDir);
            foreach (var entry in attEntries)
            {
                var relPath  = entry.FullName["attachments/".Length..].Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.Combine(attDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await using var src = entry.Open();
                await using var dst = File.Create(destPath);
                await src.CopyToAsync(dst, ct);
                restoredFiles[Path.GetFileName(destPath)] = destPath;
            }
        }

        // Fix legacy absolute FilePaths → filename-only
        if (restoredFiles.Count > 0)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPathInfo.Path,
                Pooling    = false
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var fileName in restoredFiles.Keys)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "UPDATE Attachments SET FilePath = @rel " +
                    "WHERE (FilePath LIKE @pattern OR FilePath LIKE @patternBS) " +
                    "AND FilePath != @rel";
                cmd.Parameters.AddWithValue("@rel",       fileName);
                cmd.Parameters.AddWithValue("@pattern",   $"%/{fileName}");
                cmd.Parameters.AddWithValue("@patternBS", $@"%\{fileName}");
                cmd.Transaction = (SqliteTransaction)tx;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    // ── AES-256-GCM encryption ────────────────────────────────────────────────

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with AES-256-GCM.
    /// Output format: magic (7) | salt (16) | nonce (12) | ciphertext (N) | tag (16)
    /// Key derived via PBKDF2-SHA256, 300 000 iterations.
    /// </summary>
    private static byte[] EncryptBytes(byte[] plaintext, string password)
    {
        var salt  = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key   = DeriveKey(password, salt);

        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var magic  = EncryptedMagic.ToArray();
        var result = new byte[magic.Length + SaltSize + NonceSize + ciphertext.Length + TagSize];
        var span   = result.AsSpan();

        magic.CopyTo(span);        span = span[magic.Length..];
        salt.CopyTo(span);         span = span[SaltSize..];
        nonce.CopyTo(span);        span = span[NonceSize..];
        ciphertext.CopyTo(span);   span = span[ciphertext.Length..];
        tag.CopyTo(span);

        return result;
    }

    /// <summary>
    /// Decrypts an AES-256-GCM encrypted backup.
    /// Throws <see cref="InvalidDataException"/> on wrong password (auth tag mismatch).
    /// </summary>
    private static byte[] DecryptBytes(byte[] data, string password)
    {
        var span  = data.AsSpan();
        var magic = EncryptedMagic;

        if (!span.StartsWith(magic))
            throw new InvalidDataException("Invalid backup format.");

        span = span[magic.Length..];

        var salt       = span[..SaltSize].ToArray();  span = span[SaltSize..];
        var nonce      = span[..NonceSize].ToArray(); span = span[NonceSize..];
        var tag        = span[^TagSize..].ToArray();
        var ciphertext = span[..^TagSize].ToArray();

        var key       = DeriveKey(password, salt);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Wrong backup password. Please check the password and try again.");
        }

        return plaintext;
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            KdfIterations, HashAlgorithmName.SHA256, 32);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task ValidateSqliteFileAsync(string path, CancellationToken ct)
    {
        ReadOnlyMemory<byte> magic = "SQLite format 3\0"u8.ToArray();
        var header = new byte[16];
        await using var fs   = File.OpenRead(path);
        var read = await fs.ReadAsync(header, ct);
        if (read < 16 || !header.AsSpan().SequenceEqual(magic.Span))
        {
            File.Delete(path);
            throw new InvalidDataException("The backup does not contain a valid SQLite database.");
        }
    }

    private static string EscapeSqlitePath(string path) => path.Replace("'", "''");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
