using System.Net;
using System.Net.Sockets;

namespace AzureBank.Bff;

/// <summary>
/// The client a request comes from, as one text: what the rate limiters partition on (ADR-0013),
/// and what the demo claim tells the API a visitor's address is.
/// </summary>
/// <remarks>
/// <para>
/// An IPv4 address is its own key. IPv6 end sites are handed a whole /64 (often more), so keying
/// on the full address would let an attacker rotate addresses inside their OWN allocation, with no
/// spoofing required, and a per-address limit would evaporate: an IPv6 address is keyed on its
/// /64 prefix. A connection with no address is <c>unknown</c>, one key for all of them.
/// </para>
/// <para>
/// The address is the connection's, which behind a proxy is the proxy's unless
/// <c>ForwardedHeaders:KnownProxies</c> names it or a network of
/// <c>ForwardedHeaders:KnownIPNetworks</c> holds it: only then is it rewritten from
/// <c>X-Forwarded-For</c>, before anything reads it (<c>Program.cs</c>).
/// </para>
/// <para>
/// One place, because two callers must agree: a limiter that keyed a client one way and a claim
/// that named it another would count the same visitor as two.
/// </para>
/// </remarks>
public static class ClientAddress
{
    /// <summary>The key of the client behind <paramref name="context"/>'s connection.</summary>
    public static string Of(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "unknown";
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8); // zero the interface identifier -> the /64 prefix
        return new IPAddress(bytes) + "/64";
    }
}
