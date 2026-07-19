using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Health probes (observability): liveness = process up; readiness = backend API reachable.
/// </summary>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Live_ReturnsHealthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_ReportsDegraded_ButStaysAvailable_WhenBackendUnreachable()
    {
        // Point the readiness probe at a closed port so the dependency check fails
        // deterministically — regardless of whether a real API happens to be listening on the
        // configured port on the dev box. A down backend is DEGRADED, not Unhealthy: readiness
        // must not hard-cascade on a shared downstream, so /health/ready stays 200 while the
        // reported status is "Degraded" (see BackendApiHealthCheck).
        var client = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("BackendApi:BaseUrl", "http://127.0.0.1:59321")).CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Degraded");
    }
}
