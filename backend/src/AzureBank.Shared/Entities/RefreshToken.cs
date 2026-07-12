using System.ComponentModel.DataAnnotations.Schema;

namespace AzureBank.Shared.Entities;

/// <summary>
/// Refresh token entity for JWT token rotation
/// Token is stored HASHED for security - if DB is compromised, tokens are useless
/// Banking standard: 1 hour max expiration
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// SHA256 hash of the token - NEVER store plain text!
    /// Plain token is returned to client once, then only hash is stored
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// Banking standard: 1 hour max
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Set when token is revoked (logout, rotation, suspicious activity)
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Token rotation: points to the new token that replaced this one
    /// Used for theft detection - if revoked token is reused, revoke ALL user tokens
    /// </summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>
    /// Client IP address - validate on refresh for theft detection
    /// </summary>
    public required string IpAddress { get; set; }

    /// <summary>
    /// Browser/client identifier
    /// </summary>
    public required string UserAgent { get; set; }

    // Navigation properties
    public required ApplicationUser User { get; set; }
    public RefreshToken? ReplacedByToken { get; set; }

    // Computed properties (not stored in DB)
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    [NotMapped]
    public bool IsActive => !IsRevoked && !IsExpired;
}
