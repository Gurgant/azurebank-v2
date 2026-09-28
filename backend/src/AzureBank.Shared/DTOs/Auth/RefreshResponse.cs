namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Result of a successful renewal: a new short-lived access token and its expiry, and nothing else.
/// The grant that was presented stays valid and unchanged: a renewal writes nothing, so it
/// hands back no refresh token, and the caller keeps the one it holds.
/// </summary>
public class RefreshResponse
{
    /// <summary>
    /// Fresh JWT access token: <c>Jwt:ExpirationMinutes</c> long, or shorter when the grant expires
    /// sooner, since no access token outlives the grant it came from.
    /// </summary>
    public required string AccessToken { get; set; }

    /// <summary>Absolute expiry of the new access token (from its own exp claim).</summary>
    public required DateTime ExpiresAt { get; set; }
}
