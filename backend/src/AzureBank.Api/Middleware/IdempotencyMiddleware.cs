using System.Security.Claims;
using AzureBank.Api.Attributes;
using AzureBank.Api.Observability;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using Microsoft.AspNetCore.Http.Features;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Enforces single execution for endpoints marked [RequireIdempotency] (ADR-0009).
///
/// Placement: after UseAuthentication/UseAuthorization (401/403 must
/// short-circuit BEFORE any record is created) and before the endpoint.
///
/// Flow:
///  1. Validate the Idempotency-Key header (UUID) → 400 ProblemDetails.
///  2. Fingerprint the raw body (keyed HMAC) and claim (userId, endpoint, key).
///  3. Replay stored responses (Idempotency-Replayed: true), 409 for
///     in-flight/result-unknown, 422 for key reuse with a different payload.
///  4. On acquisition: mark the record pending-Executed (rides the business
///     commit atomically), buffer the response, then persist it as Completed
///     BEFORE the first byte reaches the client.
///  5. On error: release the claim only if the database truth proves nothing
///     was committed.
/// </summary>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    // Must match [RequestSizeLimit(32_768)] on the guarded endpoints (ADR-0009).
    // Legitimate monetary bodies are < 2 KB; this only bounds the hashing and
    // buffering work so an oversized body cannot be used as a DoS amplifier.
    private const int MaxRequestBodyBytes = 32_768;

    // A successful response larger than this is NOT stored for replay (a retry
    // then gets 409 IDEMPOTENCY_RESULT_UNKNOWN): bounds the persisted row and
    // the replay buffer. Real monetary responses are a few hundred bytes.
    private const int MaxStoredResponseBytes = 64 * 1024;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    // IIdempotencyService is scoped (shares the request DbContext); resolved
    // per-request via method injection.
    public async Task InvokeAsync(HttpContext context, IIdempotencyService idempotency)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<RequireIdempotencyAttribute>() is null)
        {
            await _next(context);
            return;
        }

        var key = ReadKey(context);
        var userId = ReadUserId(context);
        var endpointName = EndpointNameOf(context, endpoint);

        // Reject oversized bodies BEFORE buffering or hashing. The per-endpoint
        // [RequestSizeLimit(32_768)] is an MVC resource filter that runs only
        // once the action executes -- AFTER this middleware -- so without this
        // guard an authenticated caller could make us HMAC (and spool to disk)
        // a body up to Kestrel's ~28 MB default: an idempotency-hash DoS
        // amplifier. The claim is NOT INSERTed on this path (no orphan rows).
        if (context.Request.ContentLength > MaxRequestBodyBytes)
        {
            throw IdempotencyException.PayloadTooLarge();
        }

        // Chunked / unknown-length requests carry no Content-Length to
        // pre-check (the comparison above is lifted-false for null): cap the
        // stream so Kestrel aborts the read at the limit instead of reading to
        // its ~28 MB default. Exceeding it surfaces as a 413
        // BadHttpRequestException, normalized to our ProblemDetails below.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = MaxRequestBodyBytes;
        }

        // Fingerprint the raw body bytes (no JSON canonicalization: it is
        // deterministic, parser-free, and real client retries resend the
        // same bytes). bufferThreshold matches the cap so an accepted body
        // (PIN included) stays in memory and is never spooled to disk.
        context.Request.EnableBuffering(bufferThreshold: MaxRequestBodyBytes);
        string requestHash;
        try
        {
            requestHash = await idempotency.ComputeRequestHashAsync(
                context.Request.Body, context.RequestAborted);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body that blew past MaxRequestBodyBytes mid-read.
            throw IdempotencyException.PayloadTooLarge();
        }
        context.Request.Body.Position = 0;

        var acquisition = await idempotency.TryAcquireAsync(
            userId, endpointName, key, requestHash, context.RequestAborted);

        if (acquisition.IsReplay)
        {
            ApiMetrics.IdempotencyReplays.Add(1);
            await WriteReplayAsync(context, acquisition);
            return;
        }

        var record = acquisition.Record;
        // Fencing token as of claim time: MarkExecutedPending rotates the
        // in-memory value, but releases must be conditional on the value the
        // database actually holds while the record is still Processing.
        var claimTimeClaimId = record.ClaimId;
        idempotency.MarkExecutedPending(record);

        var originalBody = context.Response.Body;
        using var capture = new MemoryStream();
        context.Response.Body = capture;
        try
        {
            await _next(context);
        }
        catch
        {
            // Restore FIRST: the exception handlers at the top of the
            // pipeline must write their ProblemDetails to the real stream,
            // not into the discarded capture buffer.
            context.Response.Body = originalBody;
            await ReleaseQuietlyAsync(idempotency, userId, endpointName, key, claimTimeClaimId);
            throw;
        }
        context.Response.Body = originalBody;

        if (context.Response.StatusCode is >= 200 and < 300)
        {
            if (capture.Length > MaxStoredResponseBytes)
            {
                // An unexpectedly large 2xx: do not buffer it into the record.
                // The business op committed (record is Executed), so a retry
                // gets 409 IDEMPOTENCY_RESULT_UNKNOWN — safe, never a second
                // execution. The response still streams to this caller below.
                _logger.LogWarning(
                    "Idempotency response for {Endpoint}/{Key} is {Bytes} bytes (> {Max}); not storing for replay",
                    endpointName, key, capture.Length, MaxStoredResponseBytes);
            }
            else
            {
                // Persist BEFORE the first byte reaches the client. If this
                // bookkeeping write fails, the business operation still
                // succeeded: log and send the 2xx anyway — the record stays
                // Executed, so retries get 409 IDEMPOTENCY_RESULT_UNKNOWN
                // (never a corrupt replay, never a second execution).
                var body = capture.ToArray();
                try
                {
                    await idempotency.CompleteAsync(
                        record,
                        context.Response.StatusCode,
                        context.Response.ContentType,
                        System.Text.Encoding.UTF8.GetString(body),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to store idempotency response for {Endpoint}/{Key}; record stays Executed",
                        endpointName, key);
                }
            }
        }
        else
        {
            // Non-2xx written without an exception (e.g. model-binding 400):
            // release if provably not committed so the key stays reusable.
            await ReleaseQuietlyAsync(idempotency, userId, endpointName, key, claimTimeClaimId);
        }

        context.Response.ContentLength = capture.Length;
        capture.Position = 0;
        await capture.CopyToAsync(originalBody, context.RequestAborted);
    }

    private static Guid ReadKey(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(IdempotencyConstants.HeaderName, out var values)
            || values.Count == 0 || string.IsNullOrWhiteSpace(values.ToString()))
        {
            throw IdempotencyException.KeyMissing();
        }

        // Exactly one value, a parseable, non-empty UUID. Guid.Empty is
        // rejected: it is the default-initialized value of careless clients,
        // not a generated key.
        if (values.Count > 1 || !Guid.TryParse(values.ToString(), out var key) || key == Guid.Empty)
        {
            throw IdempotencyException.KeyInvalid();
        }

        return key;
    }

    private static Guid ReadUserId(HttpContext context)
    {
        // Same claim resolution as the controllers (GetCurrentUserId).
        var claim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value;

        if (claim is null || !Guid.TryParse(claim, out var userId))
        {
            // Unreachable behind [Authorize]; defensive for misconfiguration.
            throw new AuthenticationException(
                "A valid user identity is required.", ErrorCodes.TokenInvalid);
        }

        return userId;
    }

    private static string EndpointNameOf(HttpContext context, Endpoint endpoint)
    {
        // Route TEMPLATE, never the raw path: /API/Transfers/ and
        // /api/transfers are the same logical endpoint to the router and
        // must be the same idempotency scope.
        if (endpoint is not RouteEndpoint routeEndpoint
            || routeEndpoint.RoutePattern.RawText is not { } pattern)
        {
            throw new InvalidOperationException(
                "[RequireIdempotency] endpoints must be route-based.");
        }

        return IdempotencyService.EndpointNameFor(context.Request.Method, pattern);
    }

    private static async Task WriteReplayAsync(HttpContext context, IdempotencyAcquisition acquisition)
    {
        var record = acquisition.Record;
        context.Response.StatusCode = record.ResponseStatusCode!.Value;
        if (record.ResponseContentType is not null)
        {
            context.Response.ContentType = record.ResponseContentType;
        }
        context.Response.Headers[IdempotencyConstants.ReplayedHeaderName] = "true";

        var body = System.Text.Encoding.UTF8.GetBytes(record.ResponseBody ?? string.Empty);
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private async Task ReleaseQuietlyAsync(
        IIdempotencyService idempotency, Guid userId, string endpointName, Guid key, Guid claimTimeClaimId)
    {
        try
        {
            await idempotency.ReleaseIfNotExecutedAsync(userId, endpointName, key, claimTimeClaimId);
        }
        catch (Exception ex)
        {
            // Never mask the original failure. An unreleased Processing row
            // is safe: it becomes stale and is taken over after
            // ProcessingStaleAfter.
            _logger.LogError(ex,
                "Failed to release idempotency claim for {Endpoint}/{Key}", endpointName, key);
        }
    }
}

/// <summary>
/// Extension methods for IdempotencyMiddleware.
/// </summary>
public static class IdempotencyMiddlewareExtensions
{
    /// <summary>
    /// Adds idempotency enforcement for [RequireIdempotency] endpoints.
    /// Must be placed AFTER UseAuthentication/UseAuthorization.
    /// </summary>
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<IdempotencyMiddleware>();
    }
}
