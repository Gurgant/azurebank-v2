using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Services.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// U6.7 — re-authentication at the absolute session cap.
///
/// The cap is not extendable, so reaching it has to mint a DIFFERENT session: new id, new cookie,
/// new window, no PIN elevation carried over. These tests pin that, and they pin the two mistakes
/// the obvious implementation makes.
///
/// `ISessionService` is a singleton, so the tests resolve it from the host and read the very records
/// the requests are served from — the assertions are about session state, not only about status
/// codes. A test that only checked the 200 would pass against an endpoint that never replaced
/// anything.
/// </summary>
public class ReauthenticateTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CookieName = ".AzureBank.Session"; // Development: unprefixed
    private const string SessionEmail = "cookie@example.com";
    private const string GoodPassword = "Password1!";

    private static string LoginSuccessJson(string token) => $$"""
        {
          "data": {
            "token": "{{token}}",
            "expiresAt": "2030-01-01T00:00:00Z",
            "refreshToken": "fake-refresh-{{token}}",
            "user": {
              "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "azureTag": "cookieuser",
              "email": "{{SessionEmail}}",
              "firstName": "Cookie",
              "lastName": "User",
              "hasPin": true
            }
          },
          "message": "Login successful"
        }
        """;

    private const string InvalidCredentialsJson = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
          "title": "Unauthorized",
          "status": 401,
          "detail": "Invalid email or password.",
          "errorCode": "INVALID_CREDENTIALS"
        }
        """;

    /// <summary>
    /// Records every upstream path the BFF called and answers login with success or 401 depending on
    /// the password it is handed. Recording is what makes the "never calls /api/auth/logout" test
    /// possible: that call's absence is the property, and absence cannot be observed from a response.
    /// </summary>
    private sealed class Upstream
    {
        public List<string> Paths { get; } = [];
        public List<string> LoginBodies { get; } = [];
        public int LoginCount { get; set; }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            if (path == "/api/auth/login")
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                LoginBodies.Add(body);
                LoginCount++;

                using var parsed = JsonDocument.Parse(body);
                var password = parsed.RootElement.GetProperty("password").GetString();
                if (password != GoodPassword)
                {
                    return Json(HttpStatusCode.Unauthorized, InvalidCredentialsJson);
                }
                // A DIFFERENT token per login, so "the session was replaced" is observable in the
                // store rather than inferred from the id alone.
                return Json(HttpStatusCode.OK, LoginSuccessJson($"jwt-{LoginCount}"));
            }

            return Json(HttpStatusCode.OK, """{"message":"ok"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private readonly WebApplicationFactory<Program> _factory;

    public ReauthenticateTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (WebApplicationFactory<Program> Host, Upstream Upstream) NewHost()
    {
        var upstream = new Upstream();
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(upstream.Respond))));
        return (host, upstream);
    }

    private static async Task<string> Login(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/bff/auth/login", new { email = SessionEmail, password = GoodPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return SessionIdFrom(response);
    }

    private static string SessionIdFrom(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        return cookies!.Single(c => c.StartsWith(CookieName, StringComparison.Ordinal))
            .Split(';')[0].Split('=', 2)[1];
    }

    private static async Task<HttpResponseMessage> Reauthenticate(
        HttpClient client, string? sessionId, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/reauthenticate")
        {
            Content = JsonContent.Create(body)
        };
        if (sessionId is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={sessionId}");
        }
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ItIssuesANewSessionAndRetiresTheOldOne()
    {
        var (host, _) = NewHost();
        var client = host.CreateClient();
        var sessions = host.Services.GetRequiredService<ISessionService>();

        var oldId = await Login(client);
        sessions.GetSession(oldId).Should().NotBeNull();

        var response = await Reauthenticate(client, oldId, new { password = GoodPassword });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var newId = SessionIdFrom(response);
        newId.Should().NotBe(oldId, "an absolute cap you can keep the same session through is decoration");

        // The whole point: a different session OBJECT, and the previous one is gone rather than
        // merely unreachable. The new record also carries the second token, which proves the
        // credential was actually re-verified and not just re-cookied.
        sessions.GetSession(oldId).Should().BeNull("the retired session must not survive as a spare key");
        var replacement = sessions.GetSession(newId);
        replacement.Should().NotBeNull();
        replacement!.AccessToken.Should().Be("jwt-2");
        replacement.SessionCreated.Should().BeOnOrAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task AWrongPasswordLeavesTheSessionAliveAndStillUsable()
    {
        // The property that matters most. Before this endpoint existed, the absolute branch offered a
        // button that did nothing; an endpoint that ended the session on a typo would be WORSE than
        // that — it would turn a mistake into the outcome the user was trying to avoid.
        var (host, _) = NewHost();
        var client = host.CreateClient();
        var sessions = host.Services.GetRequiredService<ISessionService>();

        var sessionId = await Login(client);

        var response = await Reauthenticate(client, sessionId, new { password = "Wrong1234!" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain(
            "INVALID_CREDENTIALS",
            "the client's global 401 rule exempts exactly this code from 'session expired', so the "
                + "dialog can show the error instead of the app signing the user out");
        response.Headers.Should().NotContainKey("Set-Cookie", "nothing was replaced");

        sessions.GetSession(sessionId).Should().NotBeNull();
        using var me = new HttpRequestMessage(HttpMethod.Get, "/bff/auth/me");
        me.Headers.Add("Cookie", $"{CookieName}={sessionId}");
        (await client.SendAsync(me)).StatusCode.Should().Be(
            HttpStatusCode.OK, "the session must still work — the user is still signed in");
    }

    [Fact]
    public async Task WithoutASessionItIs401AndTheCredentialIsNeverForwarded()
    {
        var (host, upstream) = NewHost();

        var response = await Reauthenticate(
            host.CreateClient(), sessionId: null, new { password = GoodPassword });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // There is no session to re-authenticate INTO, so this is not a login endpoint in disguise:
        // it must not spend an upstream attempt (or a lockout count) on an anonymous caller.
        upstream.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task ItNeverCallsTheApiLogout_WhichWouldRevokeTheTokensItJustMinted()
    {
        // The API's /api/auth/logout runs RevokeAllForUserAsync — EVERY refresh token this user
        // holds, including the pair minted moments earlier. "Re-auth = logout + login" therefore
        // produces a session that dies silently at its first re-mint. The negative control below is
        // what makes this assertion falsifiable rather than vacuous.
        var (host, upstream) = NewHost();
        var client = host.CreateClient();

        var oldId = await Login(client);
        var response = await Reauthenticate(client, oldId, new { password = GoodPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        upstream.Paths.Should().NotContain("/api/auth/logout");
        upstream.Paths.Should().Equal("/api/auth/login", "/api/auth/login");

        // Negative control: a real logout DOES call it. Without this, the assertion above would
        // still pass against a BFF that had lost the ability to reach that path at all.
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/logout");
        logout.Headers.Add("Cookie", $"{CookieName}={SessionIdFrom(response)}");
        (await client.SendAsync(logout)).StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.Paths.Should().Contain("/api/auth/logout");
    }

    [Fact]
    public async Task TheNewSessionDoesNotInheritPinElevation()
    {
        var (host, _) = NewHost();
        var client = host.CreateClient();
        var sessions = host.Services.GetRequiredService<ISessionService>();

        var oldId = await Login(client);
        sessions.SetPinVerified(oldId);
        sessions.GetAuthLevel(oldId).Should().Be(2, "guard: the elevation has to exist to be dropped");

        var response = await Reauthenticate(client, oldId, new { password = GoodPassword });

        // A new session starts at level 1. Carrying elevation across would let the cap refresh a
        // money-grade permission the user proved only to the session that just ended.
        sessions.GetAuthLevel(SessionIdFrom(response)).Should().Be(1);
    }

    [Fact]
    public async Task TheCallerCannotChooseWhoToSignInAs()
    {
        // Re-authentication happens IN PLACE — same route, same mounted page, same half-filled form.
        // If the request could name an identity, whoever is at the keyboard could put their own
        // session behind somebody else's screen. The request type has no email field at all; this
        // proves an extra one in the JSON is ignored rather than bound.
        var (host, upstream) = NewHost();
        var client = host.CreateClient();

        var sessionId = await Login(client);
        var response = await Reauthenticate(
            client, sessionId, new { password = GoodPassword, email = "attacker@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.LoginBodies.Should().HaveCount(2);
        upstream.LoginBodies[1].Should().Contain(SessionEmail).And.NotContain("attacker@example.com");
    }
}
