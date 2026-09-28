using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Every way a session ends goes through one path, which revokes its grant at the API (06 §4.6):
/// idle expiry, the cap, a new sign-in over an old cookie and a graceful stop, beside "Esci" and
/// re-authentication (TokenRefreshTests, ReauthenticateTests). And the cap is the grant's expiry when
/// that comes first, in the store and in what the SPA is told (06 §4.1, F5).
/// </summary>
/// <remarks>
/// Until PR-1 an expired session was only dropped from the BFF's memory: its refresh token stayed
/// live at the API for up to seven days with nobody holding it.
/// </remarks>
public class SessionEndingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public SessionEndingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var f in _derived) f.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Password = "Password1!";

    /// <summary>
    /// Records every grant revoked, and answers login and registration with a grant of its own,
    /// whose expiry it keeps.
    /// </summary>
    private sealed class Upstream
    {
        private int _revokeCalls;
        public int RevokeCalls => Volatile.Read(ref _revokeCalls);
        public ConcurrentQueue<string> RevokedGrants { get; } = new();

        /// <summary>
        /// The answer to POST /api/auth/revoke, after it is recorded. Default: 200.
        /// </summary>
        public Func<HttpResponseMessage> OnRevoke { get; set; } =
            () => Json(HttpStatusCode.OK, """{"success":true,"message":"Revoked"}""");

        /// <summary>
        /// How long the grants of this API's sign-ins live. 15 minutes, under the BFF's 60-minute cap,
        /// so a session capped at the grant and one capped at sign-in + 60 differ by 45 minutes.
        /// </summary>
        public TimeSpan GrantLifetime { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>The expiry this API reported for each grant it issued.</summary>
        public ConcurrentDictionary<string, DateTime> GrantExpiries { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/revoke":
                    Interlocked.Increment(ref _revokeCalls);
                    using (var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()))
                    {
                        foreach (var grant in body.RootElement.GetProperty("refreshTokens").EnumerateArray())
                        {
                            RevokedGrants.Enqueue(grant.GetString()!);
                        }
                    }
                    return OnRevoke();

                case "/api/auth/login":
                    var login = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    using (var parsed = JsonDocument.Parse(login))
                    {
                        if (parsed.RootElement.GetProperty("password").GetString() != Password)
                        {
                            return Json(HttpStatusCode.Unauthorized,
                                """{"status":401,"title":"Unauthorized","errorCode":"INVALID_CREDENTIALS"}""");
                        }
                    }
                    return Json(HttpStatusCode.OK, SignInJson("rt-login", account: null));

                case "/api/auth/register":
                    return Json(HttpStatusCode.Created, SignInJson("rt-register", account: """
                        {
                          "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
                          "accountNumber": "TEST-0000-0001",
                          "name": "Conto principale",
                          "type": "Checking",
                          "balance": 0,
                          "isPrimary": true,
                          "createdAt": "2026-09-28T00:00:00Z"
                        }
                        """));

                default:
                    return Json(HttpStatusCode.OK, """{"data":null,"message":"ok"}""");
            }
        }

        /// <summary>What login answers, and registration with <paramref name="account"/>.</summary>
        private string SignInJson(string grant, string? account)
        {
            var grantExpiresAt = DateTime.UtcNow + GrantLifetime;
            GrantExpiries[grant] = grantExpiresAt;
            var accountMember = account is null ? "" : $"\"account\": {account},";
            return $$"""
                {
                  "data": {
                    {{accountMember}}
                    "token": {
                      "accessToken": "jwt-login",
                      "refreshToken": "{{grant}}",
                      "refreshTokenExpiresAt": "{{grantExpiresAt:O}}",
                      "expiresIn": 899,
                      "tokenType": "Bearer",
                      "expiresAt": "{{DateTime.UtcNow.AddMinutes(15):O}}"
                    },
                    "user": {
                      "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
                      "azureTag": "ending",
                      "email": "ending@example.com",
                      "firstName": "Session",
                      "lastName": "Ending",
                      "hasPin": false
                    }
                  },
                  "message": "Signed in"
                }
                """;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class QueueSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }

    private (WebApplicationFactory<Program> Host, Upstream Upstream) NewHost(
        ConcurrentQueue<LogEvent>? log = null, bool disposeWithTheClass = true)
    {
        var upstream = new Upstream();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(upstream.Respond)));
            if (log is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(new QueueSink(log)));
            }
        });
        if (disposeWithTheClass)
        {
            _derived.Add(host);
        }
        return (host, upstream);
    }

    private static string NewSession(
        WebApplicationFactory<Program> host, string grant, DateTime? grantExpiresAt = null) =>
        host.Services.GetRequiredService<ISessionService>().CreateSession(
            "jwt-0",
            DateTime.UtcNow.AddMinutes(15),
            grant,
            grantExpiresAt ?? DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "ending",
                Email = "ending@example.com",
                FirstName = "Session",
                LastName = "Ending",
                HasPin = false
            });

    private static string CookieName(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

    private static HttpRequestMessage WithCookie(
        WebApplicationFactory<Program> host, HttpMethod method, string path, string sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{CookieName(host)}={sessionId}");
        return request;
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
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

    /// <summary>The session the response's cookie names.</summary>
    private static string SessionIdFrom(WebApplicationFactory<Program> host, HttpResponseMessage response)
    {
        var prefix = CookieName(host) + "=";
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..].Split(';')[0];
    }

    private static DateTime ReadUtc(JsonElement value) =>
        DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static IEnumerable<LogEvent> Named(ConcurrentQueue<LogEvent> log, string prefix) =>
        log.Where(e => e.MessageTemplate.Text.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public async Task AnIdleSession_IsEndedThroughTheRevokePath_OnARead_AndByTheSweep()
    {
        // 06 §4.6: idle expiry ends the session the way "Esci" does, so its grant is revoked.
        var (host, upstream) = NewHost();
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var inactivity = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.InactivityTimeoutMinutes;

        var onRead = NewSession(host, "rt-read");
        var bySweep = NewSession(host, "rt-sweep");
        sessions.GetSession(onRead)!.LastActivity = DateTime.UtcNow.AddMinutes(-inactivity - 1);
        sessions.GetSession(bySweep)!.LastActivity = DateTime.UtcNow.AddMinutes(-inactivity - 1);

        sessions.GetSession(onRead).Should().BeNull("the idle session is found expired on a read");
        await host.Services.GetRequiredService<ITokenStoreService>().CleanupExpiredSessionsAsync();

        (await Eventually(() => upstream.RevokedGrants.Contains("rt-read") && upstream.RevokedGrants.Contains("rt-sweep")))
            .Should().BeTrue("both removal paths revoke the grant");
        sessions.GetSession(bySweep).Should().BeNull();
    }

    [Fact]
    public async Task TheCap_IsTheGrantsExpiryWhenItComesFirst_InTheStoreAndInWhatTheSpaIsTold()
    {
        // 06 §4.1 (F5): AbsoluteExpiresAt = min(SessionCreated + AbsoluteTimeoutMinutes, grant expiry),
        // one field for the store, /me and session-status. A 15-minute grant here, under the 60-minute
        // cap, so the two rules give different answers.
        var (host, _) = NewHost();
        var grantExpiresAt = DateTime.UtcNow.AddMinutes(15);
        var sessionId = NewSession(host, "rt-cap", grantExpiresAt);
        var client = host.CreateClient();

        var me = await (await client.SendAsync(WithCookie(host, HttpMethod.Get, "/bff/auth/me", sessionId)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var status = await (await client.SendAsync(WithCookie(host, HttpMethod.Get, "/bff/auth/session-status", sessionId)))
            .Content.ReadFromJsonAsync<JsonElement>();

        using (new AssertionScope())
        {
            host.Services.GetRequiredService<ISessionService>().GetSession(sessionId)!.AbsoluteExpiresAt
                .Should().Be(grantExpiresAt);
            ReadUtc(me.GetProperty("data").GetProperty("session").GetProperty("expiresAt"))
                .Should().BeCloseTo(grantExpiresAt, TimeSpan.FromMilliseconds(1));
            ReadUtc(status.GetProperty("absoluteExpiresAt"))
                .Should().BeCloseTo(grantExpiresAt, TimeSpan.FromMilliseconds(1));
        }
    }

    [Fact]
    public async Task PastTheGrantsExpiry_TheSessionEnds_AndItsGrantIsRevoked()
    {
        // The other half of F5: a session whose grant has expired is over, whatever the configured cap
        // says — it could not renew again anyway.
        var (host, upstream) = NewHost();
        var sessionId = NewSession(host, "rt-past", DateTime.UtcNow.AddSeconds(-1));

        host.Services.GetRequiredService<ISessionService>().GetSession(sessionId).Should().BeNull();
        (await Eventually(() => upstream.RevokedGrants.Contains("rt-past"))).Should().BeTrue();
    }

    [Fact]
    public async Task ANewSignInCarryingAnOldCookie_EndsTheOldSession_AndAFailedOneLeavesIt()
    {
        // 06 §4.6 (F13). The browser's old session is replaced by the new sign-in's; before PR-1 it
        // lived on beside it, holding a live grant no cookie named any more.
        var (host, upstream) = NewHost();
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var old = NewSession(host, "rt-old");

        // A failed sign-in first: the old session must be exactly as it was.
        var failed = WithCookie(host, HttpMethod.Post, "/bff/auth/login", old);
        failed.Content = JsonContent.Create(new { email = "ending@example.com", password = "Wrong1234!" });
        (await client.SendAsync(failed)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        sessions.GetSession(old).Should().NotBeNull("a failed sign-in ends nothing");

        var signIn = WithCookie(host, HttpMethod.Post, "/bff/auth/login", old);
        signIn.Content = JsonContent.Create(new { email = "ending@example.com", password = Password });
        var response = await client.SendAsync(signIn);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sessions.GetSession(old).Should().BeNull("the session the old cookie named has ended");
        (await Eventually(() => upstream.RevokedGrants.Contains("rt-old"))).Should().BeTrue();
        await Task.Delay(200);
        upstream.RevokedGrants.Should().NotContain("rt-login", "the new session's grant is untouched");
    }

    [Fact]
    public async Task AGracefulStop_RevokesEveryHeldGrantInOneCall_AndSaysHowMany()
    {
        // 06 §4.6 (F7). The replica scales to zero minutes after its last request, long before the
        // idle sweep would have ended these sessions; the drain revokes their grants on the way out.
        var log = new ConcurrentQueue<LogEvent>();
        var (host, upstream) = NewHost(log, disposeWithTheClass: false);
        NewSession(host, "rt-held-1");
        NewSession(host, "rt-held-2");
        host.CreateClient(); // the host is started by now either way; this keeps it honest

        await host.DisposeAsync();

        var summary = log.Where(e => e.MessageTemplate.Text.StartsWith("Graceful stop:", StringComparison.Ordinal)).ToList();
        using (new AssertionScope())
        {
            upstream.RevokeCalls.Should().Be(1, "every held grant goes in one call");
            upstream.RevokedGrants.Should().BeEquivalentTo(["rt-held-1", "rt-held-2"]);
            summary.Should().ContainSingle();
            summary.Single().Properties["Revoked"].ToString().Should().Be("2");
            summary.Single().Properties["Left"].ToString().Should().Be("0");

            // 06 O2h: no grant in any log line, rendered or as a property.
            log.Should().NotContain(e =>
                e.RenderMessage(CultureInfo.InvariantCulture).Contains("rt-held")
                || e.Properties.Values.Any(v => v.ToString().Contains("rt-held")));
        }
    }

    [Fact]
    public async Task AGracefulStop_WithNoGrantToRevoke_StillSaysSo_AndCallsNothing()
    {
        // F7's count is how an operator tells "the drain ran" from "the drain never ran" (06 §11).
        // With nothing held it must still be written, as 0 and 0; the test above is its control.
        var log = new ConcurrentQueue<LogEvent>();
        var (host, upstream) = NewHost(log, disposeWithTheClass: false);
        host.CreateClient();

        await host.DisposeAsync();

        var summary = Named(log, "Graceful stop:").ToList();
        using (new AssertionScope())
        {
            upstream.RevokeCalls.Should().Be(0, "there was nothing to revoke");
            summary.Should().ContainSingle("the drain ran once, and said so");
            summary.Single().Properties["Revoked"].ToString().Should().Be("0");
            summary.Single().Properties["Left"].ToString().Should().Be("0");
        }
    }

    [Theory]
    [InlineData("/bff/auth/login")]
    [InlineData("/bff/auth/register")]
    [InlineData("/bff/auth/reauthenticate")]
    public async Task EveryDoorThatCreatesASession_CapsItAtTheGrantsExpiry_AndEndsTheSessionItsCookieNamed(string door)
    {
        // 06 §4.1 (F5) at the three places a session is created from the API's answer: each must pass
        // the grant's expiry on. The API reports a 15-minute grant here, so a door that dropped it
        // would report sign-in + 60 and fail. Every other cap test builds its session directly.
        // And 06 §4.6 (F13, ADR-0026): the session the request's cookie named ends through the revoke
        // path, and only its grant is revoked.
        var (host, upstream) = NewHost();
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var cap = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.AbsoluteTimeoutMinutes;
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var old = NewSession(host, "rt-old");

        var request = WithCookie(host, HttpMethod.Post, door, old);
        request.Content = door switch
        {
            "/bff/auth/register" => JsonContent.Create(new
            {
                azureTag = "ending_new",
                email = "ending@example.com",
                password = Password,
                firstName = "Session",
                lastName = "Ending",
            }),
            "/bff/auth/reauthenticate" => JsonContent.Create(new { password = Password }),
            _ => JsonContent.Create(new { email = "ending@example.com", password = Password }),
        };
        var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"the sign-in must work (body: {raw})");

        var created = SessionIdFrom(host, response);
        var newGrant = sessions.GetSession(created)!.RefreshToken!;
        var grantExpiresAt = upstream.GrantExpiries[newGrant];
        var me = await (await client.SendAsync(WithCookie(host, HttpMethod.Get, "/bff/auth/me", created)))
            .Content.ReadFromJsonAsync<JsonElement>();

        using (new AssertionScope())
        {
            grantExpiresAt.Should().BeBefore(sessions.GetSession(created)!.SessionCreated.AddMinutes(cap - 1),
                "the grant must expire before the configured cap, or the two rules agree and this proves nothing");
            ReadUtc(me.GetProperty("data").GetProperty("session").GetProperty("expiresAt"))
                .Should().BeCloseTo(grantExpiresAt, TimeSpan.FromMilliseconds(1),
                    "the session ends when the grant the API reported does");
            sessions.GetSession(old).Should().BeNull("the session the old cookie named has ended");
        }

        (await Eventually(() => upstream.RevokedGrants.Contains("rt-old"))).Should().BeTrue(
            "the old session's grant is revoked");
        await Task.Delay(200);
        upstream.RevokedGrants.Should().NotContain(newGrant, "the new session's grant is untouched");
    }

    [Fact]
    public async Task ARevokeTheApiRefusesForGood_IsAbandonedAtOnce_AndSaysSo()
    {
        // 06 §4.6 (F12): only a 5xx, a timeout, a network error or a refused key is retried. Any other
        // refusal is final, and GrantRevokeAbandoned names the status. The first retry would come after
        // 1 s, so the 1.5 s wait below makes it due.
        var log = new ConcurrentQueue<LogEvent>();
        var (host, upstream) = NewHost(log);
        upstream.OnRevoke = () => new HttpResponseMessage(HttpStatusCode.BadRequest);
        var sessions = host.Services.GetRequiredService<ISessionService>();

        sessions.EndSession(NewSession(host, "rt-refused"));

        (await Eventually(() => Named(log, "GrantRevokeAbandoned:").Any())).Should().BeTrue();
        await Task.Delay(1500);
        var abandoned = Named(log, "GrantRevokeAbandoned:").ToList();
        using (new AssertionScope())
        {
            upstream.RevokeCalls.Should().Be(1, "a final refusal is not retried");
            abandoned.Should().ContainSingle();
            abandoned.Single().Properties["StatusCode"].ToString().Should().Be("400");
            log.Should().NotContain(e => e.RenderMessage(CultureInfo.InvariantCulture).Contains("rt-refused"),
                "no grant in any log line (06 O2h)");
        }
    }

    [Fact]
    public async Task ARevokeThatKeepsFailing_IsRetriedUntilTheGrantExpires_ThenAbandoned()
    {
        // 06 §4.6 (F12): a 5xx is retried with backoff until the grant expires on its own, then
        // GrantRevokeAbandoned. A grant with 3 s left: the first call, a retry after 1 s, and then
        // the next wait (2 s) would pass its expiry, so it stops there.
        var log = new ConcurrentQueue<LogEvent>();
        var (host, upstream) = NewHost(log);
        upstream.OnRevoke = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var sessions = host.Services.GetRequiredService<ISessionService>();

        sessions.EndSession(NewSession(host, "rt-failing", DateTime.UtcNow.AddSeconds(3)));

        (await Eventually(() => Named(log, "GrantRevokeAbandoned:").Any())).Should().BeTrue(
            "a grant that has expired is not retried any more");
        var abandoned = Named(log, "GrantRevokeAbandoned:").ToList();
        using (new AssertionScope())
        {
            upstream.RevokeCalls.Should().BeGreaterThanOrEqualTo(2, "a 5xx is retried");
            abandoned.Should().ContainSingle();
            abandoned.Single().Properties["Attempts"].ToString().Should().Be(upstream.RevokeCalls.ToString(CultureInfo.InvariantCulture));
        }
    }
}
