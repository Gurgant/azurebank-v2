namespace AzureBank.Bff.Options;

/// <summary>
/// Reverse-proxy trust configuration (ADR-0013). Bound from appsettings.json
/// "ForwardedHeaders". An empty <see cref="KnownProxies"/> (the default) means the BFF is
/// the edge: X-Forwarded-For is NOT honoured and the rate limiter partitions on the direct
/// connection IP. Deployments behind a proxy/LB MUST list the proxy IPs here.
/// </summary>
public class ProxyOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// IPs of the trusted proxies in front of the BFF. X-Forwarded-For is honoured only
    /// when this is non-empty, and only for hops these proxies appended — trusting the
    /// header from any source would let an attacker rotate fake IPs past the limiter.
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Number of trusted proxy hops to walk back through X-Forwarded-For.</summary>
    public int ForwardLimit { get; set; } = 1;
}
