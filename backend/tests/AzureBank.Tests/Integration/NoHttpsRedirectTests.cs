using System.Net;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The API answers plain http and redirects nothing to https (since 2026-09-25): its one client is
/// the BFF, which follows no redirect (ADR-0055).
/// </summary>
/// <remarks>
/// The https port set here is what makes a redirect due. Without it UseHttpsRedirection finds no
/// port and passes every request through, so these tests would stay green with it back in the
/// pipeline. With it, and the middleware in place, the BFF saw 307s on the two containers: its
/// readiness said Degraded and a sign-in answered 502 (measured 2026-09-25).
/// </remarks>
public class NoHttpsRedirectTests : IntegrationTestBase
{
    public NoHttpsRedirectTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Theory]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/api/accounts", HttpStatusCode.Unauthorized)] // the BFF's credential, no token
    public async Task AnHttpRequest_IsAnswered_NotRedirected(string path, HttpStatusCode expected)
    {
        using var withHttpsPort = Factory.WithWebHostBuilder(builder => builder.UseSetting("https_port", "443"));
        using var client = withHttpsPort.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        client.BaseAddress!.Scheme.Should().Be("http");
        response.StatusCode.Should().Be(expected, "a redirect is a failed call for the BFF, which follows none");
    }
}
