using System.Buffers;
using Microsoft.Extensions.Primitives;

namespace AzureBank.Bff;

/// <summary>
/// <c>X-Forwarded-For</c> as a list of addresses and nothing else, rewritten before the framework's
/// forwarded-headers middleware reads it (ADR-0013). It runs only where that middleware does: on a
/// host that lists a proxy, for a connection that has an address (<c>Program.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the framework's own reading is not enough.</b> Measured on .NET 10, 2026-10-06, on this
/// host's pipeline with a network listed and the connection inside it: a caller that wrote
/// <c>::ffff:198.51.100.200%"</c> in the header, to which the proxy appended
/// <c>, 203.0.113.9</c> on the same line, was taken for <c>198.51.100.200</c>, and one that wrote
/// <c>2001:db8:dead:beef::1%"</c> for that address's /64: an address of the caller's choosing, a
/// new one a request, and with it a new budget at both rate limits and a new day's allowance of
/// demo copies. Two lenient readings add up to it. The framework splits the header as a list of
/// quoted strings, so the comma after a quotation mark that is never closed does not end the
/// entry; and <c>System.Net.IPEndPoint.TryParse</c> takes everything after a <c>%</c> for an IPv6
/// zone and drops it, whatever it holds. The entry the framework then read was the caller's
/// address, with the proxy's own entry as its zone. A quotation mark alone did less and still
/// something: the last entry no longer read as an address, so the caller was counted as the
/// proxy itself, a second budget shared by everybody who wrote one.
/// </para>
/// <para>
/// <b>An allow-list, not a repair</b> (as <c>CorrelationIdMiddleware</c> has one). The header's
/// lines are one list, in their order; the list is split at EVERY comma, a quotation mark being
/// a character like another; blanks and tabs around an entry are dropped, and an empty entry
/// with them. An entry made only of the characters an address, with or without a port, is
/// written with (<c>0-9 a-f A-F . : [ ]</c>) is kept as it is written. Any other entry is
/// replaced by <see cref="NotAnAddress"/>, which no parser reads as an address, so the framework
/// stops there as it does at that word when a proxy writes it. What the framework is handed
/// holds no quotation mark and no zone, and its commas are exactly the list's.
/// </para>
/// <para>
/// <b>What this does not decide.</b> Which entries are believed is still the framework's:
/// <c>ForwardedHeaders:ForwardLimit</c> of them from the end, each only while the hop before it is
/// a listed proxy. Whether the entry at the end is the caller's own address is the proxy's doing,
/// and is proved on the deployment (infra/README.md): a proxy that passes a caller's header on
/// without appending to it leaves the caller's own last entry there.
/// </para>
/// </remarks>
public static class StrictForwardedFor
{
    /// <summary>
    /// The header, by the name the framework's middleware reads it under
    /// (<c>ForwardedHeadersDefaults.XForwardedForHeaderName</c>, which is not a constant; a test
    /// holds the two equal, and that this host leaves the middleware's name as it is).
    /// </summary>
    public const string HeaderName = "X-Forwarded-For";

    /// <summary>What stands in the list for an entry that is not written as an address.</summary>
    public const string NotAnAddress = "unknown";

    private static readonly SearchValues<char> AddressCharacters =
        SearchValues.Create("0123456789abcdefABCDEF.:[]");

    /// <summary>
    /// Replaces the request's <c>X-Forwarded-For</c> lines by the one line <see cref="Of"/> makes
    /// of them, and removes the header when nothing is left. A request without the header is left
    /// as it is.
    /// </summary>
    public static void Rewrite(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue(HeaderName, out var lines))
        {
            return;
        }

        var strict = Of(lines);
        if (strict.Length == 0)
        {
            headers.Remove(HeaderName);
        }
        else
        {
            headers[HeaderName] = strict;
        }
    }

    /// <summary>
    /// The entries of <paramref name="lines"/> as one list, separated by a comma and a blank; the
    /// empty text when there is none.
    /// </summary>
    public static string Of(StringValues lines)
    {
        var entries = new List<string>();
        foreach (var line in lines)
        {
            foreach (var written in (line ?? string.Empty).Split(','))
            {
                var entry = written.AsSpan().Trim(" \t");
                if (entry.IsEmpty)
                {
                    continue;
                }

                entries.Add(entry.ContainsAnyExcept(AddressCharacters) ? NotAnAddress : entry.ToString());
            }
        }

        return string.Join(", ", entries);
    }
}
