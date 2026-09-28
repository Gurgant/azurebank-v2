using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;
using Serilog.Events;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The session stamp at the BFF (ADR-0057 §5.3): <see cref="SessionStampWatcher"/> reads the stamps
/// of the users who hold sessions every 15 s and never when nobody does; a failed read keeps the
/// last values; the store refuses a session whose stamp is below the latest known and ends it the
/// way every session ends; and each sign-in hands the API's stamp to the session it creates.
/// </summary>
/// <remarks>
/// The watcher's clock is a <see cref="FakeTimeProvider"/>, so a period is one <c>Advance</c> and no
/// test waits 15 s. What a tick did is read from its effects — the calls the fake API recorded, the
/// map, a log line — never from a hook in the watcher. The same claims end to end, against the real
/// API, are <c>AzureBank.Tests.Integration.SessionStampLeverTests</c>.
/// </remarks>
public class SessionStampWatcherTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public SessionStampWatcherTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var f in _derived) f.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Password = "Password1!";

    /// <summary>The user every sign-in of the fake API answers for.</summary>
    private static readonly Guid SignedInUser = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    /// <summary>
    /// The API as far as these tests need it: session stamps from a table, sign-ins that answer a
    /// stamp, and a revoke that records the grants it was sent.
    /// </summary>
    private sealed class Upstream
    {
        public ConcurrentQueue<Guid[]> StampReads { get; } = new();
        public ConcurrentQueue<string> RevokedGrants { get; } = new();
        public ConcurrentDictionary<Guid, int> Stamps { get; } = new();

        /// <summary>The stamp a sign-in answers.</summary>
        public int SignInStamp { get; set; }

        /// <summary>Replaces the answer to a stamp read, after it is recorded; null answers from <see cref="Stamps"/>.</summary>
        public Func<HttpResponseMessage>? FailStampReads { get; set; }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/session-stamps":
                    using (var parsed = JsonDocument.Parse(body!))
                    {
                        var ids = parsed.RootElement.GetProperty("userIds").EnumerateArray()
                            .Select(id => id.GetGuid()).ToArray();
                        StampReads.Enqueue(ids);
                        if (FailStampReads is { } fail)
                        {
                            return fail();
                        }

                        var known = ids.Where(Stamps.ContainsKey)
                            .Select(id => new { userId = id, sessionStamp = Stamps[id] });
                        return Json(HttpStatusCode.OK,
                            JsonSerializer.Serialize(new { success = true, data = new { stamps = known } }));
                    }

                case "/api/auth/revoke":
                    using (var parsed = JsonDocument.Parse(body!))
                    {
                        foreach (var grant in parsed.RootElement.GetProperty("refreshTokens").EnumerateArray())
                        {
                            RevokedGrants.Enqueue(grant.GetString()!);
                        }
                    }
                    return Json(HttpStatusCode.OK, """{"success":true,"message":"Revoked"}""");

                case "/api/auth/login":
                    using (var parsed = JsonDocument.Parse(body!))
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

        private string SignInJson(string grant, string? account)
        {
            var accountMember = account is null ? "" : $"\"account\": {account},";
            return $$"""
                {
                  "data": {
                    {{accountMember}}
                    "token": {
                      "accessToken": "jwt-login",
                      "refreshToken": "{{grant}}",
                      "refreshTokenExpiresAt": "{{DateTime.UtcNow.AddMinutes(60):O}}",
                      "sessionStamp": {{SignInStamp}},
                      "expiresIn": 899,
                      "tokenType": "Bearer",
                      "expiresAt": "{{DateTime.UtcNow.AddMinutes(15):O}}"
                    },
                    "user": {
                      "id": "{{SignedInUser}}",
                      "azureTag": "stamped",
                      "email": "stamped@example.com",
                      "firstName": "Session",
                      "lastName": "Stamp",
                      "hasPin": false
                    }
                  },
                  "message": "Signed in"
                }
                """;
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class QueueSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }

    private (WebApplicationFactory<Program> Host, Upstream Upstream, FakeTimeProvider Clock, ConcurrentQueue<LogEvent> Log)
        NewHost()
    {
        var upstream = new Upstream();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var log = new ConcurrentQueue<LogEvent>();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            // The watcher's repeated failures are Debug; the host's minimum is Information.
            builder.UseSetting(
                "Serilog:MinimumLevel:Override:AzureBank.Bff.Services.SessionStampWatcher", "Debug");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(upstream.Respond));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            });
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(new QueueSink(log)));
        });
        _derived.Add(host);

        // Building the host builds the watcher, and with it the watcher's timer on this clock, so an
        // Advance made from here on is seen. ExecuteAsync itself may start later, on the thread pool:
        // a tick that falls due before it waits is kept for that wait.
        _ = host.Services;
        return (host, upstream, clock, log);
    }

    private static string NewSession(WebApplicationFactory<Program> host, Guid userId, int stamp, string grant) =>
        host.Services.GetRequiredService<ISessionService>().CreateSession(
            "jwt-0",
            DateTime.UtcNow.AddMinutes(15),
            grant,
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = userId,
                AzureTag = "stamped",
                Email = "stamped@example.com",
                FirstName = "Session",
                LastName = "Stamp",
                HasPin = false
            },
            stamp);

    private static HttpRequestMessage WithCookie(
        WebApplicationFactory<Program> host, HttpMethod method, string path, string sessionId)
    {
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
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

    private static List<LogEvent> PollFailures(ConcurrentQueue<LogEvent> log) =>
        log.Where(e => e.MessageTemplate.Text.StartsWith("SessionStampPollFailed", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task WithNoSession_ATickCallsNothing_AndWithOne_ItReadsThatUsersStamp()
    {
        var (host, upstream, clock, _) = NewHost();
        var stamps = host.Services.GetRequiredService<SessionStamps>();

        /*
          A no-session tick must be SEEN to run, or "no call" proves nothing. Its one visible effect
          is that the map forgets users who hold no session, so a user with no session is planted in
          it: once that entry is gone, the tick has run, and it had nobody to ask about.
        */
        var nobody = Guid.NewGuid();
        stamps.Observe(nobody, 3);
        clock.Advance(SessionStampWatcher.Period);
        (await Eventually(() => stamps.Latest(nobody) is null)).Should().BeTrue("the no-session tick must run");
        upstream.StampReads.Should().BeEmpty("no session, no call: an idle replica never wakes the API");

        // The control: the same tick with a session asks about that session's user, once.
        var user = Guid.NewGuid();
        NewSession(host, user, stamp: 0, "rt-a");
        clock.Advance(SessionStampWatcher.Period);
        (await Eventually(() => !upstream.StampReads.IsEmpty)).Should().BeTrue("a tick with a session reads its stamp");

        await Task.Delay(200);
        upstream.StampReads.Should().ContainSingle().Which.Should().Equal(user);
    }

    [Fact]
    public async Task ATickBeforeThePeriod_CallsNothing()
    {
        // 15 s, not less: a session exists, and one tick short of the period nothing is read.
        var (host, upstream, clock, _) = NewHost();
        NewSession(host, Guid.NewGuid(), stamp: 0, "rt-a");

        clock.Advance(SessionStampWatcher.Period - TimeSpan.FromMilliseconds(1));
        await Task.Delay(300);
        upstream.StampReads.Should().BeEmpty();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await Eventually(() => !upstream.StampReads.IsEmpty)).Should().BeTrue("the full period reads the stamps");
    }

    [Fact]
    public async Task AStampAboveTheSessions_RefusesItAtItsNextRequest_EndsItThroughTheRevokePath_AndSparesTheRest()
    {
        var (host, upstream, clock, _) = NewHost();
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var signedOut = Guid.NewGuid();
        var other = Guid.NewGuid();
        var older = NewSession(host, signedOut, stamp: 0, "rt-older");
        var others = NewSession(host, other, stamp: 0, "rt-other");

        // Positive control: both answer before the watcher knows anything.
        foreach (var id in new[] { older, others })
        {
            (await client.SendAsync(WithCookie(host, HttpMethod.Get, "/bff/auth/me", id))).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        upstream.Stamps[signedOut] = 1;
        upstream.Stamps[other] = 0;
        clock.Advance(SessionStampWatcher.Period);
        var stamps = host.Services.GetRequiredService<SessionStamps>();
        (await Eventually(() => stamps.Latest(signedOut) == 1)).Should().BeTrue("the tick reads the raised stamp");

        // A sign-in after the sign-out: given the raised stamp, as the API answers it.
        var current = NewSession(host, signedOut, stamp: 1, "rt-current");

        using (new AssertionScope())
        {
            (await client.SendAsync(WithCookie(host, HttpMethod.Get, "/bff/auth/me", older))).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, "its stamp is below the latest known");
            (await client.SendAsync(WithCookie(host, HttpMethod.Get, "/bff/auth/me", current))).StatusCode
                .Should().Be(HttpStatusCode.OK, "a session given the raised stamp is not older than it");
            (await client.SendAsync(WithCookie(host, HttpMethod.Get, "/bff/auth/me", others))).StatusCode
                .Should().Be(HttpStatusCode.OK, "another user's stamp did not move");
        }

        // Ended by the one path (ADR-0057 §4.6): gone from the store, and its grant, only its,
        // revoked.
        host.Services.GetRequiredService<ISessionService>().GetSession(older).Should().BeNull();
        (await Eventually(() => upstream.RevokedGrants.Contains("rt-older"))).Should().BeTrue();
        await Task.Delay(200);
        upstream.RevokedGrants.Should().Equal("rt-older");
    }

    public static TheoryData<string> Failures() => ["500", "network", "no stamps", "not json"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedPoll_KeepsTheLastStamps_WarnsOnce_AndSaysWhenItWorksAgain(string failure)
    {
        var (host, upstream, clock, log) = NewHost();
        var stamps = host.Services.GetRequiredService<SessionStamps>();
        var user = Guid.NewGuid();

        // A session the map, once it knows 1, refuses — kept unread, since a read would end it.
        var older = NewSession(host, user, stamp: 0, "rt-older");
        upstream.Stamps[user] = 1;
        clock.Advance(SessionStampWatcher.Period);
        (await Eventually(() => stamps.Latest(user) == 1)).Should().BeTrue("the first poll reads 1");

        upstream.FailStampReads = failure switch
        {
            "500" => () => Upstream.Json(HttpStatusCode.InternalServerError, """{"status":500}"""),
            "network" => () => throw new HttpRequestException("the API is not listening"),
            "no stamps" => () => Upstream.Json(HttpStatusCode.OK, """{"data":null,"message":"ok"}"""),
            _ => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") },
        };

        // Two failed polls: the watcher saw both, and the map kept 1 through them.
        clock.Advance(SessionStampWatcher.Period);
        (await Eventually(() => PollFailures(log).Count == 1)).Should().BeTrue("the failed poll is logged");
        clock.Advance(SessionStampWatcher.Period);
        (await Eventually(() => PollFailures(log).Count == 2)).Should().BeTrue("so is the second");

        using (new AssertionScope())
        {
            upstream.StampReads.Should().HaveCount(3, "one good poll, then two failed ones");
            stamps.Latest(user).Should().Be(1, "a failed poll keeps the last values");
            host.Services.GetRequiredService<ISessionService>().GetSession(older)
                .Should().BeNull("the kept value still refuses the older session");
            PollFailures(log).Select(e => e.Level).Should().Equal(
                [LogEventLevel.Warning, LogEventLevel.Debug], "an outage costs one Warning, not one per tick");
        }

        // It works again: said once, at Information.
        upstream.FailStampReads = null;
        NewSession(host, user, stamp: 1, "rt-later");
        clock.Advance(SessionStampWatcher.Period);
        (await Eventually(() => log.Any(e => e.MessageTemplate.Text.StartsWith(
            "SessionStampPoll: reading session stamps works again", StringComparison.Ordinal)))).Should().BeTrue();
    }

    [Theory]
    [InlineData("/bff/auth/login")]
    [InlineData("/bff/auth/register")]
    [InlineData("/bff/auth/reauthenticate")]
    public async Task EachSignIn_KeepsTheStampTheApiAnswered_OnTheSessionItCreates(string path)
    {
        var (host, upstream, _, _) = NewHost();
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        upstream.SignInStamp = 7;

        // Re-authentication needs a session to replace; the others take the old cookie too, and end it.
        var old = NewSession(host, SignedInUser, stamp: 7, "rt-old");
        var request = WithCookie(host, HttpMethod.Post, path, old);
        request.Content = path.EndsWith("register", StringComparison.Ordinal)
            ? JsonContent.Create(new
            {
                azureTag = "stamped",
                email = "stamped@example.com",
                password = Password,
                firstName = "Session",
                lastName = "Stamp",
            })
            : JsonContent.Create(new { email = "stamped@example.com", password = Password });

        var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(cookieName + "=", StringComparison.Ordinal));
        var created = cookie[(cookieName.Length + 1)..].Split(';')[0];
        sessions.GetSession(created)!.SessionStamp.Should().Be(7);
    }

    [Fact]
    public async Task ASignInAnsweringAHigherStamp_EndsTheUsersOlderSessions_WithoutWaitingForAPoll()
    {
        // The sign-in's stamp is the API's current one (ADR-0057 §5.3): the map learns it with the
        // session, so a session of the same user given a lower one is refused at once. No tick runs
        // here.
        var (host, upstream, _, _) = NewHost();
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var older = NewSession(host, SignedInUser, stamp: 2, "rt-older");
        upstream.SignInStamp = 3;

        var signIn = await client.PostAsJsonAsync("/bff/auth/login",
            new { email = "stamped@example.com", password = Password });

        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.StampReads.Should().BeEmpty("no poll ran");
        sessions.GetSession(older).Should().BeNull("a sign-in revealed a stamp above it");
    }
}
