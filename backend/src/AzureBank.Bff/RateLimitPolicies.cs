namespace AzureBank.Bff;

/// <summary>Named rate-limiter policies (ADR-0013).</summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// Tight per-IP limit for the sensitive auth endpoints. Referenced by the BFF auth
    /// controller ([EnableRateLimiting]) and by the dedicated YARP /api/auth routes
    /// (RateLimiterPolicy in appsettings.json — keep the literal there in sync with this).
    /// </summary>
    public const string Auth = "auth";
}
