using System.ComponentModel.DataAnnotations.Schema;

namespace AzureBank.Shared.Entities;

/// <summary>
/// Refresh token entity for JWT token rotation (RFC 9700 / OWASP; see ADR-0021).
/// Token is stored HASHED (SHA-256) - if the DB is compromised, tokens are useless.
/// Lifetime is a sliding window of JwtOptions.RefreshTokenExpirationDays (default 7 days):
/// each rotation issues a fresh successor, and the effective session cap is enforced by the
/// BFF's inactivity/absolute timeouts, not by this row.
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
    /// Absolute expiry of THIS token (issued-at + RefreshTokenExpirationDays). Enforced on
    /// every read: RotateAsync treats an expired token as invalid.
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

    /// <summary>
    /// Optimistic-concurrency token (SQL Server rowversion, DB-generated). Guards rotation:
    /// two concurrent refreshes of the SAME token cannot both commit — the loser's UPDATE
    /// matches zero rows and EF raises DbUpdateConcurrencyException — so the rotation chain
    /// can never fork (a fork would silently defeat reuse-detection). Mirrors Account.RowVersion.
    /// </summary>
    public byte[] RowVersion { get; set; } = null!;

    // Navigation properties.
    // User is nullable-to-CONSTRUCT on purpose: issuance sets only the UserId foreign key and
    // never links this navigation, because DbContext.Add cascades an INSERT to any non-null
    // reachable principal — and a caller may hand us a DETACHED user (the login path detaches
    // it after the atomic lockout reset). The property is still populated on load via Include.
    public ApplicationUser? User { get; set; }
    public RefreshToken? ReplacedByToken { get; set; }

    // Computed properties (not stored in DB)
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    [NotMapped]
    public bool IsActive => !IsRevoked && !IsExpired;
}
