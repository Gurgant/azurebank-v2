using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Exceptions;
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
/// The BFF's own doors as the public demo changes them (<c>Demo:Enabled</c>): the claim, which
/// hands a visitor a demo copy and signs them in to it, and registration, which the demo closes.
/// What each answers with the flag on, and with it off, as in every deployment that does not set
/// it.
/// </summary>
/// <remarks>
/// <para>
/// One host per test, with the flag set by <c>UseSetting</c> as a deployment's variable sets it,
/// and an API that is a script: it records each call's path and body and answers by the path.
/// Calls are counted by path, never in all: the host's own client also calls
/// <c>/api/auth/revoke</c> when a session ends and <c>/api/auth/session-stamps</c> while one is
/// held.
/// </para>
/// <para>
/// The test server gives a request no address, so every request of a host is the limiter's one
/// client <c>unknown</c>, with ten calls a minute on the <c>auth</c> policy unless a test sets
/// another number, or gives a request an address (<see cref="FakeRemoteIpStartupFilter"/>).
/// </para>
/// <para>
/// What the claim does to the database, and that the session it opens reads the copy's accounts,
/// is shown with the real API behind the real BFF (<c>DemoClaimSqlServerTests</c> and
/// <c>DemoModeEndpointTests</c> in <c>AzureBank.Tests</c>).
/// </para>
/// </remarks>
public class DemoClaimTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string ClaimPath = "/bff/auth/demo/claim";
    private const string ApiClaimPath = "/api/auth/demo/claim";
    private const string RegisterPath = "/bff/auth/register";
    private const string ApiRegisterPath = "/api/auth/register";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public DemoClaimTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var host in _derived)
        {
            host.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The scripted API: every call it was sent, and an answer for each path.</summary>
    private sealed class Upstream
    {
        /// <summary>The user every claim of this API answers: a copy's owner.</summary>
        public static readonly Guid CopyOwnerId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

        /// <summary>When the access token of a claim expires.</summary>
        public static readonly DateTime AccessTokenExpiresAt = new(2030, 1, 1, 0, 15, 0, DateTimeKind.Utc);

        /// <summary>When the copy of a claim ends: another instant than the token's.</summary>
        public static readonly DateTime CopyExpiresAt = new(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The password of the copy a claim answers, in the shape the API draws one.</summary>
        public const string CopyPassword = "Abcd-Efgh-Jkmn-Pqr2";

        private int _claims;

        /// <summary>Each call in arrival order. A queue, because the grant revoker calls from its own thread.</summary>
        public ConcurrentQueue<(string Path, string Body)> Calls { get; } = new();

        public int CallsTo(string path) => Calls.Count(call => call.Path == path);

        /// <summary>Every grant named in a <c>POST /api/auth/revoke</c>, in arrival order.</summary>
        public ConcurrentQueue<string> RevokedGrants { get; } = new();

        /// <summary>The expiry this API reported for each grant a claim issued.</summary>
        public ConcurrentDictionary<string, DateTime> GrantExpiries { get; } = new();

        /// <summary>
        /// What the claim answers in place of a copy, when a test sets it: a refusal, or an API
        /// that does not answer. It is given the call's own token to wait on.
        /// </summary>
        public Func<CancellationToken, Task<HttpResponseMessage>>? ClaimAnswer { get; set; }

        /// <summary>The address the copy of this API's <paramref name="n"/>th claim signs in with.</summary>
        public static string CopyEmail(int n) => $"demo-0123456789abcde{n}@azurebank.example";

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Enqueue((path, body));

            switch (path)
            {
                case ApiClaimPath:
                    return ClaimAnswer is { } answer
                        ? await answer(cancellationToken)
                        : Json(HttpStatusCode.OK, ClaimedJson());

                case ApiRegisterPath:
                    return Json(HttpStatusCode.Created, RegisteredJson);

                case "/api/auth/revoke":
                    using (var revoke = JsonDocument.Parse(body))
                    {
                        foreach (var grant in revoke.RootElement.GetProperty("refreshTokens").EnumerateArray())
                        {
                            RevokedGrants.Enqueue(grant.GetString()!);
                        }
                    }

                    return Json(HttpStatusCode.OK, """{"success":true,"message":"Revoked"}""");

                default:
                    return Json(HttpStatusCode.OK, """{"data":null,"message":"ok"}""");
            }
        }

        /// <summary>
        /// What the API answers a claim: each claim of this host is given a copy, a token and a
        /// grant of its own, the grant living 15 minutes. That is under the BFF's 60-minute cap, so
        /// a session capped at the grant and one capped at the claim plus 60 differ by 45 minutes.
        /// The stamp is 7, a value no session has unless it was told.
        /// </summary>
        private string ClaimedJson()
        {
            var n = Interlocked.Increment(ref _claims);
            var grant = $"rt-claim-{n}";
            var grantExpiresAt = DateTime.UtcNow.AddMinutes(15);
            GrantExpiries[grant] = grantExpiresAt;
            return $$"""
                {
                  "data": {
                    "token": {
                      "accessToken": "jwt-claim-{{n}}",
                      "refreshToken": "{{grant}}",
                      "refreshTokenExpiresAt": "{{grantExpiresAt:O}}",
                      "sessionStamp": 7,
                      "expiresIn": 899,
                      "tokenType": "Bearer",
                      "expiresAt": "{{AccessTokenExpiresAt:O}}"
                    },
                    "user": {
                      "id": "{{CopyOwnerId}}",
                      "azureTag": "john_k7m{{n}}",
                      "email": "{{CopyEmail(n)}}",
                      "firstName": "John",
                      "lastName": "Smith",
                      "hasPin": true
                    },
                    "copy": {
                      "email": "{{CopyEmail(n)}}",
                      "password": "{{CopyPassword}}",
                      "pin": "123456",
                      "contacts": ["jane_k7m{{n}}", "mike_k7m{{n}}"],
                      "expiresAt": "{{CopyExpiresAt:O}}"
                    }
                  },
                  "message": "Demo copy claimed"
                }
                """;
        }

        /// <summary>A 429 of the API's, in the shape its exception handler writes one.</summary>
        public static HttpResponseMessage Refusal(string errorCode, string detail, int? retryAfterSeconds)
        {
            var wait = retryAfterSeconds is { } seconds ? $",\"retryAfterSeconds\":{seconds}" : string.Empty;
            var response = Json(
                HttpStatusCode.TooManyRequests,
                $$"""{"type":"https://httpstatuses.com/429","title":"Too Many Requests","status":429,"detail":"{{detail}}","instance":"/api/auth/demo/claim","errorCode":"{{errorCode}}","traceId":"deadbeef"{{wait}}}""");
            if (retryAfterSeconds is { } header)
            {
                response.Headers.Add("Retry-After", header.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return response;
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>What the API answers a registration it accepts.</summary>
    private const string RegisteredJson = """
        {
          "data": {
            "account": {
              "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
              "accountNumber": "TEST-0000-0001",
              "name": "Conto principale",
              "type": "Checking",
              "balance": 0,
              "isPrimary": true,
              "createdAt": "2026-09-28T00:00:00Z"
            },
            "token": {
              "accessToken": "jwt-registered",
              "refreshToken": "rt-registered",
              "refreshTokenExpiresAt": "2030-01-01T01:00:00Z",
              "expiresIn": 899,
              "tokenType": "Bearer",
              "expiresAt": "2030-01-01T00:00:00Z"
            },
            "user": {
              "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "azureTag": "newcomer_2026",
              "email": "newcomer@example.com",
              "firstName": "New",
              "lastName": "Comer",
              "hasPin": false
            }
          },
          "message": "Registration successful"
        }
        """;

    private sealed class QueueSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }

    /// <summary>A host with the demo on or off, in front of a scripted API of its own.</summary>
    private (WebApplicationFactory<Program> Host, Upstream Upstream) NewHost(
        bool demo, Action<IWebHostBuilder>? configure = null)
    {
        var upstream = new Upstream();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            if (demo)
            {
                builder.UseSetting("Demo:Enabled", "true");
            }

            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(upstream.RespondAsync));

                // A request's address is the one its X-Test-Client-Ip header names, when it has one.
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
            });
        });
        _derived.Add(host);
        return (host, upstream);
    }

    /// <summary>
    /// A client that keeps no cookies of its own, so every request carries exactly the session the
    /// test names.
    /// </summary>
    private static HttpClient Browser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>A POST with <paramref name="body"/>; by default the empty object the application sends a claim with.</summary>
    private static HttpRequestMessage Post(string path, string? body = "{}", string? mediaType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType!);
        }

        return request;
    }

    private static string CookieName(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

    private static HttpRequestMessage WithCookie(
        WebApplicationFactory<Program> host, HttpRequestMessage request, string sessionId)
    {
        request.Headers.Add("Cookie", $"{CookieName(host)}={sessionId}");
        return request;
    }

    /// <summary>The session the response's cookie names.</summary>
    private static string SessionIdFrom(WebApplicationFactory<Program> host, HttpResponseMessage response)
    {
        var prefix = CookieName(host) + "=";
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue("the answer sets the session cookie");
        var cookie = cookies!.Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..].Split(';')[0];
    }

    /// <summary>A session of another user, opened before the claim: the one a browser's cookie names.</summary>
    private static string NewSession(WebApplicationFactory<Program> host, string grant) =>
        host.Services.GetRequiredService<ISessionService>().CreateSession(
            "jwt-0",
            DateTime.UtcNow.AddMinutes(15),
            grant,
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "earlier",
                Email = "earlier@example.com",
                FirstName = "Earlier",
                LastName = "Visitor",
                HasPin = true,
            });

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

    private static IEnumerable<string> NamesOf(JsonElement json) => json.EnumerateObject().Select(member => member.Name);

    /// <summary>
    /// A response as one text: its status, its content type, the names of its headers and its
    /// body. With parentheses where the body has braces: the assertion library builds a failure's
    /// message with <c>string.Format</c>, and a brace in either of two texts that differ can make
    /// it throw in place of the message that shows where they differ.
    /// </summary>
    private static async Task<string> AnswerOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var headers = response.Headers.Concat(response.Content.Headers)
            .Select(header => header.Key)
            .Order(StringComparer.OrdinalIgnoreCase);
        return $"{(int)response.StatusCode} | {response.Content.Headers.ContentType} | {string.Join(",", headers)} | {body}"
            .Replace('{', '(').Replace('}', ')');
    }

    // ── The claim, with the demo off ─────────────────────────────────────────────────────────────

    public static TheoryData<string, string?, string?> WhatAClaimCanCarry() => new()
    {
        { "an empty object", "{}", "application/json" },
        { "no body at all", null, null },
        { "a form", "clientAddress=203.0.113.7", "application/x-www-form-urlencoded" },
    };

    // CONTROL: green before this change, when no route answered the path. With the action there,
    // [DemoOnly] is what keeps it green: without the marker the request reaches the binder and,
    // past it, the action.
    [Theory]
    [MemberData(nameof(WhatAClaimCanCarry))]
    public async Task WithTheFlagOff_TheClaimIs404_LikeAPathTheBffDoesNotHave_WhateverIsSent(
        string what, string? body, string? mediaType)
    {
        var (host, upstream) = NewHost(demo: false);
        var client = Browser(host);

        using var claim = await client.SendAsync(Post(ClaimPath, body, mediaType));
        using var unknown = await client.SendAsync(Post("/bff/nope", body, mediaType));

        using (new AssertionScope())
        {
            claim.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0}: with the demo off the door is not there", what);
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: the BFF has no such path");
            (await AnswerOfAsync(claim)).Should().Be(
                await AnswerOfAsync(unknown),
                "{0}: the claim is answered as a path the BFF does not have is, in status, headers' names and body", what);
            upstream.CallsTo(ApiClaimPath).Should().Be(0, "nothing is asked of the API");
            claim.Headers.Contains("Set-Cookie").Should().BeFalse();
        }
    }

    // What the 404 above does not hide, pinned so that it is known. The limiter comes before the
    // refusal, and the action carries the auth policy whatever the flag: past that policy's limit
    // the claim's path is answered 429 where a path the BFF does not have is still 404.
    [Fact]
    public async Task WithTheFlagOff_TheClaimsPath_StillSpendsTheAuthLimit()
    {
        var (host, upstream) = NewHost(demo: false, builder => builder.UseSetting("RateLimiting:AuthPermitLimit", "2"));
        var client = Browser(host);

        using var first = await client.SendAsync(Post(ClaimPath));
        using var second = await client.SendAsync(Post(ClaimPath));
        using var third = await client.SendAsync(Post(ClaimPath));
        using var unknown = await client.SendAsync(Post("/bff/nope"));

        using (new AssertionScope())
        {
            first.StatusCode.Should().Be(HttpStatusCode.NotFound);
            second.StatusCode.Should().Be(HttpStatusCode.NotFound);
            third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the 404s were counted by the auth policy");
            (await third.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.RateLimitExceeded);
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: a path with no door has no auth policy to spend");
            upstream.CallsTo(ApiClaimPath).Should().Be(0);
        }
    }

    // It pins what the host answers, so that it is known: the 404 hides the endpoint, which is
    // POST, and not its path. Another method on the path never reaches the action, so no marker of
    // the action's is read; it is answered 405 and told which method the path takes, with the demo
    // off or on, as on the path of sign-in. Red before the action existed: 404, no route.
    [Theory]
    [MemberData(nameof(OtherMethods))]
    public async Task AnotherMethodOnTheClaimsPath_Is405WhateverTheFlag_AsOnSignInsPath(bool demoOn, string method)
    {
        var (host, upstream) = NewHost(demoOn);
        var client = Browser(host);

        using var claim = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), ClaimPath));
        using var login = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/bff/auth/login"));
        using var unknown = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/bff/nope"));

        using (new AssertionScope())
        {
            claim.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            claim.Content.Headers.Allow.Should().Equal("POST");
            login.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "CONTROL: sign-in's path answers another method the same way");
            login.Content.Headers.Allow.Should().Equal("POST");
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: a path with no route is 404 under any method");
            upstream.CallsTo(ApiClaimPath).Should().Be(0);
        }
    }

    // ── The claim, with the demo on ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData(null, "unknown")]
    public async Task AClaim_SendsTheLimitersOwnKeyUpstream(string? connectionAddress, string expected)
    {
        // An auth limit of 1, so the same client's second claim is refused by the limiter, which
        // then logs the partition it counted the client under: the key the first claim sent the
        // API has to be that text.
        var log = new ConcurrentQueue<LogEvent>();
        var (host, upstream) = NewHost(demo: true, builder =>
        {
            builder.UseSetting("RateLimiting:AuthPermitLimit", "1");
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(new QueueSink(log)));
        });
        var client = Browser(host);

        HttpRequestMessage ClaimFromThere()
        {
            // A browser that names an address of its own is not believed: the body's type has no
            // member, so the text below goes nowhere.
            var request = Post(ClaimPath, """{"clientAddress":"198.51.100.200"}""", "application/json");
            if (connectionAddress is not null)
            {
                request.Headers.Add(FakeRemoteIpStartupFilter.HeaderName, connectionAddress);
            }

            return request;
        }

        using var response = await client.SendAsync(ClaimFromThere());
        using var limited = await client.SendAsync(ClaimFromThere());

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "ARRANGE: the limit here is 1");
        var sent = upstream.Calls.Single(call => call.Path == ApiClaimPath).Body;
        using var json = JsonDocument.Parse(sent);
        var rejected = log.Where(e => e.Properties.TryGetValue("SecurityEvent", out var name)
            && name is ScalarValue { Value: string value } && value == SecurityEvents.RateLimitExceeded).ToList();
        using (new AssertionScope())
        {
            json.RootElement.EnumerateObject().Select(member => member.Name).Should().Equal("clientAddress");
            json.RootElement.GetProperty("clientAddress").GetString().Should().Be(expected);
            sent.Should().NotContain("198.51.100.200", "the address is the connection's, never one the browser wrote");

            rejected.Should().HaveCount(1, "the limiter refused the second claim, and says so");
            ((rejected.FirstOrDefault()?.Properties["Partition"] as ScalarValue)?.Value).Should().Be(
                expected, "the claim names the client by the limiter's own key");
        }
    }

    [Fact]
    public async Task AClaim_OpensASession_WithTheAnsweredStampAndGrant_CappedAtTheGrantsEnd_AndSetsTheCookie()
    {
        var (host, upstream) = NewHost(demo: true);
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var cap = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.AbsoluteTimeoutMinutes;
        var client = Browser(host);

        using var response = await client.SendAsync(Post(ClaimPath));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var created = SessionIdFrom(host, response);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        var session = sessions.GetSession(created);
        session.Should().NotBeNull("the cookie names a session the store holds");
        var grantExpiresAt = upstream.GrantExpiries["rt-claim-1"];
        using var me = await client.SendAsync(WithCookie(host, new HttpRequestMessage(HttpMethod.Get, "/bff/auth/me"), created));
        using (new AssertionScope())
        {
            session!.SessionStamp.Should().Be(7, "the stamp the API answered is the one the session is held to");
            session.RefreshToken.Should().Be("rt-claim-1");
            session.GrantExpiresAt.Should().Be(grantExpiresAt);
            session.AccessToken.Should().Be("jwt-claim-1");
            session.TokenExpiry.Should().Be(Upstream.AccessTokenExpiresAt);
            grantExpiresAt.Should().BeBefore(
                session.SessionCreated.AddMinutes(cap - 1),
                "ARRANGE: the grant ends before the configured cap, or the two rules agree and this proves nothing");
            session.AbsoluteExpiresAt.Should().Be(grantExpiresAt, "the session ends when the grant the API reported does");
            session.UserId.Should().Be(Upstream.CopyOwnerId);
            session.UserInfo.Email.Should().Be(Upstream.CopyEmail(1));
            session.UserInfo.AzureTag.Should().Be("john_k7m1");
            session.UserInfo.HasPin.Should().BeTrue();
            session.AuthLevel.Should().Be(1);

            setCookie.Should().StartWith(CookieName(host).ToLowerInvariant() + "=")
                .And.Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/");
            me.StatusCode.Should().Be(HttpStatusCode.OK, "the cookie signs the visitor in to the copy");
        }
    }

    [Fact]
    public async Task AClaim_EndsTheSessionItsCookieNamed_AndRevokesOnlyItsGrant()
    {
        var (host, upstream) = NewHost(demo: true);
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var client = Browser(host);
        var old = NewSession(host, "rt-old");

        using var response = await client.SendAsync(WithCookie(host, Post(ClaimPath), old));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var created = SessionIdFrom(host, response);
        using (new AssertionScope())
        {
            created.Should().NotBe(old);
            sessions.GetSession(old).Should().BeNull("the session the old cookie named has ended");
            sessions.GetSession(created).Should().NotBeNull();
        }

        (await Eventually(() => upstream.RevokedGrants.Contains("rt-old"))).Should().BeTrue(
            "the old session's grant is revoked at the API");
        await Task.Delay(200);
        upstream.RevokedGrants.Should().Equal(["rt-old"], "the new session's grant is not touched");

        // Its control: the new grant can be revoked, and is, once its own session ends. Without it
        // the line above would pass against a script that recorded no revoke of that grant at all.
        using var logout = await client.SendAsync(
            WithCookie(host, new HttpRequestMessage(HttpMethod.Post, "/bff/auth/logout"), created));
        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Eventually(() => upstream.RevokedGrants.Contains("rt-claim-1"))).Should().BeTrue();
    }

    [Fact]
    public async Task AClaimArrivingWithAPinVerifiedSession_OpensOneAtAuthLevelOne()
    {
        var (host, _) = NewHost(demo: true);
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var old = NewSession(host, "rt-old");
        sessions.SetPinVerified(old);
        sessions.GetAuthLevel(old).Should().Be(2, "ARRANGE: the elevation has to exist to be dropped");

        using var response = await Browser(host).SendAsync(WithCookie(host, Post(ClaimPath), old));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var created = SessionIdFrom(host, response);
        using (new AssertionScope())
        {
            // The PIN was proved to the session that just ended, and for another user.
            sessions.GetAuthLevel(created).Should().Be(1);
            sessions.GetSession(created)!.AuthLevel.Should().Be(1);
            sessions.GetSession(created)!.PinVerifiedAt.Should().BeNull();
            sessions.IsPinVerificationValid(created).Should().BeFalse();
        }
    }

    [Fact]
    public async Task TheAnswer_IsTheEnvelope_WithTheUserTheCopyAndNoToken_AndIsNeverStored()
    {
        var (host, _) = NewHost(demo: true);

        using var response = await Browser(host).SendAsync(Post(ClaimPath));

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        using (new AssertionScope())
        {
            NamesOf(root).Should().BeEquivalentTo(["data", "message"]);
            var data = root.GetProperty("data");
            NamesOf(data).Should().BeEquivalentTo(["user", "expiresAt", "copy"]);

            // The user, as sign-in answers it.
            var user = data.GetProperty("user");
            NamesOf(user).Should().BeEquivalentTo(["id", "email", "firstName", "lastName", "azureTag", "hasPin"]);
            user.GetProperty("id").GetGuid().Should().Be(Upstream.CopyOwnerId);
            user.GetProperty("email").GetString().Should().Be(Upstream.CopyEmail(1));
            user.GetProperty("azureTag").GetString().Should().Be("john_k7m1");
            user.GetProperty("hasPin").GetBoolean().Should().BeTrue();

            // The access token's expiry, as sign-in answers it; the copy's end is the copy's own.
            data.GetProperty("expiresAt").GetDateTime().Should().Be(Upstream.AccessTokenExpiresAt);

            // The copy, as the API answered it: what signs in to it again, whom it pays, its end.
            var copy = data.GetProperty("copy");
            NamesOf(copy).Should().BeEquivalentTo(["email", "password", "pin", "contacts", "expiresAt"]);
            copy.GetProperty("email").GetString().Should().Be(Upstream.CopyEmail(1));
            copy.GetProperty("password").GetString().Should().Be(Upstream.CopyPassword);
            copy.GetProperty("pin").GetString().Should().Be("123456");
            copy.GetProperty("contacts").EnumerateArray().Select(contact => contact.GetString())
                .Should().Equal("jane_k7m1", "mike_k7m1");
            copy.GetProperty("expiresAt").GetDateTime().Should().Be(Upstream.CopyExpiresAt);

            // No token, by its name or by its value: the two stay in the BFF's store.
            text.Should().NotContain("oken", "no member of the answer is a token's")
                .And.NotContain("jwt-claim-1").And.NotContain("rt-claim-1").And.NotContain("sessionStamp");

            // The answer carries a password: nothing may keep it.
            (response.Headers.CacheControl?.NoStore).Should().BeTrue();
            response.Headers.Pragma.ToString().Should().Be("no-cache");
            (response.Content.Headers.ContentType?.MediaType).Should().Be("application/json");
        }
    }

    // CONTROL: green before this change. The answer is the one place the copy's password is
    // written to; the email and the two tokens stay out of the log as well. What the log does name
    // is the user's id.
    [Fact]
    public async Task AClaim_WritesNoLogLineThatHoldsThePasswordTheEmailOrAToken()
    {
        var log = new ConcurrentQueue<LogEvent>();
        var (host, _) = NewHost(demo: true, builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(new QueueSink(log))));

        using var response = await Browser(host).SendAsync(Post(ClaimPath));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await Eventually(() => log.Any(e => e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod}", StringComparison.Ordinal))))
            .Should().BeTrue("ARRANGE: the request's own line is written last, so the claim's lines are all in");

        // Each event as one text: its message, the value of every property, its exception. With
        // parentheses for braces, as AnswerOfAsync does and for its reason.
        var written = log
            .Select(e => string.Join(" ", e.Properties.Select(p => p.Value.ToString()).Prepend(e.RenderMessage()).Append(e.Exception?.ToString())))
            .Select(line => line.Replace('{', '(').Replace('}', ')'))
            .ToList();
        using (new AssertionScope())
        {
            written.Should().Contain(
                line => line.Contains("claimed a demo copy") && line.Contains(Upstream.CopyOwnerId.ToString()),
                "CONTROL: the claim's own line is among those read, and names the user by id");
            foreach (var secret in new[] { Upstream.CopyPassword, Upstream.CopyEmail(1), "jwt-claim-1", "rt-claim-1" })
            {
                written.Should().NotContain(line => line.Contains(secret), "{0} stays out of the log", secret);
            }
        }
    }

    /// <summary>Bodies no page of the application sends, each with the status the binder answers it.</summary>
    public static TheoryData<string, string?, string?, int> HostileBodies() => new()
    {
        { "a form with no field", "", "application/x-www-form-urlencoded", 415 },
        { "a form with one field", "clientAddress=203.0.113.7", "application/x-www-form-urlencoded", 415 },
        { "a form sent in parts, with one field", "203.0.113.7", MultipartForm, 415 },
        { "plain text carrying an object", "{}", "text/plain", 415 },
        { "no body and no content type", null, null, 415 },
        { "JSON with nothing in it", "", "application/json", 400 },
        { "JSON that is not an object", "[]", "application/json", 400 },
        { "JSON that is null", "null", "application/json", 400 },
    };

    private const string MultipartForm = "multipart/form-data";

    [Theory]
    [MemberData(nameof(HostileBodies))]
    public async Task HostileBodies_AreRefusedBeforeTheAction_AndReachNoApi(
        string what, string? body, string? mediaType, int expected)
    {
        // A page of another site can make a browser send a form or plain text without asking
        // first; it cannot make it send JSON. The claim takes JSON only, so those requests stop at
        // the binder, whatever the cookie and the Fetch-Metadata rules did before it.
        var (host, upstream) = NewHost(demo: true);
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var old = NewSession(host, "rt-old");

        // The form in parts is built as a browser builds one, boundary and all; its one field
        // is named as the API's request names the address.
        var request = mediaType == MultipartForm
            ? new HttpRequestMessage(HttpMethod.Post, ClaimPath)
            {
                Content = new MultipartFormDataContent { { new StringContent(body!), "clientAddress" } },
            }
            : Post(ClaimPath, body, mediaType);

        using var response = await Browser(host).SendAsync(WithCookie(host, request, old));

        using (new AssertionScope())
        {
            ((int)response.StatusCode).Should().Be(expected, "{0} ({1})", what, await AnswerOfAsync(response));
            upstream.CallsTo(ApiClaimPath).Should().Be(0, "{0}: the API is not asked for a copy", what);
            response.Headers.Contains("Set-Cookie").Should().BeFalse();
            sessions.GetSession(old).Should().NotBeNull("the session the cookie named is left as it was");
        }
    }

    // CONTROL: green before this change. The Fetch-Metadata rule reads no path, so it stands
    // before the claim as before every other door (FetchMetadataTests runs it on sign-in's). This
    // runs it on the claim's: BffDemoClaimRequest's remark leans on it for a browser that says
    // where a request comes from.
    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task AClaimFromAnotherSite_Is403_AndReachesNoApi(string site)
    {
        var (host, upstream) = NewHost(demo: true);
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var old = NewSession(host, "rt-old");
        var client = Browser(host);
        var request = WithCookie(host, Post(ClaimPath), old);
        request.Headers.Add("Sec-Fetch-Site", site);

        using var response = await client.SendAsync(request);

        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync()).Should().Contain("CROSS_SITE_REQUEST_BLOCKED");
            upstream.CallsTo(ApiClaimPath).Should().Be(0, "the API is not asked for a copy");
            response.Headers.Contains("Set-Cookie").Should().BeFalse();
            sessions.GetSession(old).Should().NotBeNull("the session the cookie named is left as it was");
        }

        // Its control: the same claim from the application's own page is served. Without it the
        // lines above would pass on a host that refused every claim that names a site.
        var fromThePage = Post(ClaimPath);
        fromThePage.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var served = await client.SendAsync(fromThePage);
        using (new AssertionScope())
        {
            served.StatusCode.Should().Be(HttpStatusCode.OK, "CONTROL: a claim from the same origin is served");
            upstream.CallsTo(ApiClaimPath).Should().Be(1);
        }
    }

    public static TheoryData<string, string, int?> ApiRefusals() => new()
    {
        { ErrorCodes.DemoDailyLimit, DemoRefusalException.DailyLimitDetail, 600 },
        { ErrorCodes.DemoPoolEmpty, DemoRefusalException.PoolEmptyDetail, null },
    };

    [Theory]
    [MemberData(nameof(ApiRefusals))]
    public async Task AnApiRefusal_IsForwardedWithItsRetryAfter_AndTheOldSessionLives(
        string errorCode, string detail, int? retryAfterSeconds)
    {
        var (host, upstream) = NewHost(demo: true);
        upstream.ClaimAnswer = _ => Task.FromResult(Upstream.Refusal(errorCode, detail, retryAfterSeconds));
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var old = NewSession(host, "rt-old");

        using var response = await Browser(host).SendAsync(WithCookie(host, Post(ClaimPath), old));

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        using (new AssertionScope())
        {
            root.GetProperty("errorCode").GetString().Should().Be(errorCode);
            root.GetProperty("detail").GetString().Should().Be(detail);
            root.GetProperty("status").GetInt32().Should().Be(429);
            root.GetProperty("traceId").GetString().Should().Be("deadbeef", "the API's own body, as it wrote it");

            if (retryAfterSeconds is { } seconds)
            {
                (response.Headers.RetryAfter?.Delta).Should().Be(TimeSpan.FromSeconds(seconds), "the API's Retry-After comes with its refusal");
                root.GetProperty("retryAfterSeconds").GetInt32().Should().Be(seconds);
            }
            else
            {
                response.Headers.Contains("Retry-After").Should().BeFalse("the API named no wait, and the BFF invents none");
                root.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
            }

            upstream.CallsTo(ApiClaimPath).Should().Be(1);
            response.Headers.Contains("Set-Cookie").Should().BeFalse("no session was opened, and the cookie is left alone");
            sessions.GetSession(old).Should().NotBeNull("a refused claim leaves the visitor in the session they had");
        }
    }

    [Fact]
    public async Task AnApiWithTheDemoOff_MakesTheClaim404()
    {
        // The two hosts' flags are two settings. A BFF with the demo on in front of an API with it
        // off asks, and is answered as for a path the API does not have: the mismatch fails closed.
        var (host, upstream) = NewHost(demo: true);
        upstream.ClaimAnswer = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"traceId":"deadbeef"}""",
                Encoding.UTF8,
                "application/problem+json"),
        });

        using var response = await Browser(host).SendAsync(Post(ClaimPath));

        var text = await response.Content.ReadAsStringAsync();
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, text);
            upstream.CallsTo(ApiClaimPath).Should().Be(1, "the 404 is the API's: this BFF has the door and asked");
            text.Should().Contain("\"title\":\"Not Found\"").And.Contain("deadbeef", "the API's body comes with its status");
            response.Headers.Contains("Set-Cookie").Should().BeFalse();
        }
    }

    [Fact]
    public async Task PastTheAuthLimit_TheClaimIs429RateLimitExceeded()
    {
        var (host, upstream) = NewHost(demo: true, builder => builder.UseSetting("RateLimiting:AuthPermitLimit", "2"));
        var client = Browser(host);

        using var first = await client.SendAsync(Post(ClaimPath));
        using var second = await client.SendAsync(Post(ClaimPath));
        using var third = await client.SendAsync(Post(ClaimPath));

        var text = await third.Content.ReadAsStringAsync();
        using (new AssertionScope())
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
            text.Should().Contain(ErrorCodes.RateLimitExceeded);
            third.Headers.Contains("Retry-After").Should().BeTrue();
            third.Headers.Contains("Set-Cookie").Should().BeFalse();
            upstream.CallsTo(ApiClaimPath).Should().Be(2, "the third claim never left the BFF");
        }
    }

    /// <summary>An API whose connection is refused, as while it restarts.</summary>
    private static Task<HttpResponseMessage> Refusing(CancellationToken cancellationToken) =>
        throw new HttpRequestException("Connection refused (test)");

    /// <summary>An API that answers nothing: it waits on the call's own token until the BFF gives up.</summary>
    private static async Task<HttpResponseMessage> Silent(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }

    /// <summary>An API that answers a success the BFF cannot read a copy from.</summary>
    private static Task<HttpResponseMessage> WithNoCopy(CancellationToken cancellationToken) =>
        Task.FromResult(Upstream.Json(HttpStatusCode.OK, """{"data":null,"message":"ok"}"""));

    [Theory]
    [InlineData("it cannot be reached")]
    [InlineData("it answers nothing in time")]
    [InlineData("it answers a success with no copy in it")]
    public async Task AnApiThatDoesNotAnswer_Is503_AndOpensNoSession(string how)
    {
        // One second, so the row that waits for the BFF's own timeout is quick.
        var (host, upstream) = NewHost(demo: true, builder => builder.UseSetting("BackendApi:TimeoutSeconds", "1"));
        upstream.ClaimAnswer = how switch
        {
            "it cannot be reached" => Refusing,
            "it answers nothing in time" => Silent,
            _ => WithNoCopy,
        };
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var old = NewSession(host, "rt-old");

        using var response = await Browser(host).SendAsync(WithCookie(host, Post(ClaimPath), old));

        await BackendTimeoutTests.AssertOutageAsync(response);
        using (new AssertionScope())
        {
            upstream.CallsTo(ApiClaimPath).Should().Be(1);
            response.Headers.Contains("Set-Cookie").Should().BeFalse("no session is opened on an answer that never came");
            sessions.GetSession(old).Should().NotBeNull("an outage ends nobody's session");
        }
    }

    // ── Registration ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A registration the BFF's binder accepts and the scripted API answers 201.</summary>
    private const string ARegistration =
        """{"azureTag":"newcomer_2026","email":"newcomer@example.com","password":"TestPass123!","firstName":"New","lastName":"Comer"}""";

    private static HttpRequestMessage RegistrationOf(string? body, string? mediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RegisterPath);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType!);
        }

        return request;
    }

    /// <summary>Requests to registration, each with the status a host with the demo off answers it.</summary>
    public static TheoryData<string, string?, string?, int> Registrations() => new()
    {
        { "a registration that would be accepted", ARegistration, "application/json", 201 },
        { "an empty object", "{}", "application/json", 400 },
        { "a form post", "email=newcomer%40example.com", "application/x-www-form-urlencoded", 415 },
        { "plain text", "let me in", "text/plain", 415 },
        { "no body at all", null, null, 415 },
    };

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task InDemoMode_RegistrationIs403_WhateverTheBody_AndTheApiIsNeverCalled(
        string what, string? body, string? mediaType, int withTheDemoOff)
    {
        // CONTROL: where registration is open the same request is answered by the binder or by the
        // API, each in its own way. On the demo neither is reached.
        var (ordinary, _) = NewHost(demo: false);
        using var there = await ordinary.CreateClient().SendAsync(RegistrationOf(body, mediaType));
        ((int)there.StatusCode).Should().Be(
            withTheDemoOff, "CONTROL: {0}, on a host with the demo off", what);

        var (host, upstream) = NewHost(demo: true);

        using var response = await host.CreateClient().SendAsync(RegistrationOf(body, mediaType));

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "{0}: on the demo registration is closed", what);
        using var problem = JsonDocument.Parse(text);
        var root = problem.RootElement;
        using (new AssertionScope())
        {
            // The API's own refusal of a registration on the demo, member for member, written here
            // without asking it.
            root.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
                ["type", "title", "status", "detail", "instance", "errorCode", "traceId"]);
            root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.RegistrationClosed);
            root.GetProperty("detail").GetString().Should().Be(RegistrationClosedException.Detail);
            root.GetProperty("status").GetInt32().Should().Be(403);
            root.GetProperty("title").GetString().Should().Be("Forbidden");
            root.GetProperty("type").GetString().Should().Be("https://httpstatuses.com/403");
            root.GetProperty("instance").GetString().Should().Be(RegisterPath);
            root.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace("a refusal can be found in the log by it");
            (response.Content.Headers.ContentType?.MediaType).Should().Be("application/json");

            upstream.CallsTo(ApiRegisterPath).Should().Be(0, "the BFF refuses by itself: the API is not asked");
            response.Headers.Contains("Set-Cookie").Should().BeFalse("no session is opened");
        }
    }

    // CONTROL: green before this change. Where the demo is off, registration is open, as it was.
    [Fact]
    public async Task WithTheFlagOff_RegistrationStillReachesTheApi()
    {
        var (host, upstream) = NewHost(demo: false);

        using var response = await host.CreateClient().SendAsync(RegistrationOf(ARegistration, "application/json"));

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        using var body = JsonDocument.Parse(text);
        using (new AssertionScope())
        {
            body.RootElement.GetProperty("data").GetProperty("user").GetProperty("email").GetString()
                .Should().Be("newcomer@example.com");
            upstream.CallsTo(ApiRegisterPath).Should().Be(1);
            upstream.Calls.Single(call => call.Path == ApiRegisterPath).Body.Should().Contain("newcomer_2026");
            response.Headers.Contains("Set-Cookie").Should().BeTrue("a registration opens a session");
        }
    }

    [Fact]
    public async Task InDemoMode_AClosedRegistration_StillSpendsTheAuthLimit()
    {
        // The limiter comes before the refusal: a closed door is still a door somebody can hammer,
        // and the client that does is told to slow down as on any other door of this policy.
        var (host, upstream) = NewHost(demo: true, builder => builder.UseSetting("RateLimiting:AuthPermitLimit", "2"));
        var client = host.CreateClient();

        using var first = await client.SendAsync(RegistrationOf(ARegistration, "application/json"));
        using var second = await client.SendAsync(RegistrationOf(ARegistration, "application/json"));
        using var third = await client.SendAsync(RegistrationOf(ARegistration, "application/json"));

        var text = await third.Content.ReadAsStringAsync();
        using (new AssertionScope())
        {
            first.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
            text.Should().Contain(ErrorCodes.RateLimitExceeded);
            third.Headers.Contains("Retry-After").Should().BeTrue();
            upstream.CallsTo(ApiRegisterPath).Should().Be(0);
        }
    }

    public static TheoryData<bool, string> OtherMethods()
    {
        var rows = new TheoryData<bool, string>();
        foreach (var demoOn in new[] { false, true })
        {
            foreach (var method in new[] { "GET", "HEAD", "PUT", "PATCH", "DELETE", "OPTIONS" })
            {
                rows.Add(demoOn, method);
            }
        }

        return rows;
    }

    // CONTROL: green before this change. It pins what the host answers today, so that it is known:
    // the 403 closes the endpoint, which is POST, and not its path. Another method on the path
    // never reaches the action, so no marker of the action's is read; it is answered 405 and told
    // which method the path takes, with the demo off or on, as on the path of sign-in.
    [Theory]
    [MemberData(nameof(OtherMethods))]
    public async Task AnotherMethodOnRegistrationsPath_Is405WhateverTheFlag_AsOnSignInsPath(bool demoOn, string method)
    {
        var (host, upstream) = NewHost(demoOn);
        var client = host.CreateClient();

        using var register = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), RegisterPath));
        using var login = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/bff/auth/login"));
        using var unknown = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/bff/nope"));

        using (new AssertionScope())
        {
            register.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            register.Content.Headers.Allow.Should().Equal("POST");
            login.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "CONTROL: sign-in's path answers another method the same way");
            login.Content.Headers.Allow.Should().Equal("POST");
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: a path with no route is 404 under any method");
            upstream.CallsTo(ApiRegisterPath).Should().Be(0);
        }
    }
}
