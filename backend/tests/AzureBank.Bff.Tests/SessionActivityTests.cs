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
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The inactivity timeout's honesty depends on WHICH requests count as activity
/// (ADR-0018): GET /bff/auth/session-status is the frontend's safe probe and must NOT
/// refresh LastActivity — otherwise any status poll keeps the session alive forever —
/// while /bff/auth/me is the deliberate "Stay signed in" refresher and MUST.
/// Sessions are created directly against the in-process session service.
/// </summary>
public class SessionActivityTests : IClassFixture<SessionActivityTests.ApiStubbedFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SessionActivityTests(ApiStubbedFactory factory) => _factory = factory;

    /// <summary>
    /// The BFF with its OWN client to the API stubbed. The sessions here hold grants, and since PR-1
    /// a host that stops revokes the grants it held (GrantRevoker's drain, 06 §4.6): on the plain
    /// factory that drain sent /api/auth/revoke to the configured API address, a live call on a
    /// machine where the API runs. Measured before: "0 grants revoked, 5 left" at this class's
    /// teardown. /me's read-through meets the stub too, whose body carries no user, so /me serves its
    /// cached copy exactly as it did with no API to reach.
    /// </summary>
    public sealed class ApiStubbedFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"message":"ok"}""", Encoding.UTF8, "application/json"),
                    })));
    }

    private (string SessionId, string CookieName, ISessionService Sessions) CreateSession()
    {
        var sessions = _factory.Services.GetRequiredService<ISessionService>();
        var cookieName = _factory.Services
            .GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(
            "fake-jwt",
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "activityuser",
                Email = "activity@example.com",
                FirstName = "Activity",
                LastName = "User",
                HasPin = false
            });
        return (sessionId, cookieName, sessions);
    }

    private static HttpRequestMessage Get(string path, string cookieName, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        return request;
    }

    [Fact]
    public async Task SessionStatusProbe_DoesNotRefreshLastActivity()
    {
        var (sessionId, cookieName, sessions) = CreateSession();
        var client = _factory.CreateClient();
        var baseline = sessions.GetSession(sessionId)!.LastActivity;

        await Task.Delay(30); // guarantee a clock difference if activity WERE refreshed

        var response = await client.SendAsync(Get("/bff/auth/session-status", cookieName, sessionId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue(
            "the probe must still SEE the session — it just must not keep it alive");

        sessions.GetSession(sessionId)!.LastActivity.Should().Be(baseline,
            "polling session-status must not neutralize the inactivity timeout");
    }

    [Fact]
    public async Task SessionStatusProbe_WithTrailingSlash_DoesNotRefreshLastActivityEither()
    {
        // The middleware runs BEFORE routing; routing tolerates one trailing slash, so
        // the exclusion must too or a slash-suffixed poll silently counts as activity.
        var (sessionId, cookieName, sessions) = CreateSession();
        var client = _factory.CreateClient();
        var baseline = sessions.GetSession(sessionId)!.LastActivity;

        await Task.Delay(30);

        await client.SendAsync(Get("/bff/auth/session-status/", cookieName, sessionId));

        sessions.GetSession(sessionId)!.LastActivity.Should().Be(baseline);
    }

    [Fact]
    public async Task MeEndpoint_DoesRefreshLastActivity()
    {
        var (sessionId, cookieName, sessions) = CreateSession();
        var client = _factory.CreateClient();
        var baseline = sessions.GetSession(sessionId)!.LastActivity;

        await Task.Delay(30);

        var response = await client.SendAsync(Get("/bff/auth/me", cookieName, sessionId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        sessions.GetSession(sessionId)!.LastActivity.Should().BeAfter(baseline,
            "/bff/auth/me is the deliberate keep-alive — 'Stay signed in' depends on it");
    }

    /// <summary>
    /// The probe must report a deadline that FALLS. That is the whole reason the frontend can show
    /// a countdown at all: every other endpoint slides LastActivity as a side effect of being
    /// called, so the deadline it reports is always the full window and never moves.
    /// </summary>
    [Fact]
    public async Task SessionStatusProbe_ReportsAnInactivityDeadlineThatCountsDown()
    {
        var (sessionId, cookieName, _) = CreateSession();
        var client = _factory.CreateClient();

        var first = await ReadProbeDeadlines(client, cookieName, sessionId);
        await Task.Delay(1100);
        var second = await ReadProbeDeadlines(client, cookieName, sessionId);

        // Identical deadlines across two probes a second apart: probing did not push them out.
        second.Inactivity.Should().Be(first.Inactivity);
        second.Absolute.Should().Be(first.Absolute);
        // And the remaining time genuinely shrank, which is what a countdown needs. Measured at
        // each probe rather than derived afterwards — comparing a fixed deadline against a later
        // clock reading is true by arithmetic and would assert nothing.
        second.Remaining.Should().BeLessThan(first.Remaining);
    }

    /// <summary>
    /// The two rules are independent, and a client that watches only one of them is wrong half the
    /// time. Activity slides the inactivity deadline; nothing moves the absolute one.
    /// </summary>
    [Fact]
    public async Task Me_SlidesTheInactivityDeadlineButNotTheAbsoluteOne()
    {
        var (sessionId, cookieName, _) = CreateSession();
        var client = _factory.CreateClient();

        var before = await ReadProbeDeadlines(client, cookieName, sessionId);
        await Task.Delay(1100);
        (await client.SendAsync(Get("/bff/auth/me", cookieName, sessionId)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadProbeDeadlines(client, cookieName, sessionId);

        after.Inactivity.Should().BeAfter(before.Inactivity);
        after.Absolute.Should().Be(before.Absolute);
    }

    private async Task<(DateTime Inactivity, DateTime Absolute, TimeSpan Remaining)> ReadProbeDeadlines(
        HttpClient client, string cookieName, string sessionId)
    {
        var response = await client.SendAsync(Get("/bff/auth/session-status", cookieName, sessionId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var inactivity = root.GetProperty("inactivityExpiresAt").GetDateTime();
        return (
            inactivity,
            root.GetProperty("absoluteExpiresAt").GetDateTime(),
            inactivity - DateTime.UtcNow);
    }
}
