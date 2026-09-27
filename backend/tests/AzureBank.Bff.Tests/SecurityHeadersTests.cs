using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The whole security-header set, pinned value by value (ADR-0054). Nothing asserted any of it
/// before 2026-09-11, so the X-XSS-Protection value OWASP advises against, and a CSP comment saying
/// the SPA "would need a more permissive policy", had nothing standing in their way.
/// </summary>
/// <remarks>
/// Every expected value was read off the running BFF (http profile, 2026-09-11T14:52:57Z) with
/// <c>curl -D - /health/live</c>, and it is the same set on every response: a problem 401, a proxied
/// API refusal and, when <c>Spa:RootPath</c> is set, the page shell and its files
/// (<see cref="SpaHostingTests"/>).
/// </remarks>
public class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>The policy the built SPA was walked under with zero violations (see the middleware).</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; " +
        "style-src 'self' 'sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU='; " +
        "img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none';";

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

    [Fact]
    public void TheEmptyStringHash_IsTheHashOfTheEmptyString()
    {
        // The one opaque token in the policy, recomputed rather than trusted: if it named any other
        // content it would admit a <style> that says something, which is what it exists not to do.
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData([]));

        ContentSecurityPolicy.Should().Contain($"'sha256-{hash}'");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OutsideDevelopment_StrictTransportSecurity_IsSent_OverPlainHttpToo(string environment)
    {
        // Plain http to localhost: the two things app.UseHsts() would have skipped, and the way the
        // request reaches the container behind an edge that terminates TLS.
        var client = _factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment)).CreateClient();

        var response = await client.GetAsync("/health/live");

        client.BaseAddress!.Scheme.Should().Be("http");
        client.BaseAddress.Host.Should().Be("localhost");
        string.Join(", ", response.Headers.GetValues("Strict-Transport-Security")).Should().Be("max-age=31536000");
        AssertTheWholeSet(response);
    }

    [Fact]
    public async Task InDevelopment_NoStrictTransportSecurity()
    {
        // The factory's default environment. The development loop runs on http://localhost, and a
        // browser that took the header from an https localhost would hold every port there to https
        // for a year (RFC 6797 keys the policy on the host, not the port).
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
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
