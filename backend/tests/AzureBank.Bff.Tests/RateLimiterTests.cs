using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Integration tests for the BFF edge rate limiter (ADR-0013). The limiter runs before
/// the auth controller, so it rejects with 429 regardless of the (irrelevant) request
/// body once the per-IP "auth" policy is exceeded — the backend API does not need to run.
/// </summary>
public class RateLimiterTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimiterTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task AuthEndpoint_Rejects_With429_AfterExceedingPerIpLimit()
    {
        // Tighten the auth policy to 2/window so it trips within the test.
        var client = _factory
            .WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:AuthPermitLimit", "2"))
            .CreateClient();

        var statuses = new List<HttpResponseMessage>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add(await client.PostAsJsonAsync(
                "/bff/auth/login", new { azureTag = "probe", password = "x" }));
        }

        // Everything past the 2/window limit must be rejected with 429 (the pre-limit
        // requests fail differently — the backend is down — which is irrelevant here).
        var rejected = statuses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        rejected.Should().NotBeNull("the auth policy must reject once the per-IP limit is exceeded");

        rejected!.Headers.Should().ContainKey("Retry-After");

        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("errorCode").GetString().Should().Be("RATE_LIMIT_EXCEEDED");
    }
}
