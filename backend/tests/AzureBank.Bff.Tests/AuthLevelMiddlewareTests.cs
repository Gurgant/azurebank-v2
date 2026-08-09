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

        /// <summary>
        /// The Authorization header AS THE BACKEND WOULD SEE IT, per forwarded request (null when
        /// none was sent). Recording the path alone cannot express the invariant that matters most
        /// here — that a token the CLIENT supplied never reaches the API — because the request is
        /// forwarded either way and only the header distinguishes the two cases.
        /// </summary>
        public List<string?> ForwardedAuthorization { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new Handler(this));

        private sealed class Handler(RecordingForwarder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.ForwardedPaths.Add(request.RequestUri!.AbsolutePath);
                owner.ForwardedAuthorization.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":null,"message":"proxied"}""", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    /// <summary>A request carrying a client-supplied bearer and NO session cookie.</summary>
    private static HttpRequestMessage BearerOnly(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", $"Bearer {StolenToken}");
        return request;
    }

    /// <summary>
    /// Stands in for a JWT the caller obtained for themselves. It does not need to be a valid
    /// token: the point is that the BFF must not hand it to the backend, which is decided before
    /// anything validates it.
    /// </summary>
    private const string StolenToken = "client-supplied.jwt.value";

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

    /// <summary>
    /// The BFF's refusal must be INDISTINGUISHABLE from the API's own missing-token 401.
    ///
    /// <para>
    /// Two reasons, and the second is why it is asserted rather than left to taste. The SPA already
    /// understands this envelope, so nothing downstream learns a new shape. And a caller probing for
    /// which paths carry the step-up gate learns nothing from the response: a gated path with no
    /// session and an ungated one with no session answer identically.
    /// </para>
    /// <para>
    /// The literals come from the running API, observed on /api/accounts with no Authorization
    /// header, not from a doc:
    /// <c>{"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
    /// "detail":"Authentication is required to access this resource.","instance":"/api/accounts",
    /// "errorCode":"AUTH_TOKEN_MISSING","traceId":"…"}</c>
    /// </para>
    /// </summary>
    private static async Task AssertLooksLikeTheApisOwn401(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("https://httpstatuses.com/401");
        body.GetProperty("status").GetInt32().Should().Be(401);
        body.GetProperty("title").GetString().Should().Be("Unauthorized");
        body.GetProperty("detail").GetString()
            .Should().Be("Authentication is required to access this resource.");
        body.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_MISSING");
        body.TryGetProperty("traceId", out _).Should().BeTrue();
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

    /*
      THE BYPASS, and why these four exist.

      Measured on the running stack (API :7215, BFF :5000, seeded dev database) before any of this
      was written — not reasoned about:

        POST /api/auth/login  (a PROXIED route)                      -> 200, raw JWT in the body
        GET  /api/accounts/{id}/full-number + Bearer, NO cookie      -> 200 "AB-0000-0000-01"
        GET  same with NO Authorization header                       -> 401   (the header IS honoured)
        POST /api/transfers + Bearer, NO cookie, bogus recipient     -> 404 from /api/transfers,
                                                                        i.e. the gate was passed

      Two defects compounded. The gate only ran INSIDE `if (Cookies.TryGetValue(...))`, and its else
      branch fell through on the belief that the API would answer 401 — which is false the moment the
      caller brings their own token. And the transform only ever SET the Authorization header inside
      that same cookie branch, never clearing an inbound one, while YARP's default header copy had
      already placed the client's on the outbound request.

      The class docstring above is the reason this is severe rather than untidy: this middleware is
      the only PIN gate in the system, so bypassing it leaves NO second factor on a transfer or a
      reveal for anyone holding the password.
    */

    [Fact]
    public async Task FullNumber_WithAClientBearerAndNoSession_IsNotProxied()
    {
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var response = await client.SendAsync(
            BearerOnly(HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "no session means no caller — and the BFF must decide that itself rather than delegate "
            + "it to an API that would happily authenticate the token the client brought");
        backend.ForwardedPaths.Should().BeEmpty(
            "this is the request that returned the unmasked account number on the live stack");
        await AssertLooksLikeTheApisOwn401(response);
    }

    [Fact]
    public async Task TransfersPost_WithAClientBearerAndNoSession_IsNotProxied()
    {
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var request = BearerOnly(HttpMethod.Post, "/api/transfers");
        request.Content = JsonContent.Create(new { });
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        backend.ForwardedPaths.Should().BeEmpty(
            "on the live stack this reached the endpoint — a real recipient would have been paid "
            + "with no PIN ever entered");
        await AssertLooksLikeTheApisOwn401(response);
    }

    [Fact]
    public async Task AClientSuppliedAuthorizationHeaderIsReplacedByTheSessionsToken()
    {
        // The gate is satisfied honestly here, so nothing short-circuits and the request really is
        // forwarded. What must not survive the hop is the CLIENT's token: the BFF injects the one it
        // holds server-side, and an inbound header must never win or linger.
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, sessions) = CreateSession(factory);
        sessions.SetPinVerified(sessionId);
        var client = factory.CreateClient();

        var path = $"/api/accounts/{Guid.NewGuid()}/full-number";
        var request = Request(HttpMethod.Get, path, cookieName, sessionId);
        request.Headers.Add("Authorization", $"Bearer {StolenToken}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        backend.ForwardedPaths.Should().ContainSingle(p => p == path);
        backend.ForwardedAuthorization.Should().ContainSingle()
            .Which.Should().Be("Bearer fake-jwt",
                "the session's stored token, never the one the caller sent");
    }

    [Fact]
    public async Task AClientBearerIsStrippedOnUngatedRoutesToo()
    {
        // /api/accounts is not PIN-gated, so the middleware never looks at it — which is exactly why
        // the transform has to be the one that strips. Without this, every un-gated endpoint stays
        // reachable with a self-obtained token and the server-side session model is decorative.
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        await client.SendAsync(BearerOnly(HttpMethod.Get, "/api/accounts"));

        backend.ForwardedPaths.Should().ContainSingle(p => p == "/api/accounts");
        backend.ForwardedAuthorization.Should().ContainSingle()
            .Which.Should().BeNull("with no session there is no token to inject, and the client's "
                + "must not stand in for one — the API then answers 401 for real");
    }
}
