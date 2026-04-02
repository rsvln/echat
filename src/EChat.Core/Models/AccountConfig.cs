namespace EChat.Core.Models;

/// <summary>
/// Mutable singleton that holds the active account's runtime credentials.
/// Updated by the UI when the user creates/switches/saves an account,
/// so all Core services pick up new values without requiring a restart.
/// </summary>
public class AccountConfig
{
    public string AccountId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;

    // PGP key material — populated after account load / key generation
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }
    public string? KeyPassword { get; set; }
}
