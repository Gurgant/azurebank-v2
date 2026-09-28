extern alias bff;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;
using BffSessionOptions = bff::AzureBank.Bff.Options.BffSessionOptions;
using BffSessions = bff::AzureBank.Bff.Services.Interfaces.ISessionService;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Sessions seen from both hosts at once: the real BFF in front of the real API
/// (<see cref="BffOverApiFactory"/>, InMemory database). What the browser does goes through the
/// BFF; what the API answered and wrote is read from the API.
/// </summary>
public sealed class BffOverApiSessionTests
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Password = "SecurePass123!";

    /// <summary>
    /// A key the API does not hold: the BFF's half of a key rotation applied on one side only.
    /// Test-only value, NOT a real secret.
    /// </summary>
    private const string KeyTheApiDoesNotHold =
        "integration-tests-only-a-rotated-bff-key-the-api-lacks-0123456789";

    [Fact]
    public async Task SigningOutOneSession_LeavesTheUsersOtherSessionRenewing_AndRecordsNoReuse()
    {
        // ADR-0057 §10 O0-2 item 3 (F9e). "Esci" ends ONE session (ADR-0057 §4.6): the other
        // session of the same user keeps renewing, and nobody is accused of theft. Red on main,
        // where "Esci" revokes every grant of the user (BffAuthController.Logout ->
        // /api/auth/logout) and the other session's next renewal is then read as reuse: a 401 and
        // one false RefreshTokenReuse.
        using var api = new CustomWebApplicationFactory();
        api.CaptureLog(LogEventLevel.Warning);
        var apiClient = api.CreateClient();

        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);

        var (email, _) = await RegisterAsync(apiClient);
        var sessionA = await SignInThroughTheBffAsync(bffHost, browser, email);
        var sessionB = await SignInThroughTheBffAsync(bffHost, browser, email);

        var sessions = bffHost.Services.GetRequiredService<BffSessions>();
        var userId = sessions.GetSession(sessionA)!.UserId;
        var grantA = sessions.GetSession(sessionA)!.RefreshToken;
        var grantB = sessions.GetSession(sessionB)!.RefreshToken;
        grantA.Should().NotBeNullOrEmpty("a sign-in hands the BFF a grant");
        grantB.Should().NotBeNullOrEmpty("a sign-in hands the BFF a grant");
        grantB.Should().NotBe(grantA, "each sign-in mints its own grant");

        // "Esci" on A, as the browser sends it.
        var esci = await browser.SendAsync(WithSession(bffHost, HttpMethod.Post, "/bff/auth/logout", sessionA));
        esci.StatusCode.Should().Be(HttpStatusCode.OK);

        // The false event must be DUE before its absence can count: B renews only once the API holds
        // A's grant as revoked. On main that is at once; with ADR-0057 §4.6 the revoke is queued,
        // so wait.
        (await WaitUntilRevokedAsync(api, grantA!, TimeSpan.FromSeconds(10))).Should().BeTrue(
            "\"Esci\" on A must revoke A's grant at the API, or B's renewal below tests nothing");

        // B renews exactly as the BFF renews it: its grant, the refresh endpoint, the BFF's key.
        var renewal = await apiClient.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = grantB! }, Json);
        var renewalBody = await renewal.Content.ReadAsStringAsync();

        var reuseRows = await CountAuditRowsAsync(api, userId, SecurityEvents.RefreshTokenReuse);
        var reuseLines = api.CapturedEvents.Count(e => IsSecurityEvent(e, SecurityEvents.RefreshTokenReuse));

        using (new AssertionScope())
        {
            renewal.StatusCode.Should().Be(HttpStatusCode.OK,
                $"\"Esci\" on A leaves B's grant alone (body: {renewalBody})");
            reuseRows.Should().Be(0,
                "signing out is not theft: no RefreshTokenReuse audit row may name this user");
            reuseLines.Should().Be(0,
                "and no RefreshTokenReuse security event may be logged");
        }
    }

    [Fact]
    public async Task WhenTheBffKeyIsNotTheApiKey_AProxiedReadReachesTheBrowserAs503()
    {
        // ADR-0057 §10 O0-2 item 6 (F4, ADR-0057 §4.7). A key rotation applied on one side only is
        // an outage of the service, not a verdict on the user: the browser must see a 503 it
        // retries, never the 401 it reads as "signed out". Red on main, where YARP hands the API's
        // 401 SERVICE_CREDENTIAL_REQUIRED straight to the browser.
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();

        using var bffHost = new BffOverApiFactory(api, KeyTheApiDoesNotHold);
        var browser = Browser(bffHost);

        var (_, registered) = await RegisterAsync(apiClient);
        var token = registered.Token;

        // Positive control: the access token is good. With the API's own key it reads the accounts,
        // so the only thing wrong with the proxied read below is the key.
        using (var direct = new HttpRequestMessage(HttpMethod.Get, "/api/accounts"))
        {
            direct.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            (await apiClient.SendAsync(direct)).StatusCode.Should().Be(HttpStatusCode.OK,
                "the token must be accepted when the key is right, or the refusal below proves nothing about the key");
        }

        // A live session holding that token, planted in the BFF's store: a sign-in THROUGH this BFF
        // cannot succeed, since the API refuses its key.
        var sessions = bffHost.Services.GetRequiredService<BffSessions>();
        var sessionId = sessions.CreateSession(
            token.AccessToken, token.ExpiresAt, token.RefreshToken, token.RefreshTokenExpiresAt, registered.User);

        var proxied = await browser.SendAsync(WithSession(bffHost, HttpMethod.Get, "/api/accounts", sessionId));
        var body = await proxied.Content.ReadAsStringAsync();

        proxied.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            $"a refused service key is the service failing, so the browser gets a 503 and stays signed in (body: {body})");
        sessions.GetSession(sessionId).Should().NotBeNull("nothing about the key ends the user's session");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A client that keeps no cookies of its own, so every request carries exactly the session the
    /// test names. With the default cookie handling, the second sign-in would carry the first
    /// session's cookie, and a BFF that ends the old session on a new sign-in (ADR-0057 §4.6, F13)
    /// would end A before the test signs A out.
    /// </summary>
    private static HttpClient Browser(BffOverApiFactory bffHost) =>
        bffHost.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<(string Email, RegisterResponse Registered)> RegisterAsync(HttpClient apiClient)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"e2e{unique}@example.com";
        var response = await apiClient.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"e2e_{unique}",
            Email = email,
            Password = Password,
            FirstName = "End",
            LastName = "ToEnd",
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        return (email, registered);
    }

    /// <summary>Signs in through the BFF, as the SPA does, and returns the session the cookie names.</summary>
    private static async Task<string> SignInThroughTheBffAsync(
        BffOverApiFactory bffHost, HttpClient browser, string email)
    {
        var response = await browser.PostAsJsonAsync("/bff/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"the sign-in through the BFF must work (body: {await response.Content.ReadAsStringAsync()})");

        var prefix = CookieName(bffHost) + "=";
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..].Split(';')[0];
    }

    private static string CookieName(BffOverApiFactory bffHost) =>
        bffHost.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

    private static HttpRequestMessage WithSession(
        BffOverApiFactory bffHost, HttpMethod method, string path, string sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{CookieName(bffHost)}={sessionId}");
        return request;
    }

    /// <summary>Polls the API's database until the row for <paramref name="grant"/> is revoked.</summary>
    private static async Task<bool> WaitUntilRevokedAsync(
        CustomWebApplicationFactory api, string grant, TimeSpan timeout)
    {
        // The grant is stored only as its SHA-256, Base64 (RefreshTokenService.ComputeHash).
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(grant)));
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
                var row = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash);
                if (row?.RevokedAt is not null)
                {
                    return true;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(50);
        }
    }

    private static async Task<int> CountAuditRowsAsync(CustomWebApplicationFactory api, Guid userId, string securityEvent)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == userId && e.Event == securityEvent);
    }

    private static bool IsSecurityEvent(LogEvent logEvent, string securityEvent) =>
        logEvent.Properties.TryGetValue("SecurityEvent", out var value)
        && value is ScalarValue { Value: string name }
        && name == securityEvent;
}
