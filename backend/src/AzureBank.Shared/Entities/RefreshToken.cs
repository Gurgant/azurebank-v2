using System.ComponentModel.DataAnnotations.Schema;
using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// The BFF session's grant: one reusable refresh token per session (06 §3, §4.1).
/// Token is stored HASHED (SHA-256) - if the DB is compromised, tokens are useless.
/// It lives <c>Jwt:RefreshTokenLifetimeMinutes</c> (60 by default) from issue, a lifetime fixed then
/// and never extended: a renewal reads this row and writes nothing, so the grant does not rotate
/// and nothing from one sign-in outlives it. (Until PR-1 each renewal rotated the token and granted
/// another seven days.)
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
    /// Absolute expiry of the grant: issued-at + <c>Jwt:RefreshTokenLifetimeMinutes</c>, fixed at
    /// issue and never extended. Enforced on every read: RenewAsync treats an expired grant as
    /// invalid, and caps every access token it mints at this instant.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the grant was revoked. The API writes the revoking request's <c>ReceivedAt</c> stamp
    /// here, never the commit time, so it can be compared with a renewal's own stamp: a renewal
    /// received after a <see cref="RefreshTokenRevokedReason.SessionEnded"/> revoke is the tripwire
    /// (06 §4.3). The migration that added <see cref="RevokedReason"/> wrote the database's clock on
    /// the rows it revoked, which no comparison reads.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Why the grant was revoked; null on a legacy row revoked before the column existed.
    /// Stored as the member's name. See <see cref="RefreshTokenRevokedReason"/>.
    /// </summary>
    public RefreshTokenRevokedReason? RevokedReason { get; set; }

    /// <summary>
    /// LEGACY: the successor that replaced this row when renewal still rotated. Nothing writes it
    /// since PR-1; it stays for the rows written before, and the cleanup sweep still unlinks it
    /// before deleting an expired row.
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
    /// SQL Server rowversion, DB-generated. It guarded rotation while renewal rotated; since PR-1 a
    /// renewal writes nothing, so a grant's rowversion changes only when it is revoked, which is
    /// what the "renewal writes nothing" oracle reads (06 §10 O2g).
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
