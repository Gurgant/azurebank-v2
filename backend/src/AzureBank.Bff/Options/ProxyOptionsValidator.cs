using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Options;

/// <summary>
/// Fails startup on an unusable reverse-proxy trust configuration (ADR-0013). A typo in
/// KnownProxies would otherwise be skipped silently, leaving X-Forwarded-For untrusted and
/// collapsing every client into a single rate-limit partition — the security control is
/// then quietly down with nothing but a log line to notice it. A warning is not enough for
/// a control whose failure mode is invisible; refuse to start instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>A network (KnownIPNetworks) fails the other way too.</b> Every address inside one may name
/// a caller's address, so an entry that is read as a wider or another network than the one meant
/// does not switch the control off: it hands it to strangers. Each entry must therefore be
/// </para>
/// <list type="bullet">
/// <item>a network in CIDR form, <c>address/prefix-length</c>. An address alone is one proxy and
/// belongs in KnownProxies;</item>
/// <item>with a prefix length the address's family has (0 to 32, 0 to 128);</item>
/// <item>not <c>/0</c>, which trusts every address, in either family;</item>
/// <item>not wider than a <c>/8</c>, in either family. The widest private blocks are that wide
/// (10.0.0.0/8; fd00::/8, the assigned half of IPv6 unique-local), so a shorter prefix reaches
/// into addresses that are somebody else's, and it is far more likely a slip of the pen (a 1
/// for a 16) than a network: it would let a large part of the internet name its own
/// address;</item>
/// <item>not an IPv4-mapped IPv6 network (<c>::ffff:a.b.c.d/n</c>). A dual-stack socket reports
/// an IPv4 proxy in that form, and it is matched by the IPv4 network that holds it
/// (<c>TrustedProxyNetworkTests</c>), as the plain form is. The mapped network matches less,
/// and by the socket. Measured on this host's pipeline with this refusal taken out (.NET 10,
/// 2026-10-06): with <c>::ffff:10.0.0.0/104</c> listed the header was read on a connection
/// reported as <c>::ffff:10.0.0.5</c> and not on one reported as <c>10.0.0.5</c>; with
/// <c>::ffff:0:0/96</c>, every IPv4 address, on neither of the two. Such an entry would work
/// or fail, in silence, by how the socket is bound (as first written that day, this said the
/// mapped network "answered false for an address inside it, written either way" and "would
/// match nobody": so the /96 did; the /104 and a /128 did not);</item>
/// <item>written exactly as the framework prints the network it reads. Its parser accepts texts
/// that mean something else than they show (measured the same day): <c>10.0.0.1/8</c> is
/// widened to <c>10.0.0.0/8</c>, <c>010.0.0.0/8</c> is read in octal as <c>8.0.0.0/8</c>,
/// <c>10/8</c> as <c>0.0.0.0/8</c>. The refusal says what was read.</item>
/// </list>
/// <para>
/// <b>Not refused: a public range.</b> A proxy may well have public addresses (a CDN publishes
/// its ranges), and whether a range is the proxy's cannot be read from the range. That is
/// measured on the deployment, and proved there (infra/README.md).
/// </para>
/// <para>
/// <b>Not changed: KnownProxies.</b> Its entries are read by the same lenient parser
/// (<c>010.0.0.0</c> is read as 8.0.0.0) and are checked as they were. A slip there trusts one
/// other address, not a network of them.
/// </para>
/// </remarks>
public sealed class ProxyOptionsValidator : IValidateOptions<ProxyOptions>
{
    /// <summary>The shortest prefix a network of proxies may have, in either address family.</summary>
    public const int ShortestPrefixLength = 8;

    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        if (options.KnownProxies is null)
        {
            return ValidateOptionsResult.Fail("ForwardedHeaders:KnownProxies must not be null.");
        }

        if (options.KnownIPNetworks is null)
        {
            return ValidateOptionsResult.Fail("ForwardedHeaders:KnownIPNetworks must not be null.");
        }

        var errors = new List<string>();

        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                errors.Add($"ForwardedHeaders:KnownProxies contains '{proxy}', which is not a valid IP address.");
            }
        }

        foreach (var network in options.KnownIPNetworks)
        {
            if (RefusalOf(network) is { } refusal)
            {
                errors.Add($"ForwardedHeaders:KnownIPNetworks contains '{network}', which {refusal}");
            }
        }

        if ((options.KnownProxies.Length > 0 || options.KnownIPNetworks.Length > 0) && options.ForwardLimit < 1)
        {
            errors.Add("ForwardedHeaders:ForwardLimit must be >= 1 when KnownProxies or KnownIPNetworks is configured.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// Why <paramref name="entry"/> is not a network of proxies this host will believe, as the end
    /// of a sentence that names it; null for one it will.
    /// </summary>
    private static string? RefusalOf(string? entry)
    {
        const string NotANetwork =
            "is not a network: a network is written address/prefix-length, as 192.0.2.0/24 is. " +
            "The exact address of one proxy belongs in ForwardedHeaders:KnownProxies.";

        var slash = entry?.LastIndexOf('/') ?? -1;
        if (entry is null || slash < 0 || !IPAddress.TryParse(entry.AsSpan(0, slash), out var address))
        {
            return NotANetwork;
        }

        // A whole number, with or without a minus sign, is a prefix length to be judged; anything
        // else after the slash is not one.
        var written = entry.AsSpan(slash + 1);
        var digits = written.Length > 0 && written[0] == '-' ? written[1..] : written;
        if (digits.IsEmpty || digits.ContainsAnyExceptInRange('0', '9'))
        {
            return NotANetwork;
        }

        var longest = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        // One that does not fit an int is out of range like any other.
        if (!int.TryParse(written, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var prefixLength)
            || prefixLength < 0 || prefixLength > longest)
        {
            return $"has a prefix length outside 0 to {longest}.";
        }

        if (prefixLength == 0)
        {
            return "trusts every address: any caller could then name its own address in X-Forwarded-For.";
        }

        if (prefixLength < ShortestPrefixLength)
        {
            return $"is wider than a /{ShortestPrefixLength}: no proxy's network is, and every address in it " +
                   "could name a caller's address in X-Forwarded-For.";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return "is an IPv4-mapped IPv6 network: it is matched only against a connection the socket reports " +
                   "in that form, never against the plain IPv4 one. List the IPv4 network instead, which holds both.";
        }

        if (!IPNetwork.TryParse(entry, out var network))
        {
            return NotANetwork;
        }

        var read = network.ToString();
        return string.Equals(read, entry, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"the framework reads as {read}: write the network as it is printed, so that the one " +
              "trusted is the one meant.";
    }
}
