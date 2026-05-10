using System.Security.Cryptography;
using System.Text;
using EChat.Core.Data;
using EChat.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EChat.Core.Services;

/// <summary>
/// Manages one-time invite tokens for secure key-exchange initiation.
///
/// Flow:
///   Alice generates a token → shares it out-of-band with Bob.
///   Bob encrypts his public key with AES-256-GCM(key=SHA256(token)) and includes
///   the ciphertext in the Initial-Contact-Key-Exchange email header.
///   Alice decrypts the pubKey using the token, verifies it against her DB, burns it.
///
/// Only SHA-256(token) is stored in the DB — the plaintext token is shown once
/// and never persisted, so a DB leak cannot be used to forge invites.
/// The pubKey is never transmitted in plaintext, eliminating passive observation attacks.
/// </summary>
public class InviteService
{
    // Unambiguous base32: no 0, 1, O, I
    private const string Alphabet   = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int    RawLength  = 30;   // → XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX (150 bits entropy)
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;

    public InviteService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ── Generation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a fresh one-time invite, stores its hash, returns the plaintext token.
    /// The formatted token (XXXXX-XXXXX) is shown to the user once and never stored.
    /// </summary>
    public async Task<(string formattedToken, PendingInvite invite)> GenerateAsync(
        string accountId, string? label = null, TimeSpan? ttl = null)
    {
        var raw   = GenerateRaw();
        var hash  = HashRaw(raw);
        var now   = DateTimeOffset.UtcNow;

        var invite = new PendingInvite
        {
            TokenId   = Guid.NewGuid().ToString(),
            TokenHash = hash,
            AccountId = accountId,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl ?? DefaultTtl),
            Label     = label
        };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync();

        return (FormatToken(raw), invite);
    }

    // ── Verification ──────────────────────────────────────────────────────────

    /// <summary>
    /// Checks that <paramref name="rawToken"/> matches a valid, non-expired, unused invite
    /// belonging to <paramref name="accountId"/>.  On success burns the token (sets UsedAt).
    /// </summary>
    public async Task<bool> VerifyAndConsumeAsync(string rawToken, string accountId)
    {
        var normalized = Normalize(rawToken);
        if (normalized.Length != RawLength) return false;

        var hash = HashRaw(normalized);
        var now  = DateTimeOffset.UtcNow;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var invite = await db.PendingInvites.FirstOrDefaultAsync(i =>
            i.TokenHash == hash &&
            i.AccountId == accountId &&
            i.UsedAt    == null &&
            i.ExpiresAt >  now);

        if (invite == null) return false;

        invite.UsedAt = now;
        await db.SaveChangesAsync();
        return true;
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<PendingInvite>> GetPendingAsync(string accountId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = DateTimeOffset.UtcNow;
        return await db.PendingInvites
            .Where(i => i.AccountId == accountId && i.UsedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task RevokeAsync(string tokenId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await db.PendingInvites.Where(i => i.TokenId == tokenId).ExecuteDeleteAsync();
    }

    // ── AES-256-GCM key exchange (static — used by builder and incoming service) ─

    /// <summary>
    /// Encrypts <paramref name="pubKey"/> using AES-256-GCM with a key derived from
    /// SHA-256(<paramref name="rawToken"/>). Returns base64(nonce[12] + tag[16] + cipher).
    /// Only the holder of the plaintext token can decrypt — the token itself never travels.
    /// </summary>
    public static string EncryptPubKey(string pubKey, string rawToken)
    {
        var key    = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(rawToken)));
        var plain  = Encoding.UTF8.GetBytes(pubKey);
        var nonce  = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag    = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        // layout: nonce(12) + tag(16) + ciphertext
        var result = new byte[28 + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, 12);
        cipher.CopyTo(result, 28);
        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypts a value produced by <see cref="EncryptPubKey"/>.
    /// Throws <see cref="CryptographicException"/> if the token is wrong or data is tampered.
    /// </summary>
    public static string DecryptPubKey(string encryptedBase64, string rawToken)
    {
        var key    = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(rawToken)));
        var data   = Convert.FromBase64String(encryptedBase64);
        var nonce  = data[..12];
        var tag    = data[12..28];
        var cipher = data[28..];
        var plain  = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    // ── Token formatting helpers (public so UI can reformat pasted codes) ─────

    public static string FormatToken(string raw) =>
        raw.Length >= RawLength
            ? raw[..5]  + "-" + raw[5..10]  + "-" + raw[10..15] + "-"
            + raw[15..20] + "-" + raw[20..25] + "-" + raw[25..30]
            : raw;

    public static string Normalize(string token) =>
        token.Replace("-", "").ToUpperInvariant();

    // ── Private ───────────────────────────────────────────────────────────────

    private static string GenerateRaw()
    {
        var bytes = RandomNumberGenerator.GetBytes(RawLength);
        return new string(bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray());
    }

    public static string HashRaw(string normalized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
}
