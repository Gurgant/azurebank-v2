using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

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

    [Theory]
    [InlineData("/bff/auth/me")]  // 401, the BFF's own problem body
    [InlineData("/api/accounts")] // 401, AuthLevelMiddleware refusing an anonymous /api call
    public async Task AnswersUnderApiAndBff_AreNotStorable(string path)
    {
        // Nothing on these two answers sets a Cache-Control of its own, so the middleware's default
        // is what shows. The signed-in answers that carry the data are pinned next to their routes
        // (MeReadsThroughToTheApiTests).
        var response = await _factory.CreateClient().GetAsync(path);

        response.Headers.CacheControl.Should().NotBeNull("the middleware names a policy when none is set");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.ToString().Should().Be("no-cache", "no-store travels with its Pragma");
    }

    [Theory]
    [InlineData("/apix")]
    [InlineData("/bffx")]
    public async Task APathThatOnlyBeginsLikeAPrefix_IsGivenNoPolicy(string path)
    {
        // The default is for /api and /bff by segment, and for nothing else: a path that merely
        // starts with the same letters is answered 404 here (no Spa:RootPath), and nothing else on
        // that road sets a Cache-Control. The probes are no example: the framework names no-store on
        // them itself.
        var response = await _factory.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl.Should().BeNull();
        response.Headers.Pragma.Should().BeEmpty();
    }

    [Fact]
    public async Task AProxiedAnswer_ThatNamesNoPolicy_IsNotStorable()
    {
        // The signed-in answers that carry balances and transactions go through the proxy, which
        // copies the API's headers in: the API sends no Cache-Control on them.
        var response = await SignedInGet("/api/accounts", _ => { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull("the middleware names a policy when none is set");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task AProxiedAnswer_ThatNamesItsOwnPolicy_KeepsIt()
    {
        // The default fills a gap and overrides nothing: the callback runs after the proxy has
        // copied the API's headers in, so a policy the API chose stays, and Pragma is not added.
        var response = await SignedInGet(
            "/api/accounts",
            upstream => upstream.Headers.CacheControl =
                new CacheControlHeaderValue { Private = true, MaxAge = TimeSpan.FromSeconds(60) });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(60));
        response.Headers.CacheControl.NoStore.Should().BeFalse("the API's own policy is not replaced");
        response.Headers.Pragma.Should().BeEmpty();
    }

    /// <summary>Stands in for the API on the proxy's road and lets the test shape its answer.</summary>
    private sealed class StubUpstream(Action<HttpResponseMessage> shape) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new FakeBackendApiHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[],"message":null}""", Encoding.UTF8, "application/json"),
                };
                shape(response);
                return response;
            }));
    }

    /// <summary>A GET through the proxy with a live session's cookie, the upstream answering as shaped.</summary>
    private async Task<HttpResponseMessage> SignedInGet(string path, Action<HttpResponseMessage> shapeUpstream)
    {
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(
                    new StubUpstream(shapeUpstream)));

                // The BFF's own client, which a host that stops uses to revoke the grants it held.
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"message":"ok"}""", Encoding.UTF8, "application/json"),
                    }));
            }));

        var sessionId = host.Services.GetRequiredService<ISessionService>().CreateSession(
            "jwt-held-for-this-session",
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "cacheuser",
                Email = "cacheuser@example.com",
                FirstName = "Cache",
                LastName = "User",
                HasPin = true,
            });
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={sessionId}");
        return await client.SendAsync(request);
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
