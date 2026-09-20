using AzureBank.Bff.Options;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Http;

/// <summary>
/// Attaches the service credential (ADR-0055) to the BFF's own calls to the API, per request, and
/// only where the key may travel. The proxy's transform does the same for the other road.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per request and not once on the client.</b> The key used to be a default header on the
/// named <c>BackendApi</c> client, set when that client was configured. The client reads
/// <c>BackendApi:BaseUrl</c> live on every <c>CreateClient</c>, and the options validator that
/// refuses an unsafe address on reload complains without restoring anything — so after a reload the
/// next login would have carried the key to whatever that value then named. Measured before this
/// class existed, with the configuration reloaded to <c>http://api.internal:5068</c>: the login
/// that followed reached that destination with the key on it.
/// </para>
/// <para>
/// <b>It throws rather than sending less</b>, for the reason
/// <see cref="Transforms.BearerTokenTransformProvider"/> gives: withholding one secret while the
/// request goes on carrying the session's own is not a boundary. Nothing is sent to an address
/// that is neither <c>https</c> nor loopback.
/// </para>
/// </remarks>
internal sealed class ServiceCredentialHandler : DelegatingHandler
{
    private readonly IOptionsMonitor<ServiceCredentialOptions> _options;
    private readonly ILogger<ServiceCredentialHandler> _logger;

    public ServiceCredentialHandler(
        IOptionsMonitor<ServiceCredentialOptions> options, ILogger<ServiceCredentialHandler> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Absolute by now: HttpClient resolves BaseAddress before the pipeline runs, so this is
        // the address the request is actually going to.
        if (!ServiceCredentialTransport.IsSafe(request.RequestUri))
        {
            _logger.LogError("Not sent: the API address is neither https nor loopback");
            throw new InvalidOperationException(
                "The API address is neither https nor loopback, so nothing is sent to it.");
        }

        request.Headers.Remove(ServiceCredentialOptions.HeaderName);
        request.Headers.TryAddWithoutValidation(
            ServiceCredentialOptions.HeaderName, _options.CurrentValue.BffKey);

        return base.SendAsync(request, cancellationToken);
    }
}
