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
///   Bob includes the token (+ HMAC) in his first email to Alice.
///   Alice verifies the token against her DB, burns it, trusts Bob's public key.
///
/// Only SHA-256(token) is stored in the DB — the plaintext token is shown once
/// and never persisted, so a DB leak cannot be used to forge invites.
/// </summary>
public class InviteService
{
    // Unambiguous base32: no 0, 1, O, I
    private const string Alphabet   = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int    RawLength  = 10;   // → XXXXX-XXXXX (50 bits entropy)
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

    // ── HMAC helpers (static — used by builder and incoming service) ──────────

    /// <summary>
    /// HMAC-SHA256(key=rawToken, data="echat-invite-v1:" + pubKey + ":" + recipientEmail).
    /// Binds the token to a specific public key, preventing key-substitution attacks.
    /// </summary>
    public static string ComputeHmac(string rawToken, string senderPublicKey, string recipientEmail)
    {
        var key  = Encoding.UTF8.GetBytes(Normalize(rawToken));
        var data = Encoding.UTF8.GetBytes(
            $"echat-invite-v1:{senderPublicKey}:{recipientEmail.Trim().ToLowerInvariant()}");
        return Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

    /// <summary>Constant-time HMAC verification.</summary>
    public static bool VerifyHmac(string rawToken, string hmac,
                                   string senderPublicKey, string recipientEmail)
    {
        var expected = ComputeHmac(rawToken, senderPublicKey, recipientEmail);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(hmac.Trim().ToLowerInvariant()));
    }

    // ── Token formatting helpers (public so UI can reformat pasted codes) ─────

    public static string FormatToken(string raw) =>
        raw.Length >= RawLength
            ? raw[..5] + "-" + raw[5..RawLength]
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
