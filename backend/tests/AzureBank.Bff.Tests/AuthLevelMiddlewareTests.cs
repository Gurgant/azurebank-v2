using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// First coverage of AuthLevelMiddleware — security-load-bearing now that
/// GET /api/accounts/{id}/full-number discloses the unmasked account number: this
/// middleware is the ONLY PIN (level-2) gate in the system (the API has no auth-level
/// concept; the JWT carries no level claim). YARP's forwarder is replaced with a
/// recording fake, so "the backend was never called" is asserted, never assumed.
/// </summary>
public class AuthLevelMiddlewareTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derivedFactories = [];

    public AuthLevelMiddlewareTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        // WithWebHostBuilder spins up a whole derived host per test — release them.
        foreach (var factory in _derivedFactories)
        {
            factory.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Captures every request YARP forwards instead of hitting a real backend.</summary>
    private sealed class RecordingForwarder : IForwarderHttpClientFactory
    {
        public List<string> ForwardedPaths { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new Handler(this));

        private sealed class Handler(RecordingForwarder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.ForwardedPaths.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":null,"message":"proxied"}""", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    private (WebApplicationFactory<Program> Factory, RecordingForwarder Backend) WithRecorder(
        Action<IWebHostBuilder>? configure = null)
    {
        var recorder = new RecordingForwarder();
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(recorder)));
        });
        _derivedFactories.Add(factory);
        return (factory, recorder);
    }

    private static (string SessionId, string CookieName, ISessionService Sessions) CreateSession(
        WebApplicationFactory<Program> factory)
    {
        var sessions = factory.Services.GetRequiredService<ISessionService>();
        var cookieName = factory.Services
            .GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(
            "fake-jwt",
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "stepupuser",
                Email = "stepup@example.com",
                FirstName = "Step",
                LastName = "Up",
                HasPin = true
            });
        return (sessionId, cookieName, sessions);
    }

    private static HttpRequestMessage Request(
        HttpMethod method, string path, string cookieName, string sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        return request;
    }

    private static async Task AssertStepUp403(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.GetValues("X-Auth-Level-Required").Should().ContainSingle()
            .Which.Should().Be("2");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("STEP_UP_REQUIRED");
        body.GetProperty("requiredLevel").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task FullNumber_AtLevel1_Is403StepUp_AndTheBackendIsNeverCalled()
    {
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, _) = CreateSession(factory);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number", cookieName, sessionId));

        await AssertStepUp403(response);
        backend.ForwardedPaths.Should().BeEmpty(
            "a level-1 session must be short-circuited BEFORE the proxy — the API would serve the number");
    }

    [Fact]
    public async Task FullNumber_WithTrailingSlash_IsGatedToo()
    {
        // Endpoint routing tolerates one trailing slash, so "/full-number/" reaches the
        // API endpoint — a raw suffix match would let it BYPASS the PIN gate entirely.
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, _) = CreateSession(factory);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number/", cookieName, sessionId));

        await AssertStepUp403(response);
        backend.ForwardedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task TransfersPost_WithTrailingSlash_IsGatedToo()
    {
        // Same normalization hole for the exact-match transfer paths: "/api/transfers/"
        // routes to the transfers endpoint but is not equal to "/api/transfers".
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, _) = CreateSession(factory);
        var client = factory.CreateClient();

        var request = Request(HttpMethod.Post, "/api/transfers/", cookieName, sessionId);
        request.Content = JsonContent.Create(new { });
        var response = await client.SendAsync(request);

        await AssertStepUp403(response);
        backend.ForwardedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task FullNumber_AfterPinVerification_IsProxiedThrough()
    {
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, sessions) = CreateSession(factory);
        sessions.SetPinVerified(sessionId);
        var client = factory.CreateClient();

        var path = $"/api/accounts/{Guid.NewGuid()}/full-number";
        var response = await client.SendAsync(Request(HttpMethod.Get, path, cookieName, sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "level 2 satisfies the gate");
        backend.ForwardedPaths.Should().ContainSingle(p => p == path);
    }

    [Fact]
    public async Task FullNumber_AfterPinExpiry_IsGatedAgain()
    {
        // PinValidityMinutes=0 makes the elevation expire immediately: GetAuthLevel must
        // downgrade the session back to level 1 and the gate must close again.
        var (factory, backend) = WithRecorder(builder =>
            builder.UseSetting("Security:PinValidityMinutes", "0"));
        var (sessionId, cookieName, sessions) = CreateSession(factory);
        sessions.SetPinVerified(sessionId);
        await Task.Delay(30); // any elapsed time > 0-minute validity

        var client = factory.CreateClient();
        var response = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number", cookieName, sessionId));

        await AssertStepUp403(response);
        backend.ForwardedPaths.Should().BeEmpty();
    }
}
