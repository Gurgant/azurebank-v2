using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The three calls the BFF makes to the API on its own, outside the proxy (<c>verify-pin</c>,
/// <c>set-pin</c> and the rename), when the browser has no session or the session's grant is dead:
/// 401, and the API is not sent the PIN or the new handle.
/// </summary>
/// <remarks>
/// <para>
/// These paths bypass the proxy's transform, so each fetches the token itself and each has its own
/// copy of the two refusals. <c>TokenRefreshTests</c> holds the dead grant on a proxied call and
/// the 503 on <c>verify-pin</c>; the 401 of a dead grant on these three had no test, and neither
/// had <c>verify-pin</c> and <c>set-pin</c> with no session at all.
/// </para>
/// <para>
/// The API is faked at the "BackendApi" client and every path it is asked is recorded, so "the
/// API was not sent the PIN" is read from the record. The record is not empty in the dead-grant
/// case: the renewal that was refused is in it, once, and after it the revoke of the grant, which
/// ending the session queues.
/// </para>
/// </remarks>
public sealed class OwnPathsWithoutALiveSessionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public OwnPathsWithoutALiveSessionTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var derived in _derived)
        {
            derived.Dispose();
        }
    }

    /// <summary>The API, refusing every renewal as it refuses a grant that is unknown, revoked or expired.</summary>
    private sealed class ApiWhoseGrantIsDead
    {
        public ConcurrentQueue<string> Paths { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            return path == "/api/auth/refresh"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        $$"""
                        {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,"instance":"{{path}}","errorCode":"{{ErrorCodes.RefreshTokenInvalid}}"}
                        """,
                        Encoding.UTF8,
                        "application/problem+json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":{"verified":true,"azureTag":"renamed_handle"},"message":"ok"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
        }
    }

    private (WebApplicationFactory<Program> Host, ApiWhoseGrantIsDead Api) NewHost()
    {
        var api = new ApiWhoseGrantIsDead();
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(api.Respond))));
        _derived.Add(host);
        return (host, api);
    }

    private static HttpRequestMessage Call(string path)
    {
        var (method, body) = path switch
        {
            "/bff/auth/verify-pin" => (HttpMethod.Post, (object)new { pin = "824613" }),
            "/bff/auth/set-pin" => (HttpMethod.Post, new { pin = "824613" }),
            "/bff/auth/azuretag" => (HttpMethod.Patch, new { azureTag = "renamed_handle" }),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "not a path of this test"),
        };
        return new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
    }

    private static string ApiPathOf(string path) => path switch
    {
        "/bff/auth/verify-pin" => "/api/auth/pin/verify",
        "/bff/auth/set-pin" => "/api/auth/pin",
        "/bff/auth/azuretag" => "/api/users/me/azuretag",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "not a path of this test"),
    };

    [Theory]
    [InlineData("/bff/auth/verify-pin")]
    [InlineData("/bff/auth/set-pin")]
    public async Task WithNoSession_ThePinPathsAnswer401_AndTheApiIsNotCalled(string path)
    {
        var (host, api) = NewHost();

        var response = await host.CreateClient().SendAsync(Call(path));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("detail").GetString().Should().Be("Session expired or invalid");
        api.Paths.Should().BeEmpty("an anonymous caller's PIN does not reach the API");
    }

    [Theory]
    [InlineData("/bff/auth/verify-pin")]
    [InlineData("/bff/auth/set-pin")]
    [InlineData("/bff/auth/azuretag")]
    public async Task WithADeadGrant_TheBffsOwnCallsAnswer401_EndTheSession_AndSendTheApiNothingElse(string path)
    {
        // The held token still has half a minute of a quarter of an hour: alive, and so close to
        // its end that the call waits for a renewal first. The API answers that renewal with the
        // one refusal that says the grant is dead (ADR-0057 §4.5). The 401 is the SPA's sign-out;
        // the 503 of a renewal that merely failed is another test's.
        //
        // WHY A LIVE TOKEN. With an expired one this test stayed green without the statement that
        // ends the session: the refusal stops the renewals, a session that cannot renew and holds
        // an expired token answers 401 by itself, and the test's own read of the session expired
        // it. A token with life left is sent on unless the session was ended, so here only the
        // ending gives the 401, keeps the PIN from the API and empties the store. Measured
        // 2026-10-12 without that statement: 200 on all three paths.
        var (host, api) = NewHost();
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var expiresAt = DateTime.UtcNow.AddSeconds(30);
        var sessionId = sessions.CreateSession(
            Jwt(issuedAt: expiresAt.AddSeconds(-900), expiresAt),
            expiresAt,
            "rt-0",
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "deadgrant",
                Email = "deadgrant@example.com",
                FirstName = "Dead",
                LastName = "Grant",
                HasPin = true,
            });

        var request = Call(path);
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        var response = await host.CreateClient().SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var held = sessions.GetSession(sessionId);

        var again = Call(path);
        again.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        var next = await host.CreateClient().SendAsync(again);

        // Ending a session queues the revoke of its grant, sent to the API in the background: it
        // is waited for, so that the record is read whole.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!api.Paths.Contains("/api/auth/revoke") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        using var all = new AssertionScope();
        DateTime.UtcNow.Should().BeBefore(
            expiresAt.AddSeconds(-10), "ARRANGE: the token was alive throughout, so no expiry ended anything");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("detail").GetString().Should().Be("Session expired or invalid");
        held.Should().BeNull("a dead grant ends the session, whatever is left of its token");
        next.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the cookie of an ended session opens nothing");
        api.Paths.Should().Equal(
            ["/api/auth/refresh", "/api/auth/revoke"],
            "the renewal was asked for once and refused, and the ended session's grant was revoked; "
            + $"neither call sent the API a PIN or a handle, so {ApiPathOf(path)} is not in the record");
    }

    /// <summary>
    /// A JWT-shaped token that carries its own issue time and expiry, so the BFF reads how much of
    /// its lifetime is left. Unsigned: the BFF reads a token, it never verifies one.
    /// </summary>
    private static string Jwt(DateTime issuedAt, DateTime expiresAt)
    {
        static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        var iat = new DateTimeOffset(issuedAt).ToUnixTimeSeconds();
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        return Part("""{"alg":"none","typ":"JWT"}""")
            + "." + Part($$"""{"sub":"held","iat":{{iat}},"exp":{{exp}}}""")
            + ".test-signature";
    }
}
