namespace AzureBank.Bff.Options;

/// <summary>
/// Reverse-proxy trust configuration (ADR-0013). Bound from appsettings.json
/// "ForwardedHeaders". With <see cref="KnownProxies"/> and <see cref="KnownIPNetworks"/> both
/// empty (the default) the BFF is the edge: X-Forwarded-For is NOT honoured and the rate limiter
/// partitions on the direct connection IP. Deployments behind a proxy/LB MUST list the proxy
/// here, by its exact addresses or by the networks its addresses come from. (Until 2026-10-06
/// this said "MUST list the proxy IPs here": exact addresses were the only form.)
/// </summary>
public class ProxyOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// IPs of the trusted proxies in front of the BFF. X-Forwarded-For is honoured only
    /// when this or <see cref="KnownIPNetworks"/> is non-empty, and only for hops these proxies
    /// appended — trusting the header from any source would let an attacker rotate fake IPs past
    /// the limiter.
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>
    /// Networks of trusted proxies, each in CIDR form (<c>192.0.2.0/24</c>, <c>fd00::/8</c>): a
    /// connection from any address inside one is a trusted proxy's, exactly as one from an
    /// address of <see cref="KnownProxies"/> is. For a platform that may move its proxy inside a
    /// range it owns: an exact address stops matching the day it moves, in silence, and every
    /// client is one rate-limit partition again. Named as the framework names its own list
    /// (<c>ForwardedHeadersOptions.KnownIPNetworks</c>). What is refused, and why:
    /// <see cref="ProxyOptionsValidator"/>.
    /// </summary>
    public string[] KnownIPNetworks { get; set; } = [];

    /// <summary>
    /// Number of trusted proxy hops to walk back through X-Forwarded-For. Never more than the
    /// proxies there really are: each hop is believed only while the one before it is a listed
    /// proxy, so with a limit of 2 and one proxy a caller whose own address is inside a listed
    /// network is taken for the second proxy, and the entry it wrote itself is believed
    /// (<c>TrustedProxyNetworkTests.ForwardLimit_IsStillTheNumberOfHopsBelieved</c>).
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}
