using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Tests;

/// <summary>
/// A renewal the API answers with something that is not its contract: a 200 whose body is not
/// JSON or carries no token; and a call that fails in a way nobody listed. Each is
/// the service failing and not the session: 503 with the <c>Retry-After</c> of a failed renewal's
/// cooldown, the session kept, and no token sent on, least of all an empty one.
/// </summary>
/// <remarks>
/// <para>
/// <c>TokenRefreshTests</c> holds the statuses (a 401 that names the grant, a 401 that does not, a
/// 5xx, a timeout). A SUCCESS that is wrong had no test: read as a renewal it would store an empty
/// access token, and read as a refusal it would sign the user out for a fault of the API's.
/// </para>
/// <para>
/// Observed through <c>verify-pin</c>, a call the BFF makes itself with the session's token: the
/// fake API records the paths it is asked and the Authorization each carried, so "nothing was sent
/// on" is read from the record, beside the renewal that is in it.
/// </para>
/// </remarks>
public sealed class RenewalOutsideTheContractTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public RenewalOutsideTheContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var derived in _derived)
        {
            derived.Dispose();
        }
    }

    private sealed class RecordingApi(Func<HttpResponseMessage> onRefresh)
    {
        public ConcurrentQueue<(string Path, string? Authorization)> Calls { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Enqueue((path, request.Headers.Authorization?.ToString()));
            return path == "/api/auth/refresh"
                ? onRefresh()
                : Json("""{"data":{"verified":true},"message":"ok"}""");
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private (WebApplicationFactory<Program> Host, RecordingApi Api) NewHost(Func<HttpResponseMessage> onRefresh)
    {
        var api = new RecordingApi(onRefresh);
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(api.Respond))));
        _derived.Add(host);
        return (host, api);
    }

    private static (string SessionId, HttpRequestMessage VerifyPin) ASessionAndItsCall(
        WebApplicationFactory<Program> host, DateTime tokenExpiry, string? grant, string accessToken = "jwt-0")
    {
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(
            accessToken,
            tokenExpiry,
            grant,
            grant is null ? null : DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "renewal",
                Email = "renewal@example.com",
                FirstName = "Re",
                LastName = "Newal",
                HasPin = true,
            });

        var request = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/verify-pin")
        {
            Content = JsonContent.Create(new { pin = "824613" }),
        };
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        return (sessionId, request);
    }

    [Theory]
    [InlineData("a 200 that is not JSON")]
    [InlineData("a 200 with no data")]
    [InlineData("a 200 with an empty access token")]
    [InlineData("a failure nobody listed")]
    public async Task ARenewalOutsideTheContract_Is503_KeepsTheSession_AndSendsNoTokenOn(string answer)
    {
        Func<HttpResponseMessage> onRefresh = answer switch
        {
            "a 200 that is not JSON" => () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>a proxy's page</html>", Encoding.UTF8, "text/html"),
            },
            "a 200 with no data" => () => Json("""{"success":true,"data":null,"message":"Token refreshed"}"""),
            "a 200 with an empty access token" => () => Json(
                """{"success":true,"data":{"accessToken":"","refreshToken":"rt-1","expiresAt":"2030-01-01T00:00:00Z"}}"""),
            "a failure nobody listed" => () => throw new InvalidOperationException("not a network failure and not a timeout"),
            _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "not a case of this test"),
        };
        var (host, api) = NewHost(onRefresh);
        var (sessionId, verifyPin) = ASessionAndItsCall(host, tokenExpiry: DateTime.UtcNow.AddSeconds(-1), grant: "rt-0");

        var response = await host.CreateClient().SendAsync(verifyPin);
        var held = host.Services.GetRequiredService<ISessionService>().GetSession(sessionId);

        using var all = new AssertionScope();
        api.Calls.Select(call => call.Path).Should().Equal(
            ["/api/auth/refresh"], "the renewal was asked for once, and the PIN was sent nowhere");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the service is failing, not the session");
        response.Headers.RetryAfter.Should().NotBeNull("the SPA retries a 503 and reads a 401 as signed out");

        // WHICH 503. A renewal still running answers "come back in 5 s", the length of the wait; a
        // renewal that FAILED answers with what is left of its 15 s cooldown, during which no
        // other starts. The presence of the header does not tell them apart, and a failure that
        // left the renewal unrecorded would answer the first. Measured 2026-10-12 with the catch
        // narrowed to network failures: "a failure nobody listed" is still a 503, with 5.
        response.Headers.RetryAfter?.Delta.Should().BeGreaterThan(
            TimeSpan.FromSeconds(5), "the failure was recorded on the session, and its cooldown is what the caller is told");
        held.Should().NotBeNull("a fault of the API's signs nobody out");
        held!.AccessToken.Should().Be("jwt-0", "nothing the answer carried was stored as a token");
    }

    [Fact]
    public async Task ASessionThatCannotRenew_UsesItsTokenUntilItExpires_AndAsksForNoRenewal()
    {
        // No grant: the token is all the session has. With 5 s or less left a session that can
        // renew waits for a new one; this one cannot, and is not refused while its token is good.
        // The token is JWT-shaped with its own issue time, a quarter of an hour of life and four
        // seconds of it left: an opaque one would be dated from the moment the BFF got it, and
        // four seconds out of four is "more than half left", which never comes this far.
        var (host, api) = NewHost(() => throw new InvalidOperationException("no renewal is due"));
        var expiresAt = DateTime.UtcNow.AddSeconds(4);
        var token = Jwt(issuedAt: expiresAt.AddSeconds(-900), expiresAt);
        var (_, verifyPin) = ASessionAndItsCall(host, expiresAt, grant: null, token);

        var response = await host.CreateClient().SendAsync(verifyPin);

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.Calls.Should().Equal(
            [("/api/auth/pin/verify", "Bearer " + token)], "the held token is sent, and nothing asks for another");
    }

    /// <summary>
    /// A JWT-shaped token that carries its own issue time and expiry. Unsigned: the BFF reads a
    /// token, it never verifies one.
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
