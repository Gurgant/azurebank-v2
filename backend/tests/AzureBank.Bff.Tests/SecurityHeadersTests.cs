using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The whole security-header set, pinned value by value. Nothing asserted any of it
/// before 2026-09-11, so the X-XSS-Protection value OWASP advises against had nothing standing in
/// its way.
/// </summary>
/// <remarks>
/// Every expected value was read off a running BFF (http profile) with <c>curl -D - /health/live</c>:
/// the five this change keeps from main 7ceb16f at 2026-09-11T14:35:08Z, X-XSS-Protection from this
/// branch at 14:52:57Z. It is the same set on every response: a problem 401 and a proxied API
/// refusal as well.
/// </remarks>
public class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>The policy every response carries.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none';";

    private readonly WebApplicationFactory<Program> _factory;

    public SecurityHeadersTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData("/health/live")]  // 200, plain text
    [InlineData("/bff/auth/me")]  // 401, the BFF's own problem body
    [InlineData("/api/accounts")] // 401, AuthLevelMiddleware refusing an anonymous /api call
    public async Task EveryResponse_CarriesTheWholeSet(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        AssertTheWholeSet(response);
    }

    internal static void AssertTheWholeSet(HttpResponseMessage response)
    {
        string Header(string name) => string.Join(", ", response.Headers.GetValues(name));

        Header("X-Content-Type-Options").Should().Be("nosniff");
        Header("X-Frame-Options").Should().Be("DENY");
        Header("X-XSS-Protection").Should().Be("0",
            "OWASP advises 0: the filter the old value turns on can create XSS in a safe page");
        Header("Referrer-Policy").Should().Be("strict-origin-when-cross-origin");
        Header("Permissions-Policy").Should().Be(
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), " +
            "microphone=(), payment=(), usb=()");
        Header("Content-Security-Policy").Should().Be(ContentSecurityPolicy);
    }
}
