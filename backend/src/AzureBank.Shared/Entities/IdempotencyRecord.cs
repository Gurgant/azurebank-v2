using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// One row per (user, endpoint, key): the uniqueness of the composite primary
/// key is the distributed lock that guarantees single execution (ADR-0009).
///
/// Deliberately NOT a BaseEntity: composite PK (no Id), no soft-delete
/// semantics, timestamps managed explicitly by the idempotency service.
/// </summary>
public class IdempotencyRecord
{
    /// <summary>Owning user (from the validated JWT). Scopes the key per user.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Logical endpoint identity: "{METHOD} {RoutePattern.RawText}",
    /// e.g. "POST api/transfers". Never derived from the raw request path
    /// (routing is case/trailing-slash tolerant; raw paths would create
    /// distinct rows for the same logical endpoint).
    /// </summary>
    public required string Endpoint { get; set; }

    /// <summary>Client-generated idempotency key (UUID from the Idempotency-Key header).</summary>
    public Guid Key { get; set; }

    /// <summary>
    /// Fencing/owner token (concurrency token). Regenerated when a stale
    /// Processing row is taken over AND rotated when the record flips to
    /// Executed, so any concurrent claimant or replayed commit fails its
    /// UPDATE (0 rows → DbUpdateConcurrencyException) instead of executing twice.
    /// </summary>
    public Guid ClaimId { get; set; }

    /// <summary>
    /// HMAC-SHA256 (server-side key, never stored in the database) of the raw
    /// request body bytes, lowercase hex. Keyed hashing prevents offline
    /// brute-force of low-entropy payload fields (e.g. the withdraw PIN).
    /// </summary>
    public required string RequestHash { get; set; }

    public IdempotencyStatus Status { get; set; } = IdempotencyStatus.Processing;

    /// <summary>HTTP status code of the stored response (Completed only).</summary>
    public int? ResponseStatusCode { get; set; }

    /// <summary>Serialized response body (Completed only).</summary>
    public string? ResponseBody { get; set; }

    /// <summary>Content type of the stored response (Completed only).</summary>
    public string? ResponseContentType { get; set; }

    /// <summary>Claim timestamp (UTC). Also the reference for staleness takeover.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>End of the idempotency window (UTC). Expired rows are treated as absent.</summary>
    public DateTime ExpiresAt { get; set; }
}
