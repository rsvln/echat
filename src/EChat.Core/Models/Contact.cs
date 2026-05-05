namespace EChat.Core.Models;

public class Contact
{
    public required string AccountId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? PublicKey { get; set; }
    public string? KeyFingerprint { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string? ProtocolVersion { get; set; }
    public bool SupportsBatching { get; set; }

    // ── Contact management ────────────────────────────────────────────────────
    /// <summary>When this contact was first added.</summary>
    public DateTimeOffset? AddedAt { get; set; }

    /// <summary>User-written note ("met at conf 2025", "work colleague").</summary>
    public string? Notes { get; set; }

    /// <summary>True = incoming messages from this contact are silently discarded.</summary>
    public bool IsBlocked { get; set; }

    /// <summary>Timestamp of the last block action (for audit).</summary>
    public DateTimeOffset? BlockedAt { get; set; }

    // ── Phase 2 reserved: per-contact inbound key pair ────────────────────────
    // Alice generates a key pair per contact and advertises LocalPublicKey to them.
    // They encrypt to Alice using LocalPublicKey; Alice decrypts with LocalPrivateKey.
    // Setting LocalKeyRevokedAt stops decryption → cryptographic "unsubscribe".
    public string? LocalPublicKey { get; set; }
    public string? LocalPrivateKey { get; set; }
    public DateTimeOffset? LocalKeyRevokedAt { get; set; }
}