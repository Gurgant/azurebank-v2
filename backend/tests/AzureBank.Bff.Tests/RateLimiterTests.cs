using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Integration tests for the BFF edge rate limiter (ADR-0013). The limiter runs before the
/// auth controller and before the YARP proxy, so it rejects with 429 regardless of the
/// (irrelevant) request body and without the backend API running — pre-limit responses fail
/// for their own reasons (400/502), which these tests deliberately ignore.
///
/// The client IP is injected per-request via <see cref="FakeRemoteIpStartupFilter"/>, because
/// TestServer leaves Connection.RemoteIpAddress null — without it every request shares one
/// partition and the per-IP assertions below would prove nothing.
/// </summary>
public class RateLimiterTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimiterTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(int authPermitLimit) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "RateLimiting:AuthPermitLimit", authPermitLimit.ToString(CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
        }).CreateClient();

    private static async Task<List<HttpResponseMessage>> HammerAsync(
        HttpClient client, string path, string clientIp, int count, string? forwardedFor = null)
    {
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < count; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new { azureTag = "probe", password = "x" })
            };
            request.Headers.Add(FakeRemoteIpStartupFilter.HeaderName, clientIp);
            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            responses.Add(await client.SendAsync(request));
        }

        return responses;
    }

    [Fact]
    public async Task AuthEndpoint_Rejects_With429_AfterExceedingPerIpLimit()
    {
        var client = CreateClient(authPermitLimit: 2);

        var responses = await HammerAsync(client, "/bff/auth/login", "203.0.113.1", count: 6);

        var rejected = responses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        rejected.Should().NotBeNull("the auth policy must reject once the per-IP limit is exceeded");

        rejected!.Headers.Should().ContainKey("Retry-After");
        rejected.Headers.CacheControl!.NoStore.Should().BeTrue("RFC 6585 §4 — a 429 must not be cached");

        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("errorCode").GetString().Should().Be("RATE_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task AuthLimiter_PartitionsPerClientIp()
    {
        // The headline claim of ADR-0013: the limit is PER client IP, not global. Without
        // this, a limiter keyed on a constant would pass the rest of the suite.
        var client = CreateClient(authPermitLimit: 2);

        var noisy = await HammerAsync(client, "/bff/auth/login", "203.0.113.10", count: 5);
        noisy.Select(r => r.StatusCode).Should().Contain(HttpStatusCode.TooManyRequests,
            "the noisy IP must be limited once it exceeds its own budget");

        var innocent = await HammerAsync(client, "/bff/auth/login", "203.0.113.99", count: 2);
        innocent.Select(r => r.StatusCode).Should().NotContain(HttpStatusCode.TooManyRequests,
            "a different client IP is a different partition and must be unaffected by the burst");
    }

    [Fact]
    public async Task SpoofedXForwardedFor_DoesNotSplitThePartition()
    {
        // No trusted proxies are configured, so X-Forwarded-For must be ignored: one real
        // connection IP rotating fake XFF values still shares a single partition and is still
        // limited. (Honouring XFF from untrusted sources would hand the attacker a fresh
        // partition per spoofed value.)
        var client = CreateClient(authPermitLimit: 2);

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 6; i++)
        {
            responses.AddRange(
                await HammerAsync(client, "/bff/auth/login", "203.0.113.50", count: 1, forwardedFor: $"10.0.0.{i}"));
        }

        responses.Select(r => r.StatusCode).Should().Contain(HttpStatusCode.TooManyRequests,
            "a spoofed X-Forwarded-For must not create separate rate-limit partitions");
    }

    [Fact]
    public async Task YarpAuthRoute_CarriesTheTightPolicy_ButTheCatchAllDoesNot()
    {
        // /api/auth/* is the proxy path that bypasses BffAuthController and its attribute, so
        // it is the route an attacker would actually use. It is guarded only because ASP.NET
        // routing scores the literal /api/auth/login above /api/{**catch-all} — implicit
        // behaviour that a future "Order" on either route would silently break. This pins it.
        var client = CreateClient(authPermitLimit: 2);

        var login = await HammerAsync(client, "/api/auth/login", "203.0.113.20", count: 5);
        login.Select(r => r.StatusCode).Should().Contain(HttpStatusCode.TooManyRequests,
            "the YARP /api/auth/login route must carry the tight auth policy");

        // A sibling that matches only the unpolicied catch-all gets the generous global limit.
        var other = await HammerAsync(client, "/api/auth/pin/verify", "203.0.113.21", count: 5);
        other.Select(r => r.StatusCode).Should().NotContain(HttpStatusCode.TooManyRequests,
            "the catch-all route carries only the generous global baseline, not the auth policy");
    }

    [Fact]
    public async Task YarpUsersRoute_CarriesTheLookupPolicy_PerClient()
    {
        // Recipient lookup (/api/users/*) is limited by the tight per-user "lookup" policy;
        // with no session cookie it falls back to per-IP. A busy client is limited while a
        // different client is unaffected (ADR-0014).
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:LookupPermitLimit", "2");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
        }).CreateClient();

        var noisy = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/someexacttag");
            request.Headers.Add(FakeRemoteIpStartupFilter.HeaderName, "203.0.113.70");
            noisy.Add((await client.SendAsync(request)).StatusCode);
        }
        noisy.Should().Contain(HttpStatusCode.TooManyRequests,
            "the lookup policy must limit a busy client on /api/users/*");

        var innocent = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/othertag");
            request.Headers.Add(FakeRemoteIpStartupFilter.HeaderName, "203.0.113.71");
            innocent.Add((await client.SendAsync(request)).StatusCode);
        }
        innocent.Should().NotContain(HttpStatusCode.TooManyRequests,
            "a different client is a different lookup partition");
    }
}
