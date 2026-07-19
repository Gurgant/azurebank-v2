using System.Net;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Health probes (observability): liveness = process up, readiness = DB reachable.
/// </summary>
public class HealthEndpointTests : IntegrationTestBase
{
    public HealthEndpointTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Live_ReturnsHealthy()
    {
        var response = await Client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_ReturnsHealthy_WhenDatabaseReachable()
    {
        var response = await Client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
