namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Result of a successful refresh-token rotation: a new short-lived access token AND a NEW
/// refresh token. The presented refresh token is now revoked — the caller MUST replace its
/// stored value with this one. Replaying the old token is rejected and, because it is now a
/// revoked token, trips reuse-detection (revoking the whole active token set for the user).
/// </summary>
public class RefreshResponse
{
    /// <summary>Fresh JWT access token (short-lived, per JwtOptions.ExpirationMinutes).</summary>
    public required string AccessToken { get; set; }

    /// <summary>The NEW refresh token (plaintext, shown once). Store it; discard the old one.</summary>
    public required string RefreshToken { get; set; }

    /// <summary>Absolute expiry of the new access token (from its own exp claim).</summary>
    public required DateTime ExpiresAt { get; set; }
}
