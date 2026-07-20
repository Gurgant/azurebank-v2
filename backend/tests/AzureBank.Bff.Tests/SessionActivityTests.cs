using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
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
public class SessionActivityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SessionActivityTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (string SessionId, string CookieName, ISessionService Sessions) CreateSession()
    {
        var sessions = _factory.Services.GetRequiredService<ISessionService>();
        var cookieName = _factory.Services
            .GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(
            "fake-jwt",
            DateTime.UtcNow.AddHours(1),
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
}
