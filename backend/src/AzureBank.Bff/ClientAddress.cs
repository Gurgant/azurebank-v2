using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

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
/// <para>
/// A LOG LINE TAKES <see cref="CodeOf"/>, NEVER THE TEXT ITSELF: an address is personal data, and
/// a log is kept for weeks. The code is HMAC-SHA256 under the service key, the one secret this
/// host holds in every mode, cut to 8 bytes: the same client gives the same code, so a pattern
/// can be seen, and without the key nobody can hash every IPv4 address to find which one it was.
/// The label keeps the code from being anything else made with that key. Rotating the key starts
/// new codes (ADR-0055 D4), so one client reads as two across a rotation.
/// </para>
/// </remarks>
public static class ClientAddress
{
    private const string CodePurpose = "rate-limit-log:";

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

    /// <summary>
    /// The one-way code of <paramref name="clientAddress"/> under <paramref name="key"/>: 16 hex
    /// characters.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key or the address is null, empty or white space. HMAC accepts an empty key, and what
    /// it gives then is a hash anybody can compute.
    /// </exception>
    public static string CodeOf(string key, string clientAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAddress);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(CodePurpose + clientAddress));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
