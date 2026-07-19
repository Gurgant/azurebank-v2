using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureBank.Bff.Health;

/// <summary>
/// Readiness probe: the BFF is only "ready" when its sole downstream — the backend API — is
/// reachable. Pings the API's liveness endpoint through the "BackendApi" named client, so the
/// probe reuses that client's dev-cert handling and shows up as a BFF→API span in the trace.
/// </summary>
public sealed class BackendApiHealthCheck : IHealthCheck
{
    private const string ClientName = "BackendApi";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClientFactory;

    public BackendApiHealthCheck(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Bound the probe so a hung backend can't stall the readiness endpoint.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(ClientName);
            using var response = await client.GetAsync("/health/live", timeout.Token);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Backend API reachable.")
                : HealthCheckResult.Unhealthy($"Backend API returned {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Unhealthy("Backend API unreachable.", ex);
        }
        // Our own timeout fired (not a host shutdown) — report unhealthy rather than throw.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Backend API health check timed out.");
        }
    }
}
