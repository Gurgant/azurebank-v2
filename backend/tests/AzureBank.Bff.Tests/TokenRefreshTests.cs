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
/// Silent access-token re-mint (ADR-0021, PR-2). The API is faked at the "BackendApi" named
/// client (so /api/auth/refresh + /api/auth/logout are scripted), YARP's forwarder is a
/// capturing fake (so the Authorization header the proxy WOULD carry is asserted, never
/// assumed), and sessions are constructed directly through the singleton ISessionService with a
/// controlled token expiry.
/// </summary>
public class TokenRefreshTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public TokenRefreshTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var f in _derived) f.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    /// <summary>Scriptable, call-counting stand-in for the upstream API on the "BackendApi" client.</summary>
    private sealed class FakeApi
    {
        private int _refreshCalls;
        public int RefreshCalls => _refreshCalls;
        public int LogoutCalls;
        public string? LastLogoutAuth;
        public string? LastPinAuth;

        /// <summary>Response for POST /api/auth/refresh. Default: rotate to jwt-1 / rt-1 (+15 min).</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> OnRefresh { get; set; } =
            _ => JsonResponse(HttpStatusCode.OK, RefreshJson("jwt-1", "rt-1", DateTime.UtcNow.AddMinutes(15)));

        /// <summary>Response for POST /api/auth/logout. Default: 200.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> OnLogout { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/refresh":
                    Interlocked.Increment(ref _refreshCalls);
                    return OnRefresh(request);
                case "/api/auth/logout":
                    Interlocked.Increment(ref LogoutCalls);
                    LastLogoutAuth = request.Headers.Authorization?.ToString();
                    return OnLogout(request);
                case "/api/auth/pin/verify":
                    LastPinAuth = request.Headers.Authorization?.ToString();
                    return JsonResponse(HttpStatusCode.OK, """{"data":{"verified":true},"message":"ok"}""");
                default:
                    return JsonResponse(HttpStatusCode.OK, """{"data":null,"message":"ok"}""");
            }
        }

        public static string RefreshJson(string access, string refresh, DateTime expiresAt) =>
            $$"""
            {"success":true,"data":{"accessToken":"{{access}}","refreshToken":"{{refresh}}","expiresAt":"{{expiresAt:O}}"},"message":"Token refreshed"}
            """;

        public static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>Captures the path + Authorization header of every request YARP forwards.</summary>
    private sealed class CapturingForwarder : IForwarderHttpClientFactory
    {
        private readonly object _lock = new();
        public List<(string Path, string? Auth)> Forwarded { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler(this));

        private sealed class Handler(CapturingForwarder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner._lock)
                {
                    owner.Forwarded.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":null}""", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private (WebApplicationFactory<Program> Factory, CapturingForwarder Forwarder) Build(FakeApi api)
    {
        var forwarder = new CapturingForwarder();
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeBackendApiHandler(api.Respond));
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(forwarder));
            }));
        _derived.Add(factory);
        return (factory, forwarder);
    }

    private static (string SessionId, string CookieName) NewSession(
        WebApplicationFactory<Program> factory, DateTime tokenExpiry, string? refreshToken,
        string accessToken = "jwt-0")
    {
        var sessions = factory.Services.GetRequiredService<ISessionService>();
        var cookieName = factory.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(accessToken, tokenExpiry, refreshToken, new UserLoginInfo
        {
            Id = Guid.NewGuid(),
            AzureTag = "remint",
            Email = "remint@example.com",
            FirstName = "Re",
            LastName = "Mint",
            HasPin = true
        });
        return (sessionId, cookieName);
    }

    private static HttpRequestMessage Proxied(HttpMethod method, string path, string cookieName, string sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        return request;
    }

    private static UserSessionRef Session(WebApplicationFactory<Program> factory, string sessionId) =>
        new(factory.Services.GetRequiredService<ISessionService>().GetSession(sessionId));

    private readonly record struct UserSessionRef(AzureBank.Bff.Models.UserSession? Value);

    private static readonly DateTime Expired = DateTime.UtcNow.AddSeconds(-1);
    private static readonly DateTime Fresh = DateTime.UtcNow.AddHours(1);

    // ── T1 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ProxiedCall_WithExpiredToken_ReMintsAndForwardsTheNewBearer_AndRotatesTheStoredRefresh()
    {
        var api = new FakeApi();
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");

        var response = await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.RefreshCalls.Should().Be(1, "an expired token within the skew window triggers exactly one re-mint");
        forwarder.Forwarded.Should().ContainSingle()
            .Which.Auth.Should().Be("Bearer jwt-1", "the proxied call must carry the freshly re-minted token");
        Session(factory, sessionId).Value!.RefreshToken.Should().Be("rt-1",
            "rotation returns a new refresh token, which must replace the stored one");
    }

    // ── T2 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ConcurrentProxiedCalls_ReMintExactlyOnce_AndAllCarryTheSameNewBearer()
    {
        // Hold the winning refresh open until the other callers have piled up behind the
        // single-flight gate. Without this the winner can finish rotating before the others even
        // look — so the RefreshCalls==1 assertion would pass even for a gate-less implementation.
        // With the hold, a broken impl lets ALL callers reach the API and RefreshCalls climbs past 1.
        using var hold = new ManualResetEventSlim(false);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi
        {
            OnRefresh = _ =>
            {
                firstEntered.TrySetResult();
                hold.Wait();
                return FakeApi.JsonResponse(HttpStatusCode.OK,
                    FakeApi.RefreshJson("jwt-1", "rt-1", DateTime.UtcNow.AddMinutes(15)));
            }
        };
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");
        var client = factory.CreateClient();

        const int n = 8;
        var responses = Enumerable.Range(0, n).Select(_ =>
            client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId))).ToArray();

        // The winner is now parked inside the refresh; give the other 7 ample time to reach the
        // single-flight gate (where they must wait) rather than the API.
        await firstEntered.Task;
        await Task.Delay(250);
        api.RefreshCalls.Should().Be(1, "single-flight: only ONE caller may reach the API refresh while it is in flight");
        responses.Should().OnlyContain(t => !t.IsCompleted, "every caller is parked until the single refresh completes");

        hold.Set();
        var completed = await Task.WhenAll(responses);

        completed.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        api.RefreshCalls.Should().Be(1, "single-flight: concurrent callers share ONE refresh, not one each");
        forwarder.Forwarded.Should().HaveCount(n)
            .And.OnlyContain(f => f.Auth == "Bearer jwt-1");
    }

    // ── T3 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Refresh401_RevokesTheSession()
    {
        var api = new FakeApi { OnRefresh = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");
        var client = factory.CreateClient();

        // The proxied call re-mints, gets a 401 → no Bearer injected, and the session is revoked.
        await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

        Session(factory, sessionId).Value.Should().BeNull("a dead refresh token means the session cannot recover");
        forwarder.Forwarded.Should().ContainSingle().Which.Auth.Should().BeNull("no token to inject after a 401");
        var me = await client.SendAsync(Proxied(HttpMethod.Get, "/bff/auth/me", cookieName, sessionId));
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T4 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RefreshTransientFailure_DoesNotRevoke_AndForwardsTheCurrentToken()
    {
        // 5xx and a thrown HttpRequestException are both "transient": keep the session.
        foreach (var onRefresh in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => throw new HttpRequestException("boom"),
        })
        {
            var api = new FakeApi { OnRefresh = onRefresh };
            var (factory, forwarder) = Build(api);
            var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0", accessToken: "jwt-stale");

            await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

            Session(factory, sessionId).Value.Should().NotBeNull("a transient blip must NOT log the user out");
            forwarder.Forwarded.Should().ContainSingle()
                .Which.Auth.Should().Be("Bearer jwt-stale", "the current token is forwarded on a transient failure");
        }
    }

    // ── T5 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TokenlessSession_WithExpiredToken_IsInvalid_OldBehaviorPreserved()
    {
        var api = new FakeApi();
        var (factory, _) = Build(api);
        var (sessionId, _) = NewSession(factory, Expired, refreshToken: null);

        Session(factory, sessionId).Value.Should().BeNull(
            "a session that never got a refresh token still dies when its access token expires");
        api.RefreshCalls.Should().Be(0, "there is nothing to refresh with");
    }

    // ── T6 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void SessionWithRefreshToken_OutlivesTheAccessTokenExpiry()
    {
        var (factory, _) = Build(new FakeApi());
        var (sessionId, _) = NewSession(factory, Expired, "rt-0");

        Session(factory, sessionId).Value.Should().NotBeNull(
            "with a refresh token the 15-minute JWT no longer kills the session (it slides within the budgets)");
    }

    // ── T7 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Logout_PropagatesToTheApiWithABearer_AndRevokesLocallyEvenWhenTheApiFails()
    {
        // Success sub-case: the API logout is called with a Bearer, and the local session is gone.
        var api = new FakeApi();
        var (factory, _) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Fresh, "rt-0");

        var response = await factory.CreateClient().SendAsync(
            Proxied(HttpMethod.Post, "/bff/auth/logout", cookieName, sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.LogoutCalls.Should().Be(1, "BFF logout must revoke the API-side refresh tokens too");
        api.LastLogoutAuth.Should().StartWith("Bearer ");
        Session(factory, sessionId).Value.Should().BeNull();

        // Failure sub-case: the API logout fails, but local logout STILL succeeds.
        var failingApi = new FakeApi
        {
            OnLogout = _ => throw new HttpRequestException("api down"),
        };
        var (factory2, _) = Build(failingApi);
        var (sid2, cn2) = NewSession(factory2, Fresh, "rt-0");

        var logout = await factory2.CreateClient().SendAsync(Proxied(HttpMethod.Post, "/bff/auth/logout", cn2, sid2));
        logout.StatusCode.Should().Be(HttpStatusCode.OK, "a failed API logout must never block the local logout");
        failingApi.LogoutCalls.Should().Be(1, "the upstream logout is still ATTEMPTED (best-effort) before it throws");
        failingApi.LastLogoutAuth.Should().StartWith("Bearer ", "the attempted logout still carried the session's bearer");
        Session(factory2, sid2).Value.Should().BeNull();
    }

    // ── T8 ─────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/refresh/")] // trailing slash still routes upstream
    public async Task RawProxiedRefresh_Is404_AndNeverReachesTheBackend(string path)
    {
        var api = new FakeApi();
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Fresh, "rt-0");
        var client = factory.CreateClient();

        // With a valid cookie...
        var withCookie = await client.SendAsync(Proxied(HttpMethod.Post, path, cookieName, sessionId));
        withCookie.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // ...and without one.
        var withoutCookie = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path));
        withoutCookie.StatusCode.Should().Be(HttpStatusCode.NotFound);

        forwarder.Forwarded.Should().BeEmpty("the browser must never drive rotation — it never reaches YARP");
        api.RefreshCalls.Should().Be(0);
    }

    // ── T9 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task VerifyPin_WithExpiredToken_ReMintsAndCallsTheApiWithTheFreshBearer()
    {
        var api = new FakeApi();
        var (factory, _) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");

        var request = Proxied(HttpMethod.Post, "/bff/auth/verify-pin", cookieName, sessionId);
        request.Content = JsonContent.Create(new { pin = "123456" });
        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.RefreshCalls.Should().Be(1, "the pin path bypasses the transform, so it must re-mint too");
        api.LastPinAuth.Should().Be("Bearer jwt-1", "the pin/verify call must carry the re-minted token");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("verified").GetBoolean().Should().BeTrue();
    }
}
