using AzureBank.Bff.Middleware;

namespace AzureBank.Bff.Http;

/// <summary>
/// Puts the request's correlation id on the BFF's own calls to the API, so that the API adopts it
/// and both hosts log a sign-in, a PIN check or a renewal under one id, as they already did a
/// proxied request (SCA-RTS Art. 29(2)(a)). The proxy's road needs nothing from here:
/// <see cref="CorrelationIdMiddleware"/> writes the id onto the request's headers, and YARP
/// forwards them.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads <see cref="CorrelationIdMiddleware.CurrentId"/>, never the request.</b> A renewal
/// that <c>TokenRefresher</c> starts with <c>Task.Run</c> carries the execution context of the
/// request that started it, and can still be sending after that request has ended, when its
/// headers are not to be read.
/// </para>
/// <para>
/// <b>Outside a request it adds nothing, and invents nothing.</b> The grant revoker's workers and
/// the session-stamp watcher start with the host, so their calls carry no id, and the API gives
/// each one of its own, as it does any call that arrives without one.
/// </para>
/// </remarks>
internal sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (CorrelationIdMiddleware.CurrentId is { } correlationId)
        {
            // Replaced, never added to: one call, one id.
            request.Headers.Remove(CorrelationIdMiddleware.CorrelationIdHeader);
            request.Headers.TryAddWithoutValidation(
                CorrelationIdMiddleware.CorrelationIdHeader, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
