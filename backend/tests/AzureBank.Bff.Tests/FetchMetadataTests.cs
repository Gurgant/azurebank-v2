using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Fetch-Metadata resource isolation (ADR-0018): a state-changing request that a browser
/// stamps as cross-site must be rejected at the edge, while safe methods, same-origin
/// traffic, user-initiated navigations, and header-less non-browser clients pass through.
/// The backend API is not running: a request that PASSES the policy fails later for its
/// own reason (400 model validation / 503 connection refused), which is precisely how
/// these tests tell "blocked by policy" (403) apart from "allowed through" (anything else).
/// </summary>
public class FetchMetadataTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FetchMetadataTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static HttpRequestMessage LoginPost(string? secFetchSite)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/login")
        {
            Content = JsonContent.Create(new { email = "probe@example.com", password = "x" })
        };
        if (secFetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", secFetchSite);
        }
        return request;
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task NonGet_WithForeignSecFetchSite_IsRejectedWith403(string site)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(LoginPost(site));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be("CROSS_SITE_REQUEST_BLOCKED");
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    [InlineData(null)]
    public async Task NonGet_WithAllowedOrAbsentSecFetchSite_PassesThePolicy(string? site)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(LoginPost(site));

        // Passing the policy means reaching the controller, which 503s without a backend.
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_WithCrossSiteSecFetchSite_IsAllowed()
    {
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/auth/session-status");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CorsIsGone_NoAccessControlHeaders_EvenForTheFormerAllowedOrigin()
    {
        // The former "AllowFrontend" policy credential-whitelisted this exact origin in
        // production. After BE-1 the BFF registers no CORS at all: a cross-origin
        // request gets no Access-Control-* response headers, so the browser blocks it.
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/auth/session-status");
        request.Headers.Add("Origin", "http://localhost:5173");
        var response = await client.SendAsync(request);

        response.Headers.Should().NotContainKey("Access-Control-Allow-Origin");
        response.Headers.Should().NotContainKey("Access-Control-Allow-Credentials");
    }
}
