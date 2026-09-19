using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Refuses every request that does not carry the BFF's service credential (ADR-0055), so the API
/// serves one client: the BFF.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The BFF exists so the browser never holds a token. Measured on 2026-09-19 against
/// the running API, with no BFF in the path: <c>POST /api/auth/register</c> answered 201,
/// <c>POST /api/auth/login</c> answered 200 with a bearer token in the body, and with that token
/// <c>GET /api/accounts/{id}/full-number</c> answered 200 with the unmasked number, no PIN ever
/// set or entered. Everything the BFF adds — the cookie session, the level-two gate, its rate
/// limits — was optional for anyone who could reach this host.
/// </para>
/// <para>
/// <b>What is exempt.</b> The health probes, which an orchestrator calls with no credential and
/// which carry nothing about a customer. In Development only, the OpenAPI document and the Scalar
/// page, which a developer opens in a browser; the operations they describe are not exempt.
/// </para>
/// <para>
/// <b>One answer for a missing key and a wrong one</b>, so the response says nothing about how
/// close a guess came, and the comparison runs over SHA-256 digests in constant time, so neither
/// does its duration — the digests also make the two inputs the same length, which
/// <see cref="CryptographicOperations.FixedTimeEquals"/> needs to compare at all.
/// </para>
/// <para>
/// This is the application-level half. In production the API also has no public address (a
/// private network, with the platform's managed identity or mutual TLS between the two hosts);
/// the key stays as the second line behind it.
/// </para>
/// </remarks>
public sealed class ServiceCredentialMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ServiceCredentialMiddleware> _logger;
    private readonly byte[] _expected;
    private readonly bool _isDevelopment;

    public ServiceCredentialMiddleware(
        RequestDelegate next,
        IOptions<ServiceCredentialOptions> options,
        IHostEnvironment environment,
        ILogger<ServiceCredentialMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.BffKey));
        _isDevelopment = environment.IsDevelopment();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExempt(context.Request.Path) || CarriesTheKey(context.Request))
        {
            await _next(context);
            return;
        }

        // Neither the header's value, which is a guess at a secret, nor the path, which carries
        // route parameters and so a customer's handle (ADR-0017). The request line Serilog writes
        // for this same request has the 401 and the correlation id.
        _logger.LogWarning(
            "Refused a {Method} request: no valid service credential", context.Request.Method);

        var problemDetails = new ProblemDetails
        {
            Type = "https://httpstatuses.com/401",
            Title = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = "This API is reached through the BFF.",
            Instance = context.Request.Path,
        };
        problemDetails.Extensions["errorCode"] = ErrorCodes.ServiceCredentialRequired;
        problemDetails.Extensions["traceId"] =
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(
            problemDetails, options: null, contentType: "application/problem+json");
    }

    private bool IsExempt(PathString path) =>
        path.StartsWithSegments("/health")
        || (_isDevelopment
            && (path.StartsWithSegments("/openapi") || path.StartsWithSegments("/scalar")));

    private bool CarriesTheKey(HttpRequest request)
    {
        // Exactly one value: two headers would arrive as two values, and picking one of them is
        // how a smuggled header gets believed.
        var values = request.Headers[ServiceCredentialOptions.HeaderName];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return false;
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        return CryptographicOperations.FixedTimeEquals(presented, _expected);
    }
}

/// <summary>
/// Extension method for registering the middleware.
/// </summary>
public static class ServiceCredentialMiddlewareExtensions
{
    /// <summary>
    /// Refuses requests that do not come from the BFF. Before authentication: a caller that is
    /// not the BFF gets no answer about its token either.
    /// </summary>
    public static IApplicationBuilder UseServiceCredential(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ServiceCredentialMiddleware>();
    }
}
