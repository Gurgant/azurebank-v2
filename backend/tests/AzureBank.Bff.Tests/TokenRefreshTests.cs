using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Http;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Implementations;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Options;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Access-token renewal (ADR-0021, 06 §4.5). The API is faked at the "BackendApi" named
/// client (so /api/auth/refresh and /api/auth/revoke are scripted), YARP's forwarder is a
/// capturing fake (so the Authorization header the proxy WOULD carry is asserted, never
/// assumed), and sessions are constructed directly through the singleton ISessionService with a
/// controlled token expiry.
/// </summary>
/// <remarks>
/// Tokens named <c>jwt-*</c> are opaque, so the BFF falls back to the moment it received them for
/// their issue time and their lifetime is what was left then. The threshold tests build JWT-shaped
/// tokens with a chosen <c>iat</c> (<see cref="Jwt"/>), so the lifetime is the token's own.
/// </remarks>
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
        private int _logoutCalls;
        public int RefreshCalls => Volatile.Read(ref _refreshCalls);
        public int LogoutCalls => Volatile.Read(ref _logoutCalls);
        public string? LastPinAuth;

        /// <summary>Every grant named in a POST /api/auth/revoke, in arrival order.</summary>
        public ConcurrentQueue<string> RevokedGrants { get; } = new();

        /// <summary>How many POST /api/auth/revoke calls arrived.</summary>
        public int RevokeCalls => Volatile.Read(ref _revokeCalls);
        private int _revokeCalls;

        /// <summary>Response for POST /api/auth/refresh. Default: jwt-1 (+15 min), with a refresh token the BFF must ignore.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> OnRefresh { get; set; } =
            _ => JsonResponse(HttpStatusCode.OK, RefreshJson("jwt-1", "rt-1", DateTime.UtcNow.AddMinutes(15)));

        /// <summary>Response for POST /api/auth/revoke, after it is recorded. Default: 200.</summary>
        public Func<int, HttpResponseMessage> OnRevoke { get; set; } =
            _ => JsonResponse(HttpStatusCode.OK, """{"success":true,"message":"Revoked"}""");

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/refresh":
                    Interlocked.Increment(ref _refreshCalls);
                    return OnRefresh(request);
                case "/api/auth/revoke":
                    var call = Interlocked.Increment(ref _revokeCalls);
                    using (var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()))
                    {
                        foreach (var grant in body.RootElement.GetProperty("refreshTokens").EnumerateArray())
                        {
                            RevokedGrants.Enqueue(grant.GetString()!);
                        }
                    }
                    return OnRevoke(call);
                case "/api/auth/logout":
                    Interlocked.Increment(ref _logoutCalls);
                    return new HttpResponseMessage(HttpStatusCode.OK);
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

    /// <summary>
    /// Captures the path + Authorization header of every request YARP forwards. Answers 200 unless
    /// given <paramref name="respond"/>, which stands in for the API's own answer.
    /// </summary>
    private sealed class CapturingForwarder(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        : IForwarderHttpClientFactory
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
                return Task.FromResult(owner.Respond(request));
            }
        }

        private HttpResponseMessage Respond(HttpRequestMessage request) =>
            respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":null}""", Encoding.UTF8, "application/json")
            };
    }

    /// <summary>
    /// The "BackendApi" client's handler for a renewal that waits with its own call's cancellation
    /// token, unlike <see cref="FakeBackendApiHandler"/>, whose synchronous responder no token can
    /// interrupt. POST /api/auth/refresh waits for <paramref name="release"/>; then, and for every
    /// other call, <paramref name="api"/> answers.
    /// </summary>
    private sealed class HeldRefreshHandler(FakeApi api, Task release, TaskCompletionSource entered)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/refresh")
            {
                entered.TrySetResult();
                await release.WaitAsync(cancellationToken);
            }
            return api.Respond(request);
        }
    }

    /// <summary>
    /// The API's refusal of a request, as it writes it: a 401 problem+json whose errorCode names the
    /// reason (ServiceCredentialMiddleware for the key, the JWT challenge for a token).
    /// </summary>
    private static HttpResponseMessage ApiRefusal(string errorCode, string path) =>
        new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                $$"""
                {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,"instance":"{{path}}","errorCode":"{{errorCode}}","traceId":"0af7651916cd43dd8448eb211c80319c"}
                """,
                Encoding.UTF8,
                "application/problem+json")
        };

    // ── Harness ────────────────────────────────────────────────────────────────

    private (WebApplicationFactory<Program> Factory, CapturingForwarder Forwarder) Build(
        FakeApi api, CapturingForwarder? forwarder = null, Action<IServiceCollection>? moreServices = null)
    {
        forwarder ??= new CapturingForwarder();
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeBackendApiHandler(api.Respond));
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(forwarder));
                moreServices?.Invoke(services);
            }));
        _derived.Add(factory);
        return (factory, forwarder);
    }

    /// <summary>
    /// A session as a sign-in creates it: the access token, its grant, and the grant's expiry, which
    /// the API reports as sign-in plus 60 minutes unless <paramref name="grantExpiresAt"/> says otherwise.
    /// </summary>
    private static (string SessionId, string CookieName) NewSession(
        WebApplicationFactory<Program> factory, DateTime tokenExpiry, string? refreshToken,
        string accessToken = "jwt-0", DateTime? grantExpiresAt = null)
    {
        var sessions = factory.Services.GetRequiredService<ISessionService>();
        var cookieName = factory.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(
            accessToken,
            tokenExpiry,
            refreshToken,
            refreshToken is null ? null : grantExpiresAt ?? DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
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

    /// <summary>
    /// A JWT-shaped token whose payload carries <paramref name="issuedAt"/> and
    /// <paramref name="expiresAt"/>, so the BFF reads its own lifetime from it. Unsigned: the BFF
    /// reads a token, it never verifies one.
    /// </summary>
    private static string Jwt(string subject, DateTime issuedAt, DateTime expiresAt)
    {
        static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        var iat = new DateTimeOffset(issuedAt).ToUnixTimeSeconds();
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        return Part("""{"alg":"none","typ":"JWT"}""")
            + "." + Part($$"""{"sub":"{{subject}}","iat":{{iat}},"exp":{{exp}}}""")
            + ".test-signature";
    }

    /// <summary>
    /// A session holding a JWT of <paramref name="lifetimeSeconds"/> with
    /// <paramref name="leftSeconds"/> of it left, as if issued that long ago.
    /// </summary>
    private static (string SessionId, string CookieName, string Token) NewJwtSession(
        WebApplicationFactory<Program> factory, int lifetimeSeconds, int leftSeconds, DateTime? grantExpiresAt = null)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddSeconds(leftSeconds);
        var token = Jwt("held", now.AddSeconds(leftSeconds - lifetimeSeconds), expiresAt);
        var (sessionId, cookieName) = NewSession(factory, expiresAt, "rt-0", token, grantExpiresAt);
        return (sessionId, cookieName, token);
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

    /// <summary>Polls until <paramref name="condition"/> holds, or the timeout passes.</summary>
    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(25);
        }
        return true;
    }

    private static readonly DateTime Expired = DateTime.UtcNow.AddSeconds(-1);
    private static readonly DateTime Fresh = DateTime.UtcNow.AddHours(1);

    // ── T1 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ProxiedCall_WithExpiredToken_ReMintsAndForwardsTheNewBearer_AndKeepsTheStoredGrant()
    {
        var api = new FakeApi();
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");

        var response = await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.RefreshCalls.Should().Be(1, "an expired token triggers exactly one re-mint");
        forwarder.Forwarded.Should().ContainSingle()
            .Which.Auth.Should().Be("Bearer jwt-1", "the proxied call must carry the freshly re-minted token");
        Session(factory, sessionId).Value!.RefreshToken.Should().Be("rt-0",
            "the grant does not rotate (06 §4.3): whatever the answer carries, the stored grant stays");
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
    public async Task ARefreshTokenInvalid401_EndsTheSession()
    {
        // The one refusal that says the grant is dead (06 §4.5): unknown, revoked or expired.
        var api = new FakeApi
        {
            OnRefresh = request => ApiRefusal(ErrorCodes.RefreshTokenInvalid, request.RequestUri!.AbsolutePath)
        };
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");
        var client = factory.CreateClient();

        // The proxied call renews, gets the 401 → no Bearer injected, and the session is ended.
        await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

        Session(factory, sessionId).Value.Should().BeNull("a dead grant means the session cannot recover");
        forwarder.Forwarded.Should().ContainSingle().Which.Auth.Should().BeNull("no token to inject after a 401");
        var me = await client.SendAsync(Proxied(HttpMethod.Get, "/bff/auth/me", cookieName, sessionId));
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A401ThatDoesNotNameTheGrant_KeepsTheSession()
    {
        // Until PR-1 ANY 401 from the refresh call ended the session, a bare one included (this was
        // T3's claim). Only REFRESH_TOKEN_INVALID says the grant is dead now; a 401 with no errorCode
        // is not a verdict on it, and the user must not be signed out for it (06 §4.5).
        var api = new FakeApi { OnRefresh = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");

        var response = await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

        api.RefreshCalls.Should().Be(1, "the 401 must actually have been received, or keeping the session proves nothing");
        Session(factory, sessionId).Value.Should().NotBeNull("a 401 that does not name the grant keeps the session");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the held token has expired and no new one came, so the answer is the service's 503");
        forwarder.Forwarded.Should().BeEmpty("an expired token is never forwarded");
    }

    // ── T4 ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RefreshTransientFailure_DoesNotRevoke_AndForwardsTheHeldTokenWhileItHasMoreThan5sLeft()
    {
        // 5xx and a thrown HttpRequestException are both "transient": keep the session. The held
        // token has 30 s of a 15-minute life left, inside the foreground window (min(60 s, L/4)), so
        // the caller waits for the renewal; when it fails, the token still has more than 5 s and is
        // sent (06 §4.5). Until PR-1 this test said the CURRENT token was forwarded whatever was left
        // of it, an expired one included: T10 is the half of that which had to change.
        foreach (var onRefresh in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => throw new HttpRequestException("boom"),
        })
        {
            var api = new FakeApi { OnRefresh = onRefresh };
            var (factory, forwarder) = Build(api);
            var (sessionId, cookieName, held) = NewJwtSession(factory, lifetimeSeconds: 900, leftSeconds: 30);
            var client = factory.CreateClient();

            await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

            api.RefreshCalls.Should().Be(1, "the foreground window must have tried a renewal");
            Session(factory, sessionId).Value.Should().NotBeNull("a transient blip must NOT log the user out");
            forwarder.Forwarded.Should().ContainSingle()
                .Which.Auth.Should().Be($"Bearer {held}", "the held token still has more than 5 s left");

            // The 15 s cooldown: a second call inside it starts no renewal and sends the held token again.
            await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
            api.RefreshCalls.Should().Be(1, "no renewal starts within 15 s of a failed one");
            forwarder.Forwarded.Should().HaveCount(2).And.OnlyContain(f => f.Auth == $"Bearer {held}");
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

        // Ended through the same path as every other session, but with no grant there is nothing to revoke.
        await Task.Delay(200);
        api.RevokeCalls.Should().Be(0);
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
    public async Task Logout_EndsOnlyThisSession_RevokesItsGrantAlone_AndNeverCallsTheApiLogout()
    {
        // 06 §4.6. "Esci" answers at once, and the grant is revoked afterwards by GrantRevoker — one
        // grant, this session's. Until PR-1 it renewed the token and called the API's logout, which
        // revoked every grant of the user on every device (T7 asserted that call).
        var api = new FakeApi();
        var (factory, _) = Build(api);
        var (sessionA, cookieName) = NewSession(factory, Fresh, "rt-A");
        var (sessionB, _) = NewSession(factory, Fresh, "rt-B");

        var response = await factory.CreateClient().SendAsync(
            Proxied(HttpMethod.Post, "/bff/auth/logout", cookieName, sessionA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Session(factory, sessionA).Value.Should().BeNull("\"Esci\" ends the session at once");
        (await Eventually(() => api.RevokedGrants.Contains("rt-A"))).Should().BeTrue(
            "the ended session's grant is revoked at the API");
        await Task.Delay(200);
        using (new AssertionScope())
        {
            api.RevokedGrants.Should().Equal(["rt-A"], "only this session's grant, once");
            Session(factory, sessionB).Value.Should().NotBeNull("the user's other sessions are untouched");
            api.LogoutCalls.Should().Be(0, "the API's logout ends every session of the user; \"Esci\" ends one");
            api.RefreshCalls.Should().Be(0, "signing out renews nothing");
        }
    }

    [Fact]
    public async Task Logout_WhileTheApiFails_StillSignsOutAtOnce_AndTheRevokeIsRetriedUntilItLands()
    {
        // 06 §4.6 (F12): a 5xx is retried with backoff. The sign-out itself never waits for the API.
        var api = new FakeApi
        {
            OnRevoke = call => call < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : FakeApi.JsonResponse(HttpStatusCode.OK, """{"success":true}""")
        };
        var (factory, _) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Fresh, "rt-0");

        var logout = await factory.CreateClient().SendAsync(Proxied(HttpMethod.Post, "/bff/auth/logout", cookieName, sessionId));

        logout.StatusCode.Should().Be(HttpStatusCode.OK, "a failing API must never block the local logout");
        Session(factory, sessionId).Value.Should().BeNull();
        (await Eventually(() => api.RevokeCalls >= 3)).Should().BeTrue(
            "two 503s are retried, and the third call is answered 200");
        api.RevokedGrants.Should().OnlyContain(g => g == "rt-0");
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

    [Fact]
    public async Task VerifyPin_WhenTheRenewalFailsAndTheTokenHasExpired_Is503_AndKeepsTheSession()
    {
        // 06 §4.5 on the paths that bypass the transform: the same 503, never the 401 that signs out.
        var api = new FakeApi { OnRefresh = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var (factory, _) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");

        var request = Proxied(HttpMethod.Post, "/bff/auth/verify-pin", cookieName, sessionId);
        request.Content = JsonContent.Create(new { pin = "123456" });
        var response = await factory.CreateClient().SendAsync(request);

        api.RefreshCalls.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        api.LastPinAuth.Should().BeNull("nothing is sent to the API with an expired token");
        Session(factory, sessionId).Value.Should().NotBeNull();
    }

    // ── T10 ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExpiredToken_WithTheRenewalAnswered5xx_Is503_ForwardsNothing_AndKeepsTheSession()
    {
        // 06 §10 O0-2 item 4 (06 §4.5). When the renewal fails for a transient reason and the token
        // has expired, the BFF answers the browser 503 itself: an expired token is never forwarded.
        // Red on main, which forwards the expired token (TokenRefresher.RefreshAsync
        // returns the current token on a 5xx) and so hands the browser the API's 401.
        var api = new FakeApi { OnRefresh = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };

        // Stands in for the API behind the proxy, and answers what it answers: an expired bearer
        // is refused with AUTH_TOKEN_EXPIRED (zero clock skew, Api ServiceCollectionExtensions).
        var forwarder = new CapturingForwarder(request =>
            request.Headers.Authorization?.Parameter == "jwt-expired"
                ? ApiRefusal(ErrorCodes.TokenExpired, request.RequestUri!.AbsolutePath)
                : new HttpResponseMessage(HttpStatusCode.OK));
        var (factory, _) = Build(api, forwarder);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0", accessToken: "jwt-expired");

        var response = await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        var body = await response.Content.ReadAsStringAsync();

        api.RefreshCalls.Should().Be(1, "the renewal must have been tried, and failed, for this test to be about its failure");
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                $"a transient renewal failure with no usable token is the service failing, not the session (body: {body})");
            forwarder.Forwarded.Should().BeEmpty("an expired token is never forwarded");
            Session(factory, sessionId).Value.Should().NotBeNull("a 5xx on renewal keeps the session");
        }
    }

    [Fact]
    public async Task TheUnavailableAnswer_IsTheApisOwn503Shape_WithRetryAfter_AndTheCooldownHolds()
    {
        // What T10's 503 carries (06 §4.5 names Retry-After; T10 does not assert it), and that a
        // retry inside the 15 s cooldown gets the same answer without another renewal.
        var api = new FakeApi { OnRefresh = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0", accessToken: "jwt-expired");
        var client = factory.CreateClient();

        var first = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();

        using (new AssertionScope())
        {
            first.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            first.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            first.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero)
                .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(15), "the rest of the 15 s cooldown");
            body.GetProperty("status").GetInt32().Should().Be(503);
            body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
            body.GetProperty("retryAfterSeconds").GetInt32().Should().BeInRange(1, 15);
        }

        var second = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        second.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        api.RefreshCalls.Should().Be(1, "no renewal starts within 15 s of a failed one");
        forwarder.Forwarded.Should().BeEmpty();
    }

    // ── T11 ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RenewalRefusedForTheServiceKey_KeepsTheSession()
    {
        // 06 §10 O0-2 item 5 (06 §4.5). Only a 401 whose errorCode is REFRESH_TOKEN_INVALID says the
        // grant is dead. SERVICE_CREDENTIAL_REQUIRED says the API refused the BFF's KEY, as in a key
        // rotation applied on one side only; the grant is fine and the session must survive it.
        // Red on main, which ends the session on any 401 from the refresh call.
        var api = new FakeApi
        {
            OnRefresh = request => ApiRefusal(ErrorCodes.ServiceCredentialRequired, request.RequestUri!.AbsolutePath)
        };
        var (factory, _) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0");

        await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

        api.RefreshCalls.Should().Be(1, "the refusal must actually have been received, or keeping the session proves nothing");
        Session(factory, sessionId).Value.Should().NotBeNull(
            "a refused service key is not a dead grant: the session is kept");
    }

    [Fact]
    public async Task AProxiedCallRefusedForTheServiceKey_Is503_WithRetryAfter_AndNothingOfTheApisRefusal()
    {
        // 06 §4.7 (F4) on the proxy road, beyond the status: O0 item 6 (BffOverApiSessionTests) sees
        // the 503 through the real API, and nothing else. The response transform must also drop the
        // API's body and its refusal header. Without SuppressResponseBody YARP would copy the API's
        // body after the 503's own, and the status alone would still pass.
        var forwarder = new CapturingForwarder(request =>
        {
            // What ServiceCredentialMiddleware writes: its problem body, and the header naming it.
            var refusal = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    $$"""
                    {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,"detail":"This API is reached through the BFF.","instance":"{{request.RequestUri!.AbsolutePath}}","errorCode":"{{ErrorCodes.ServiceCredentialRequired}}","traceId":"0af7651916cd43dd8448eb211c80319c"}
                    """,
                    Encoding.UTF8,
                    "application/problem+json")
            };
            refusal.Headers.TryAddWithoutValidation(
                ServiceCredentialOptions.RefusalHeaderName, ServiceCredentialOptions.ServiceCredentialRefusal);
            return refusal;
        });
        var (factory, _) = Build(new FakeApi(), forwarder);
        var (sessionId, cookieName) = NewSession(factory, Fresh, "rt-0");

        var response = await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        var raw = await response.Content.ReadAsStringAsync();

        forwarder.Forwarded.Should().ContainSingle("the call reached the API, and the API refused the key");
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, $"body: {raw}");
            response.Headers.RetryAfter!.Delta.Should().Be(
                TimeSpan.FromSeconds(ServiceUnavailable.KeyRefusalRetryAfterSeconds));
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            response.Headers.Contains(ServiceCredentialOptions.RefusalHeaderName).Should().BeFalse(
                "the API's marker on its refusal goes no further than the BFF");
            raw.Should().NotContain("reached through the BFF", "the API's refusal text goes no further");
            raw.Should().NotContain(ErrorCodes.ServiceCredentialRequired);

            // ONE document: a body appended after the 503's own would not parse.
            var body = JsonSerializer.Deserialize<JsonElement>(raw);
            body.GetProperty("status").GetInt32().Should().Be(503);
            body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
            body.GetProperty("retryAfterSeconds").GetInt32().Should().Be(ServiceUnavailable.KeyRefusalRetryAfterSeconds);
            Session(factory, sessionId).Value.Should().NotBeNull("nothing about the key ends the user's session");
        }
    }

    // ── T12 ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task EsciInTheGapBetweenARequestsReadAndItsWriteBack_TheSessionStaysGone()
    {
        // 06 §10 O0-2 item 7 (F2, 06 §4.6). A request reads its session, "Esci" removes it, then the
        // request writes the session back. The store must not bring it back: the signed-out cookie
        // gets 401 AUTH_TOKEN_MISSING, and nothing renews the ended session's grant. Red on main,
        // whose UpdateSessionAsync re-adds a removed session (`_sessions[id] = session`), so the
        // next request with the same cookie gets 200.
        var api = new FakeApi();
        var forwarder = new CapturingForwarder();
        var (factory, _) = Build(api, forwarder, services =>
            services.Replace(ServiceDescriptor.Singleton<ITokenStoreService>(sp =>
                new PausingTokenStore(ActivatorUtilities.CreateInstance<InMemoryTokenStore>(sp)))));
        var store = (PausingTokenStore)factory.Services.GetRequiredService<ITokenStoreService>();

        // The token has expired, so a renewal is DUE on any live session: "no renewal" below can only
        // hold because nothing reached a live session, not because none was needed.
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0", accessToken: "jwt-expired");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        HttpResponseMessage after;
        int renewalsByTheEndOfEsci;
        HttpStatusCode heldStatus;
        try
        {
            // The held request: SessionActivityMiddleware reads the session, then parks in the store
            // before writing it back.
            store.PauseNextWriteBack();
            var held = client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
            (await Task.WhenAny(store.WriteBackPaused, Task.Delay(TimeSpan.FromSeconds(10))))
                .Should().BeSameAs(store.WriteBackPaused,
                    "the seam must hold a request between its read and its write-back, or the gap is not being tested");

            // "Esci" in the gap.
            var esci = await client.SendAsync(Proxied(HttpMethod.Post, "/bff/auth/logout", cookieName, sessionId));
            esci.StatusCode.Should().Be(HttpStatusCode.OK);
            renewalsByTheEndOfEsci = api.RefreshCalls;

            store.ReleaseWriteBack();
            heldStatus = (await held.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode;

            forwarder.Forwarded.Clear();
            after = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        }
        finally
        {
            store.ReleaseWriteBack();
        }

        var body = await after.Content.ReadAsStringAsync();
        using (new AssertionScope())
        {
            after.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"\"Esci\" ended the session, and a late write-back must not bring it back (body: {body}; the held request answered {(int)heldStatus})");
            body.Should().Contain(ErrorCodes.TokenMissing, "the answer the BFF gives a cookie with no session");
            forwarder.Forwarded.Should().BeEmpty("nothing is forwarded for a session that has ended");
            api.RefreshCalls.Should().Be(0,
                $"nothing renews an ended session's grant ({renewalsByTheEndOfEsci} of them by the time \"Esci\" answered)");
        }
    }

    // ── 06 §4.5: thresholds, background renewal, the foreground wait ───────────

    [Theory]
    // 15-minute token: 7.5 min and 60 s.
    [InlineData(900, 600, false, false)] // more than L/2 left: used, no renewal
    [InlineData(900, 300, false, true)]  // between 60 s and L/2: used, renewed in the background
    [InlineData(900, 45, true, true)]    // 60 s or less: the caller waits for the renewal
    // 2-minute token (the outage harness's): 60 s and 30 s.
    [InlineData(120, 45, false, true)]   // background — where a fixed 60 s threshold would have waited
    [InlineData(120, 20, true, true)]    // 30 s or less: foreground
    public async Task TheThresholdsComeFromTheTokensOwnLifetime(
        int lifetimeSeconds, int leftSeconds, bool forwardsTheRenewedToken, bool renews)
    {
        // 06 §4.5 (F6): L = exp - iat, read from the token. More than L/2 left: use it. Between
        // min(60 s, L/4) and L/2: use it and renew in the background. Less: wait for the renewal.
        var api = new FakeApi();
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName, held) = NewJwtSession(factory, lifetimeSeconds, leftSeconds);

        var response = await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        forwarder.Forwarded.Should().ContainSingle().Which.Auth.Should().Be(
            forwardsTheRenewedToken ? "Bearer jwt-1" : $"Bearer {held}");

        if (renews)
        {
            (await Eventually(() => Session(factory, sessionId).Value?.AccessToken == "jwt-1")).Should().BeTrue(
                "the renewal's token is stored on the session, in the background or not");
            api.RefreshCalls.Should().Be(1);
        }
        else
        {
            // The rows above that renew are this row's positive control: same harness, same wait.
            await Task.Delay(200);
            api.RefreshCalls.Should().Be(0, "more than half the token's life is left");
            Session(factory, sessionId).Value!.AccessToken.Should().Be(held);
        }
    }

    [Fact]
    public async Task ABackgroundRenewal_NeverHoldsTheRequest()
    {
        // The API sits on the renewal; the request goes on with the held token regardless (06 §4.5).
        using var hold = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi
        {
            OnRefresh = _ =>
            {
                entered.TrySetResult();
                hold.Wait();
                return FakeApi.JsonResponse(HttpStatusCode.OK,
                    FakeApi.RefreshJson("jwt-1", "rt-1", DateTime.UtcNow.AddMinutes(15)));
            }
        };
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName, held) = NewJwtSession(factory, lifetimeSeconds: 900, leftSeconds: 300);

        try
        {
            var response = await factory.CreateClient()
                .SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId))
                .WaitAsync(TimeSpan.FromSeconds(3));

            (await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(entered.Task,
                "the renewal must be in flight, or the request not waiting for it proves nothing");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            forwarder.Forwarded.Should().ContainSingle().Which.Auth.Should().Be($"Bearer {held}");
        }
        finally
        {
            hold.Set();
        }

        (await Eventually(() => Session(factory, sessionId).Value?.AccessToken == "jwt-1")).Should().BeTrue();
    }

    [Fact]
    public async Task ARenewalStillPendingAt5s_IsTransient_AndItsResultIsStoredWhenItComes()
    {
        // F15: the caller waits 5 s at most, then treats the renewal as failed — with an expired
        // token that is the 503. The renewal is detached and runs on: its answer is stored when it
        // arrives, and the next request uses it without renewing again.
        using var hold = new ManualResetEventSlim(false);
        var api = new FakeApi
        {
            OnRefresh = _ =>
            {
                hold.Wait();
                return FakeApi.JsonResponse(HttpStatusCode.OK,
                    FakeApi.RefreshJson("jwt-1", "rt-1", DateTime.UtcNow.AddMinutes(15)));
            }
        };
        var (factory, forwarder) = Build(api);
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0", accessToken: "jwt-expired");
        var client = factory.CreateClient();

        HttpResponseMessage pending;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            pending = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        }
        finally
        {
            watch.Stop();
            hold.Set();
        }

        using (new AssertionScope())
        {
            pending.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.5)).And.BeLessThan(TimeSpan.FromSeconds(10),
                "the caller waits for the renewal 5 s, no less and not much more");
            forwarder.Forwarded.Should().BeEmpty();
        }

        (await Eventually(() => Session(factory, sessionId).Value?.AccessToken == "jwt-1")).Should().BeTrue(
            "the abandoned wait did not abandon the renewal");
        var next = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        forwarder.Forwarded.Should().ContainSingle().Which.Auth.Should().Be("Bearer jwt-1");
        api.RefreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task ACallerThatHangsUpDuringARenewal_LeavesItRunning_AndItsResultIsStored()
    {
        // F15: a renewal never runs on the caller's cancellation. A browser that hangs up while it
        // waits for one abandons its own wait, not the renewal: the result is stored, and the next
        // request uses it without renewing again. The refresh here waits WITH its call's own
        // cancellation token (HeldRefreshHandler), so a renewal that ran on the caller's would be
        // cancelled by the hang-up and store nothing.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi();
        var (factory, forwarder) = Build(api, moreServices: services =>
            services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                () => new HeldRefreshHandler(api, release.Task, entered)));
        var (sessionId, cookieName) = NewSession(factory, Expired, "rt-0", accessToken: "jwt-expired");
        var client = factory.CreateClient();

        using var hangUp = new CancellationTokenSource();
        var call = client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId), hangUp.Token);
        try
        {
            (await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(entered.Task,
                "the caller must be waiting on a renewal in flight when it hangs up");
            hangUp.Cancel();
            await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();

            // Held past the hang-up, so the abort has reached the server's side of the request.
            await Task.Delay(200);
        }
        finally
        {
            release.TrySetResult();
        }

        (await Eventually(() => Session(factory, sessionId).Value?.AccessToken == "jwt-1")).Should().BeTrue(
            "the hang-up did not abandon the renewal");
        forwarder.Forwarded.Should().BeEmpty("the request that hung up was never forwarded");

        var next = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        forwarder.Forwarded.Should().ContainSingle().Which.Auth.Should().Be("Bearer jwt-1");
        api.RefreshCalls.Should().Be(1, "the stored result served the next request");
    }

    [Fact]
    public async Task ARenewalThatGainsNothing_StopsTheSessionRenewingForGood()
    {
        // The API clamps a token to its grant's expiry. Once a renewal brings back an expiry no later
        // than the one held, the session stops renewing (06 §4.5).
        var api = new FakeApi();
        var (factory, _) = Build(api);
        var (sessionId, cookieName, held) = NewJwtSession(factory, lifetimeSeconds: 900, leftSeconds: 300);
        var heldExpiry = Session(factory, sessionId).Value!.TokenExpiry;
        api.OnRefresh = _ => FakeApi.JsonResponse(HttpStatusCode.OK, FakeApi.RefreshJson("jwt-same", "rt-1", heldExpiry));
        var client = factory.CreateClient();

        await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        (await Eventually(() => Session(factory, sessionId).Value!.RenewalStopped)).Should().BeTrue(
            "a renewal ran, and gained nothing");

        await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        await Task.Delay(200);
        using (new AssertionScope())
        {
            api.RefreshCalls.Should().Be(1, "the second request is in the background window too, and starts nothing");
            Session(factory, sessionId).Value!.AccessToken.Should().Be(held, "an expiry that is not later is not stored");
        }
    }

    [Fact]
    public async Task NoRenewalStarts_WhenTheGrantDoesNotOutliveTheToken()
    {
        // 06 §4.5: GrantExpiresAt - TokenExpiry must be at least 1 s. TheThresholdsComeFromTheTokens-
        // OwnLifetime's (900, 300) row is the control: the same token with a later grant renews.
        var api = new FakeApi();
        var (factory, forwarder) = Build(api);
        var expiresAt = DateTime.UtcNow.AddSeconds(300);
        var (sessionId, cookieName, held) = NewJwtSession(factory, lifetimeSeconds: 900, leftSeconds: 300,
            grantExpiresAt: expiresAt.AddMilliseconds(500));

        await factory.CreateClient().SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
        await Task.Delay(200);

        api.RefreshCalls.Should().Be(0, "a renewal could gain less than a second");
        forwarder.Forwarded.Should().ContainSingle().Which.Auth.Should().Be($"Bearer {held}");
    }

    [Fact]
    public async Task EsciDuringARenewal_TheRevokeWaitsForIt_AndItsResultIsDropped()
    {
        // 06 §4.6 (F2): "Esci" captures the renewal in flight, and GrantRevoker revokes the grant only
        // after it settles — a revoke that overtook it would make it the tripwire at the API. The
        // result of a renewal for an ended session is never stored.
        using var hold = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi
        {
            OnRefresh = _ =>
            {
                entered.TrySetResult();
                hold.Wait();
                return FakeApi.JsonResponse(HttpStatusCode.OK,
                    FakeApi.RefreshJson("jwt-1", "rt-1", DateTime.UtcNow.AddMinutes(15)));
            }
        };
        var (factory, _) = Build(api);
        var (sessionId, cookieName, held) = NewJwtSession(factory, lifetimeSeconds: 900, leftSeconds: 300);
        var session = Session(factory, sessionId).Value!;
        var client = factory.CreateClient();

        try
        {
            await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieName, sessionId));
            (await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(entered.Task);

            var esci = await client.SendAsync(Proxied(HttpMethod.Post, "/bff/auth/logout", cookieName, sessionId));
            esci.StatusCode.Should().Be(HttpStatusCode.OK, "\"Esci\" does not wait for the renewal");

            // The Logout_EndsOnlyThisSession test is the control: with no renewal in flight the
            // revoke lands within milliseconds.
            await Task.Delay(1000);
            api.RevokeCalls.Should().Be(0, "the revoke waits for the renewal the session had in flight");
        }
        finally
        {
            hold.Set();
        }

        (await Eventually(() => api.RevokedGrants.Contains("rt-0"))).Should().BeTrue(
            "once the renewal settles, the grant is revoked");
        using (new AssertionScope())
        {
            Session(factory, sessionId).Value.Should().BeNull("the session stays ended");
            session.AccessToken.Should().Be(held, "a renewal result for an ended session is dropped");
            api.RefreshCalls.Should().Be(1);
        }
    }
}
