namespace AzureBank.Shared.Options;

/// <summary>
/// Configuration options for the idempotency mechanism (ADR-0009).
/// Binds to the "Idempotency" section in appsettings.json.
/// </summary>
public class IdempotencyOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "Idempotency";

    /// <summary>
    /// Server-side key for HMAC-SHA256 request fingerprinting.
    /// MUST be configured (user-secrets / environment), never committed and
    /// never stored in the database: a plain hash of a body containing a
    /// 6-digit PIN would be an offline brute-force oracle for anyone with
    /// database read access.
    /// </summary>
    public string HashKey { get; set; } = string.Empty;

    /// <summary>
    /// Idempotency window: how long a key stays recognized (replay / conflict
    /// detection). Expired records are treated as absent and cleaned up.
    /// Default: 24 hours.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Age after which a Processing record is considered abandoned (crashed
    /// before committing anything — provably safe to take over, because a
    /// committed operation would have flipped the record to Executed).
    /// Must comfortably exceed any request timeout. Default: 10 minutes.
    /// </summary>
    public TimeSpan ProcessingStaleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Interval between background cleanup sweeps of expired records.
    /// Default: 1 hour (first sweep runs one interval after startup).
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}
