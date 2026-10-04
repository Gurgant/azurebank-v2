using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Utilities;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Every way a session ends goes through one path, which revokes its grant at the API
/// (ADR-0057 §4.6): idle expiry, the cap, a new sign-in over an old cookie and a graceful stop,
/// beside "Esci" and re-authentication (TokenRefreshTests, ReauthenticateTests). And the cap is the
/// grant's expiry when that comes first, in the store and in what the SPA is told (ADR-0057 §4.1,
/// F5).
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
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly TaskCompletionSource _firstRevoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _revokeCalls;
        private volatile bool _gone;
        public int RevokeCalls => Volatile.Read(ref _revokeCalls);
        public ConcurrentQueue<string> RevokedGrants { get; } = new();

        /// <summary>Each revoke call as it arrived: when, on this upstream's own clock, and its grants.</summary>
        public ConcurrentQueue<(TimeSpan At, string[] Grants)> RevokeCallLog { get; } = new();

        /// <summary>Completes when the first revoke call arrives.</summary>
        public Task FirstRevoke => _firstRevoke.Task;

        /// <summary>
        /// Set once the API has stopped: from then on every call fails as a connect to a closed port
        /// does, which is how a sidecar that stopped first answers the BFF.
        /// </summary>
        public bool Gone
        {
            get => _gone;
            set => _gone = value;
        }

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
            if (_gone)
            {
                throw new HttpRequestException("Connection refused: the API has stopped");
            }

            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/revoke":
                    Interlocked.Increment(ref _revokeCalls);
                    var grants = new List<string>();
                    using (var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()))
                    {
                        foreach (var grant in body.RootElement.GetProperty("refreshTokens").EnumerateArray())
                        {
                            grants.Add(grant.GetString()!);
                            RevokedGrants.Enqueue(grant.GetString()!);
                        }
                    }
                    RevokeCallLog.Enqueue((_clock.Elapsed, [.. grants]));
                    _firstRevoke.TrySetResult();
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

    /// <summary>
    /// A hosted service registered after GrantRevoker, so the host asks it to stop first. At its stop
    /// it notes whether a revoke had reached the API by then — waiting up to 10 s, so the answer is
    /// about order and not about a few milliseconds — and then does what the test gives it.
    /// </summary>
    /// <remarks>
    /// The test host is stopped twice, by the factory and by Program's own <c>app.Run()</c>
    /// (GrantRevoker.StopAsync says how that was measured). Every call returns the one stop, so
    /// neither of them gets past this service before it is done.
    /// </remarks>
    private sealed class StoppedBeforeTheRevoker(Action<Upstream, IServiceProvider> atStop) : IHostedService
    {
        private readonly Lock _gate = new();
        private Task? _stop;

        public Upstream? Upstream { get; set; }
        public IServiceProvider? Services { get; set; }

        /// <summary>Whether a revoke had reached the API when the host asked this service to stop.</summary>
        public bool RevokeHadArrived { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return _stop ??= StopOnceAsync();
            }
        }

        private async Task StopOnceAsync()
        {
            var first = Upstream!.FirstRevoke;
            RevokeHadArrived = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(10))) == first;
            atStop(Upstream, Services!);
        }
    }

    /// <summary>What <see cref="HeldRevokes"/> holds, shared by every handler the client factory builds.</summary>
    private sealed class RevokeHold
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Every grant a revoke call has brought in, held or not.</summary>
        public ConcurrentDictionary<string, byte> Arrived { get; } = new();

        public Task Released => _release.Task;

        public void Release() => _release.TrySetResult();
    }

    /// <summary>
    /// The "BackendApi" handler of an API that sits on every revoke until <see cref="RevokeHold.Release"/>.
    /// It waits asynchronously, holding no pool thread, and with its call's own cancellation token;
    /// then, and for every other call, <paramref name="upstream"/> answers.
    /// </summary>
    private sealed class HeldRevokes(Upstream upstream, RevokeHold hold) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/revoke")
            {
                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                {
                    foreach (var grant in body.RootElement.GetProperty("refreshTokens").EnumerateArray())
                    {
                        hold.Arrived.TryAdd(grant.GetString()!, 0);
                    }
                }
                await hold.Released.WaitAsync(cancellationToken);
            }
            return upstream.Respond(request);
        }
    }

    /// <summary>
    /// The "BackendApi" handler of an API that takes the first revoke call and never answers it, not
    /// even once the call is cancelled; every other call <paramref name="upstream"/> answers.
    /// <paramref name="revokes"/> is shared by every handler the client factory builds, so the first
    /// is the host's first.
    /// </summary>
    private sealed class FirstRevokeUnanswered(Upstream upstream, StrongBox<int> revokes) : HttpMessageHandler
    {
        private static readonly Task Never = new TaskCompletionSource().Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = upstream.Respond(request);
            if (request.RequestUri!.AbsolutePath == "/api/auth/revoke"
                && Interlocked.Increment(ref revokes.Value) == 1)
            {
                // Without the call's token on purpose: this is a handler that ignores cancellation.
                await Never;
            }
            return response;
        }
    }

    /// <summary>
    /// The "BackendApi" handler of an API that takes every revoke call and never answers it, letting
    /// go only when the call is cancelled, as a socket does; every other call
    /// <paramref name="upstream"/> answers.
    /// </summary>
    private sealed class RevokesUnanswered(Upstream upstream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = upstream.Respond(request);
            if (request.RequestUri!.AbsolutePath == "/api/auth/revoke")
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return response;
        }
    }

    private (WebApplicationFactory<Program> Host, Upstream Upstream) NewHost(
        ConcurrentQueue<LogEvent>? log = null, bool disposeWithTheClass = true,
        StoppedBeforeTheRevoker? stopper = null, RevokeHold? hold = null, bool firstRevokeUnanswered = false,
        bool revokesUnanswered = false)
    {
        var upstream = new Upstream();
        var revokes = new StrongBox<int>();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    firstRevokeUnanswered ? new FirstRevokeUnanswered(upstream, revokes)
                    : revokesUnanswered ? new RevokesUnanswered(upstream)
                    : hold is null ? new FakeBackendApiHandler(upstream.Respond)
                    : new HeldRevokes(upstream, hold));
                if (stopper is not null)
                {
                    stopper.Upstream = upstream;
                    services.AddHostedService(provider =>
                    {
                        stopper.Services = provider;
                        return stopper;
                    });
                }
            });
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
        NewSession(host.Services, grant, grantExpiresAt);

    private static string NewSession(
        IServiceProvider services, string grant, DateTime? grantExpiresAt = null) =>
        services.GetRequiredService<ISessionService>().CreateSession(
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
        // ADR-0057 §4.6: idle expiry ends the session the way "Esci" does, so its grant is revoked.
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
        // ADR-0057 §4.1 (F5): AbsoluteExpiresAt = min(SessionCreated + AbsoluteTimeoutMinutes,
        // grant expiry), one field for the store, /me and session-status. A 15-minute grant here,
        // under the 60-minute cap, so the two rules give different answers.
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
        // ADR-0057 §4.6 (F13). The browser's old session is replaced by the new sign-in's; before
        // PR-1 it lived on beside it, holding a live grant no cookie named any more.
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
        // ADR-0057 §4.6 (F7). The replica scales to zero minutes after its last request, long
        // before the idle sweep would have ended these sessions; the drain revokes their grants on
        // the way out.
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

            // ADR-0057 §10 O2h: no grant in any log line, rendered or as a property.
            log.Should().NotContain(e =>
                e.RenderMessage(CultureInfo.InvariantCulture).Contains("rt-held")
                || e.Properties.Values.Any(v => v.ToString().Contains("rt-held")));
        }
    }

    [Fact]
    public async Task AGracefulStop_WithNoGrantToRevoke_StillSaysSo_AndCallsNothing()
    {
        // F7's count is how an operator tells "the drain ran" from "the drain never ran"
        // (ADR-0057 §11). With nothing held it must still be written, as 0 and 0; the test above is
        // its control.
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

    [Fact]
    public async Task AGracefulStop_SendsTheHeldGrantsBeforeAnyServiceIsAskedToStop()
    {
        /*
          ADR-0057 F7, as the pre-review found it. The API is a sidecar that stops with the BFF: on
          Azure both containers get the stop signal at once. A drain sent only once the BFF's own
          services had stopped reached an API already gone, and revoked nothing. It starts now when
          the host BEGINS to stop, before any service is asked to. The stand-in below is stopped
          before GrantRevoker, and takes the API away when it is: what the drain has not sent by
          then is left.
        */
        var log = new ConcurrentQueue<LogEvent>();
        var stopper = new StoppedBeforeTheRevoker((upstream, _) => upstream.Gone = true);
        var (host, upstream) = NewHost(log, disposeWithTheClass: false, stopper);
        NewSession(host, "rt-held-1");
        NewSession(host, "rt-held-2");
        host.CreateClient();

        await host.DisposeAsync();

        var summary = Named(log, "Graceful stop:").ToList();
        using (new AssertionScope())
        {
            stopper.RevokeHadArrived.Should().BeTrue(
                "the drain starts when the host begins to stop, not after its services have stopped");
            upstream.RevokedGrants.Should().BeEquivalentTo(["rt-held-1", "rt-held-2"]);
            summary.Should().ContainSingle();
            summary.Single().Properties["Revoked"].ToString().Should().Be("2");
            summary.Single().Properties["Left"].ToString().Should().Be("0");
        }
    }

    [Fact]
    public async Task AGracefulStop_SendsTheGrantsWithNoRenewalInFlightAtOnce_AndTheOthersWithin5s()
    {
        /*
          ADR-0057 §4.6 and F2, at the stop. A revoke that overtook a renewal sent before it would
          make that renewal the tripwire at the API, so the grant of a session renewing when the
          host stops waits for its renewal: 5 s at most, out of the drain's one budget of 7.
          Nothing else waits for it. The renewal here is one the API sits on past the stop, so the
          5 s run out.
        */
        var log = new ConcurrentQueue<LogEvent>();
        var (host, upstream) = NewHost(log, disposeWithTheClass: false);
        NewSession(host, "rt-idle");
        var renewing = host.Services.GetRequiredService<ISessionService>().GetSession(NewSession(host, "rt-renewing"))!;
        var renewal = new TaskCompletionSource();
        lock (renewing.SyncRoot)
        {
            // Registered as TokenRefresher registers one: under the session's lock.
            renewing.InFlightRenewal = renewal.Task;
        }
        host.CreateClient();

        await host.DisposeAsync();

        var calls = upstream.RevokeCallLog.ToArray();
        var summary = Named(log, "Graceful stop:").ToList();
        calls.Select(c => c.Grants).Should().BeEquivalentTo(
            [new[] { "rt-idle" }, ["rt-renewing"]], o => o.WithStrictOrdering(),
            "the grant with no renewal in flight goes first, on its own");
        using (new AssertionScope())
        {
            (calls[1].At - calls[0].At).Should().BeGreaterThan(TimeSpan.FromSeconds(4),
                "the renewing session's grant waited for its renewal")
                .And.BeLessThan(TimeSpan.FromSeconds(8), "for 5 s at most");
            summary.Should().ContainSingle();
            summary.Single().Properties["Revoked"].ToString().Should().Be("2");
            summary.Single().Properties["Left"].ToString().Should().Be("0");
        }
    }

    [Fact]
    public async Task AGracefulStop_RevokesASessionStoredAfterItBegan_InASecondCall()
    {
        /*
          A sign-in still in flight when the host begins to stop can store its session after the
          first half of the drain has taken what was held. The second half, once the services have
          stopped, takes what is left, and only that: each grant is sent once.
        */
        var log = new ConcurrentQueue<LogEvent>();
        var stopper = new StoppedBeforeTheRevoker((_, services) => NewSession(services, "rt-late"));
        var (host, upstream) = NewHost(log, disposeWithTheClass: false, stopper);
        NewSession(host, "rt-held");
        host.CreateClient();

        await host.DisposeAsync();

        var summary = Named(log, "Graceful stop:").ToList();
        using (new AssertionScope())
        {
            stopper.RevokeHadArrived.Should().BeTrue("the first half went when the host began to stop");
            upstream.RevokeCallLog.Select(c => c.Grants).Should().BeEquivalentTo(
                [new[] { "rt-held" }, ["rt-late"]], o => o.WithStrictOrdering());
            summary.Should().ContainSingle();
            summary.Single().Properties["Revoked"].ToString().Should().Be("2");
            summary.Single().Properties["Left"].ToString().Should().Be("0");
        }
    }

    [Fact]
    public async Task AGracefulStop_GivesUpOnAFirstHalfTheApiNeverAnswers_AtTheDrainsBudget_AndSendsNothingPastIt()
    {
        /*
          The drain's budget cancels its calls, but a call ends only when the handler under it
          honours that. The API here takes the first half's call and never answers, not even once
          the call is cancelled. Until the stop bounded its wait, it waited for that half as long as
          the handler held it, past the host's shutdown grace. Now it waits one second past the
          drain's budget and says so. The second half, a session a sign-in stored after the first
          half had gone, gets only what the first left, which is nothing: it sends no call, says so,
          and its grant counts as left. The budget is 1 s here instead of 7, so the bound is 2 s.
        */
        var log = new ConcurrentQueue<LogEvent>();
        var stopper = new StoppedBeforeTheRevoker((_, services) => NewSession(services, "rt-late"));
        var (host, upstream) = NewHost(log, disposeWithTheClass: false, stopper, firstRevokeUnanswered: true);
        NewSession(host, "rt-held");
        var budget = TimeSpan.FromSeconds(1);
        host.Services.GetRequiredService<GrantRevoker>().DrainBudget = budget;
        host.CreateClient();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stop = host.DisposeAsync().AsTask();
        var stopped = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10))) == stop;
        clock.Stop();

        var overran = Named(log, "Graceful stop: the {Half} drain half overran").ToList();
        var spent = Named(log, "Graceful stop: the {Half} drain half found the drain's budget spent").ToList();
        var summary = Named(log, "Graceful stop:").Where(e => e.Properties.ContainsKey("Revoked")).ToList();
        using (new AssertionScope())
        {
            stopped.Should().BeTrue("the stop must not wait on a call the handler never ends");
            clock.Elapsed.Should().BeGreaterThan(budget, "the stop waited for the drain's budget first")
                .And.BeLessThan(budget + TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(3),
                    "it waits one second past the budget at most, and 3 s are room for a slow machine");
            overran.Should().ContainSingle();
            overran.Should().OnlyContain(e => e.Level == LogEventLevel.Warning
                && ((ScalarValue)e.Properties["Half"]).Value as string == "first");
            spent.Should().ContainSingle();
            spent.Should().OnlyContain(e => e.Level == LogEventLevel.Warning
                && ((ScalarValue)e.Properties["Half"]).Value as string == "second");
            upstream.RevokeCallLog.Select(c => c.Grants).Should().BeEquivalentTo(
                [new[] { "rt-held" }], "no call starts once the drain's budget is spent");
            summary.Should().ContainSingle();
            summary.Should().OnlyContain(e => e.Properties["Revoked"].ToString() == "0"
                && e.Properties["Left"].ToString() == "2", "the unanswered grant and the unsent one count as left");
        }
    }

    [Fact]
    public async Task AGracefulStop_WhoseCallsTheApiNeverAnswersInEitherHalf_EndsWithinTheDrainsOneBudget()
    {
        /*
          CodeRabbit on #217. Each half had a budget of its own, 15 s and 1 s more, and the second
          began only when the first had ended, so a stop whose API answered neither could take 32 s:
          past the 10 s Docker gives the BFF before it kills it. Both halves now draw from one budget,
          started when the host began to stop. The API here takes every revoke and never answers,
          letting go only when the call is cancelled, as a socket does. The first half's call ends at
          its own 5 s timeout; the second half, a session a sign-in stored meanwhile, is sent with the
          2 s the first left, and cut when they run out. The budget is the shipped one.
        */
        var log = new ConcurrentQueue<LogEvent>();
        var stopper = new StoppedBeforeTheRevoker((_, services) => NewSession(services, "rt-late"));
        var (host, upstream) = NewHost(log, disposeWithTheClass: false, stopper, revokesUnanswered: true);
        NewSession(host, "rt-held");
        var drain = host.Services.GetRequiredService<GrantRevoker>().DrainBudget + TimeSpan.FromSeconds(1);
        host.CreateClient();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stop = host.DisposeAsync().AsTask();
        // Past the two halves' 10 s that a budget per half would take, so that case is measured too.
        var stopped = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(30))) == stop;
        clock.Stop();

        var summary = Named(log, "Graceful stop:").Where(e => e.Properties.ContainsKey("Revoked")).ToList();
        using (new AssertionScope())
        {
            stopped.Should().BeTrue();
            drain.Should().Be(TimeSpan.FromSeconds(8), "the budget and its one second of overrun, inside Docker's 10 s");
            clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(5), "the first half's call waited out its 5 s")
                .And.BeLessThan(drain + TimeSpan.FromSeconds(1),
                    "the whole stop ends within the drain's one budget, and 1 s is room for a slow machine");
            upstream.RevokeCallLog.Select(c => c.Grants).Should().BeEquivalentTo(
                [new[] { "rt-held" }, ["rt-late"]], o => o.WithStrictOrdering(),
                "the second half is sent with what the first left");
            summary.Should().ContainSingle();
            summary.Should().OnlyContain(e => e.Properties["Revoked"].ToString() == "0"
                && e.Properties["Left"].ToString() == "2", "neither call was answered");
        }
    }

    [Fact]
    public async Task AGracefulStop_WhoseWorkerIsOnACallTheApiNeverAnswers_EndsWithinTheDrainsBudget_AndCountsItsGrantAsLeft()
    {
        /*
          CodeRabbit on #217. Before its second half the stop waits for the revoker's workers, and a
          worker's call is cancelled by the workers' own stop, not by the drain's budget. The API
          here takes the worker's call and never answers it, not even once the call is cancelled.
          While that wait was bounded by the host's token alone it lasted until the host's shutdown
          timeout, 30 s by default: past the drain's 8 s. Now it draws from the drain's budget and
          says when it ran out. The worker still owns its grant: the drain does not send it again,
          and counts it as left. The held session is revoked by the first half, as ever. The budget
          is the shipped one.
        */
        var log = new ConcurrentQueue<LogEvent>();
        var (host, upstream) = NewHost(log, disposeWithTheClass: false, firstRevokeUnanswered: true);
        var sessions = host.Services.GetRequiredService<ISessionService>();
        sessions.EndSession(NewSession(host, "rt-working"));
        (await Eventually(() => upstream.RevokeCalls == 1)).Should().BeTrue(
            "a worker must be on the call the API never answers, or it is not the workers' wait that is timed");
        NewSession(host, "rt-held");
        var drain = host.Services.GetRequiredService<GrantRevoker>().DrainBudget + TimeSpan.FromSeconds(1);
        host.CreateClient();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stop = host.DisposeAsync().AsTask();
        // Past the host's 30 s shutdown timeout, so a stop that waits for it is measured too.
        var stopped = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(40))) == stop;
        clock.Stop();

        var cut = Named(log, "Graceful stop: the revoke workers did not stop in time").ToList();
        var summary = Named(log, "Graceful stop:").Where(e => e.Properties.ContainsKey("Revoked")).ToList();
        using (new AssertionScope())
        {
            stopped.Should().BeTrue();
            clock.Elapsed.Should().BeLessThan(drain + TimeSpan.FromSeconds(1),
                "the wait for the workers ends with the drain's budget, and 1 s is room for a slow machine");
            cut.Should().ContainSingle();
            cut.Should().OnlyContain(e => e.Level == LogEventLevel.Warning && e.Properties["Count"].ToString() == "1");
            upstream.RevokeCallLog.Select(c => c.Grants).Should().BeEquivalentTo(
                [new[] { "rt-working" }, ["rt-held"]], o => o.WithStrictOrdering(),
                "the grant a worker is still sending is not sent again");
            summary.Should().ContainSingle();
            summary.Should().OnlyContain(e => e.Properties["Revoked"].ToString() == "1"
                && e.Properties["Left"].ToString() == "1", "the held grant was revoked, the worker's is unanswered");
        }
    }

    [Fact]
    public async Task WhenTheRevokeQueueIsFull_AnEndingIsDropped_SaysSo_AndDoesNotWait()
    {
        /*
          ADR-0057 F12. Every worker is on a revoke the API sits on, and the queue holds Capacity
          more, so the next ending finds no room. It must not wait for any — "Esci" answers at once
          — and must log GrantRevokeDropped, naming the session and never the grant. Once the API
          answers, every grant that found room is revoked, and the dropped one is not.
        */
        var log = new ConcurrentQueue<LogEvent>();
        var hold = new RevokeHold();
        var (host, upstream) = NewHost(log, hold: hold);
        var sessions = host.Services.GetRequiredService<ISessionService>();

        try
        {
            for (var i = 0; i < GrantRevoker.Parallelism; i++)
            {
                sessions.EndSession(NewSession(host, $"rt-working-{i}"));
            }
            (await Eventually(() => hold.Arrived.Count == GrantRevoker.Parallelism)).Should().BeTrue(
                "every worker must be on a revoke the API sits on, or it is not the queue that fills");

            for (var i = 0; i < GrantRevoker.Capacity; i++)
            {
                sessions.EndSession(NewSession(host, $"rt-queued-{i}"));
            }
            Named(log, "GrantRevokeDropped:").Should().BeEmpty("each of those found room in the queue");

            var last = NewSession(host, "rt-dropped");
            var ending = Task.Run(() => sessions.EndSession(last));
            (await Task.WhenAny(ending, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(ending,
                "an ending never waits for room in the queue");

            var dropped = Named(log, "GrantRevokeDropped:").ToList();
            dropped.Should().ContainSingle();
            ((ScalarValue)dropped.Single().Properties["SessionId"]).Value.Should().Be(SecretPrefix.Of(last));
            sessions.GetSession(last).Should().BeNull("the session ended all the same");
        }
        finally
        {
            hold.Release();
        }

        (await Eventually(() => upstream.RevokedGrants.Count == GrantRevoker.Parallelism + GrantRevoker.Capacity))
            .Should().BeTrue("every grant that found room is revoked once the API answers");
        using (new AssertionScope())
        {
            upstream.RevokedGrants.Should().NotContain("rt-dropped");
            string[] grants = ["rt-working-", "rt-queued-", "rt-dropped"];
            log.Should().NotContain(e => grants.Any(g =>
                    e.RenderMessage(CultureInfo.InvariantCulture).Contains(g)
                    || e.Properties.Values.Any(v => v.ToString().Contains(g))),
                "no grant in any log line (ADR-0057 §10 O2h)");
        }
    }

    [Theory]
    [InlineData("/bff/auth/login")]
    [InlineData("/bff/auth/register")]
    [InlineData("/bff/auth/reauthenticate")]
    public async Task EveryDoorThatCreatesASession_CapsItAtTheGrantsExpiry_AndEndsTheSessionItsCookieNamed(string door)
    {
        // ADR-0057 §4.1 (F5) at three of the four places a session is created from the API's
        // answer: each must pass the grant's expiry on. The API reports a 15-minute grant here, so a
        // door that dropped it would report sign-in + 60 and fail. Every other cap test builds its
        // session directly. And ADR-0057 §4.6 (F13, ADR-0026): the session the request's cookie
        // named ends through the revoke path, and only its grant is revoked. The fourth place is
        // the demo claim, which is 404 on this host, where the demo is off: DemoClaimTests holds
        // the same two things for it, on a host with the demo on.
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
        // ADR-0057 §4.6 (F12): only a 5xx, a timeout, a network error or a refused key is retried.
        // Any other refusal is final, and GrantRevokeAbandoned names the status. The first retry
        // would come after 1 s, so the 1.5 s wait below makes it due.
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
                "no grant in any log line (ADR-0057 §10 O2h)");
        }
    }

    [Fact]
    public async Task ARevokeThatKeepsFailing_IsRetriedUntilTheGrantExpires_ThenAbandoned()
    {
        // ADR-0057 §4.6 (F12): a 5xx is retried with backoff until the grant expires on its own,
        // then GrantRevokeAbandoned. A grant with 3 s left: the first call, a retry after 1 s, and
        // then the next wait (2 s) would pass its expiry, so it stops there.
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
