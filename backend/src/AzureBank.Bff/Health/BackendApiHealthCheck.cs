using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureBank.Bff.Health;

/// <summary>
/// Readiness dependency probe: reports whether the BFF's sole downstream — the backend API — is
/// reachable, by pinging its liveness endpoint through the "BackendApi" named client (so the probe
/// reuses that client's config; its spans are deliberately filtered out of tracing — see
/// AddObservability — so probe cycles don't flood Tempo).
///
/// A backend blip returns <see cref="HealthStatus.Degraded"/>, NOT Unhealthy: a hard readiness
/// failure on a SHARED downstream would let one API hiccup evict EVERY BFF instance at once
/// (cascading failure). Degraded keeps /health/ready at 200 — the BFF process is up and can still
/// serve — while surfacing the downstream state for dashboards and alerts.
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
                : HealthCheckResult.Degraded($"Backend API returned {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Degraded("Backend API unreachable.", ex);
        }
        // Our own timeout fired (not a host shutdown) — degraded, don't throw.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded("Backend API health check timed out.");
        }
    }
}
