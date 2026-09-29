using System.Net;
using AzureBank.Api.Attributes;
using AzureBank.Shared.Options;
using AzureBank.Shared.Utilities;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Options of <see cref="TokenRoadMiddleware"/>. Deliberately NOT bound to configuration: nothing a
/// deployment sets can widen the road.
/// </summary>
public sealed class TokenRoadOptions
{
    /// <summary>
    /// Accept a request whose <c>Connection.RemoteIpAddress</c> is null, as if it were loopback.
    /// False unless code sets it, and only the test host does: <c>TestServer</c> has no socket, so
    /// every request it carries has no address (ADR-0057 F1). In a deployment a null address means
    /// a transport with no IP at all (a Unix socket, a named pipe), which is not the road
    /// ADR-0057 §3 argues from, so it is refused like any other address.
    /// </summary>
    public bool AcceptMissingRemoteAddress { get; set; }
}

/// <summary>
/// Answers 404 to a token endpoint (<see cref="TokenEndpointAttribute"/>) unless the request came
/// over loopback AND carries exactly one <see cref="ServiceCredentialOptions.TokenRoadHeaderName"/>
/// whose value is <see cref="ServiceCredentialOptions.TokenRoadMarker"/> (ADR-0057 §4.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both.</b> The argument that lets a grant stop rotating (ADR-0057 §3) is that presenting
/// one takes the grant, the service key AND a socket on the API's loopback interface, which only
/// code inside the replica has. Loopback alone does not hold that line: the BFF's proxy also
/// reaches the API over loopback, and adds the key to every browser request. So the BFF's own
/// client adds the marker, its proxy strips any copy a browser sends, and this refuses a request
/// without exactly one: picking one of two is how a smuggled header gets believed — the rule the
/// key follows in <see cref="ServiceCredentialMiddleware"/>. Two can arrive as two values, or, over
/// a real socket, as ONE value: SocketsHttpHandler writes two values of a header on one line,
/// "bff, bff", and Kestrel hands the line over whole (measured by the pre-review of PR-1). The
/// key's rule holds either way because its value is compared; so the marker's value is compared
/// too, exactly.
/// </para>
/// <para>
/// <b>404, not 401 or 403,</b> so a caller off the road is told only what an unknown path tells it.
/// The response is left empty for the status-code pages to fill, which is what they do for a path
/// that matches no route. After <see cref="ServiceCredentialMiddleware"/>: a caller without the key
/// still gets that middleware's 401, logged as the refusal it is.
/// </para>
/// <para>
/// <b>An IPv4 address mapped into IPv6</b> (<c>::ffff:127.0.0.1</c>, what a dual-mode socket reports
/// for an IPv4 client) is unmapped first, so loopback over either family counts.
/// </para>
/// </remarks>
public sealed class TokenRoadMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TokenRoadMiddleware> _logger;
    private readonly bool _acceptMissingRemoteAddress;

    public TokenRoadMiddleware(
        RequestDelegate next,
        IOptions<TokenRoadOptions> options,
        ILogger<TokenRoadMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _acceptMissingRemoteAddress = options.Value.AcceptMissingRemoteAddress;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<TokenEndpointAttribute>() is null)
        {
            await _next(context);
            return;
        }

        var refusal = RefusalOf(context);
        if (refusal is null)
        {
            await _next(context);
            return;
        }

        // Warning, because a caller holding the key off the BFF's road is either a misconfigured
        // deployment or somebody who has the key. Neither the address nor the path: the reason
        // names what was missing, and the request line Serilog writes carries the 404.
        _logger.LogWarning(
            "Refused a {Method} request to a token endpoint: {Reason}",
            LogSanitizer.Sanitize(context.Request.Method), refusal);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private string? RefusalOf(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            if (!_acceptMissingRemoteAddress)
            {
                return "no remote address";
            }
        }
        else if (!IsLoopback(address))
        {
            return "not loopback";
        }

        var markers = context.Request.Headers[ServiceCredentialOptions.TokenRoadHeaderName];
        if (markers.Count > 1)
        {
            return "more than one token-road marker";
        }

        if (markers.Count == 0 || string.IsNullOrEmpty(markers[0]))
        {
            return "no token-road marker";
        }

        // The one value must be the marker itself: two markers sent over a socket arrive as ONE
        // value, "bff, bff", which a count of values takes for one.
        if (!string.Equals(markers[0], ServiceCredentialOptions.TokenRoadMarker, StringComparison.Ordinal))
        {
            return "a token-road marker that is not the BFF's";
        }

        return null;
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}

/// <summary>
/// Extension method for registering the middleware.
/// </summary>
public static class TokenRoadMiddlewareExtensions
{
    /// <summary>
    /// Refuses the token endpoints to anything but the BFF's own client over loopback. After the
    /// service credential, before authentication.
    /// </summary>
    public static IApplicationBuilder UseTokenRoad(this IApplicationBuilder builder) =>
        builder.UseMiddleware<TokenRoadMiddleware>();
}
