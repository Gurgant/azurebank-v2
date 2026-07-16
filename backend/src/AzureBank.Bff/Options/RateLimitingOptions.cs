namespace AzureBank.Bff.Options;

/// <summary>
/// Edge rate-limiting configuration (ADR-0013). Bound from appsettings.json
/// "RateLimiting". All limits are per client IP.
/// </summary>
public class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Anti-DoS baseline applied to EVERY request (controllers + the YARP proxy, so it
    /// also covers the /api/* bypass), partitioned per client IP.
    /// </summary>
    public int GlobalPermitLimit { get; set; } = 300;

    /// <summary>Window, in seconds, for the global baseline limiter.</summary>
    public int GlobalWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Tight limit for the sensitive auth endpoints (login / register), partitioned per
    /// client IP — the anti-brute-force / anti-enumeration control (ASVS 6.3.1).
    /// </summary>
    public int AuthPermitLimit { get; set; } = 10;

    /// <summary>Window, in seconds, for the auth limiter.</summary>
    public int AuthWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Segments the auth sliding window is divided into (6 => 10s segments over a 60s
    /// window). More segments track the limit more smoothly; 1 degenerates to a fixed
    /// window and reinstates the 2x boundary burst.
    /// </summary>
    public int AuthSegmentsPerWindow { get; set; } = 6;
}
