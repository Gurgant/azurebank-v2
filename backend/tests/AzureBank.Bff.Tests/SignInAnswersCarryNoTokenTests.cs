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
/// The three doors that open a session from the API's own sign-in answer (sign-in, registration
/// and re-authentication) answer the browser with the user and the access token's expiry, and with
/// no token. The API's answer carries the access token, the grant and the session stamp; the BFF
/// keeps the three in its store, and only the type it answers with, <c>BffLoginResponse</c>, keeps
/// them from the browser.
/// </summary>
/// <remarks>
/// <para>
/// The scripted API answers its sign-in and its registration with an access token and a grant that
/// appear nowhere else, and with a user none of whose fields holds "oken". So each answer is read
/// as text, as <c>DemoClaimTests</c> reads the answer of the fourth door, the demo claim: neither
/// value under any name, no member whose name holds "oken", and no <c>sessionStamp</c>. Its headers
/// are read for the two values too, since the session cookie is the one part of the session an
/// answer carries.
/// </para>
/// <para>
/// Each door is also seen to put the two values in the session it opens, so a door that read
/// nothing from the API could not pass for one that kept what it read.
/// </para>
/// </remarks>
public sealed class SignInAnswersCarryNoTokenTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string LoginPath = "/bff/auth/login";
    private const string RegisterPath = "/bff/auth/register";
    private const string ReauthenticatePath = "/bff/auth/reauthenticate";
    private const string ApiLoginPath = "/api/auth/login";
    private const string ApiRegisterPath = "/api/auth/register";

    /// <summary>The access token of every sign-in the API answers.</summary>
    private const string AccessToken = "jwt-kept-by-the-bff";

    /// <summary>The grant of every sign-in the API answers.</summary>
    private const string Grant = "rt-kept-by-the-bff";

    private const string UserId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Email = "quiet.saver@example.com";
    private const string Password = "Password1!";

    /// <summary>The account a registration opens, which the API's answer to it carries.</summary>
    private const string AccountJson = """
        {
          "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
          "accountNumber": "TEST-0000-0001",
          "name": "Conto principale",
          "type": "Checking",
          "balance": 0,
          "isPrimary": true,
          "createdAt": "2026-09-28T00:00:00Z"
        }
        """;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    public SignInAnswersCarryNoTokenTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The scripted API: it keeps the path of every call it is sent, and answers its sign-in and
    /// its registration with the access token, the grant and a session stamp. A queue, because the
    /// grant revoker calls from its own thread.
    /// </summary>
    private sealed class Upstream
    {
        public ConcurrentQueue<string> Paths { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            return path switch
            {
                ApiLoginPath => Json(HttpStatusCode.OK, SignedInJson(account: null)),
                ApiRegisterPath => Json(HttpStatusCode.Created, SignedInJson(account: AccountJson)),
                _ => Json(HttpStatusCode.OK, """{"data":null,"message":"ok"}"""),
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>What the API answers a sign-in, and a registration with <paramref name="account"/>.</summary>
    private static string SignedInJson(string? account)
    {
        var accountMember = account is null ? "" : $"\"account\": {account},";
        return $$"""
            {
              "data": {
                {{accountMember}}
                "token": {
                  "accessToken": "{{AccessToken}}",
                  "refreshToken": "{{Grant}}",
                  "refreshTokenExpiresAt": "{{DateTime.UtcNow.AddMinutes(60):O}}",
                  "sessionStamp": 7,
                  "expiresIn": 899,
                  "tokenType": "Bearer",
                  "expiresAt": "{{DateTime.UtcNow.AddMinutes(15):O}}"
                },
                "user": {
                  "id": "{{UserId}}",
                  "azureTag": "quiet_saver",
                  "email": "{{Email}}",
                  "firstName": "Quiet",
                  "lastName": "Saver",
                  "hasPin": false
                }
              },
              "message": "Signed in"
            }
            """;
    }

    private (WebApplicationFactory<Program> Host, Upstream Upstream) NewHost()
    {
        var upstream = new Upstream();
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(upstream.Respond))));
        _hosts.Add(host);
        return (host, upstream);
    }

    private static string CookieName(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

    /// <summary>
    /// A live session of the user the API signs in, made in the store directly: re-authentication
    /// answers only from one. Returns its id.
    /// </summary>
    private static string LiveSession(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<ISessionService>().CreateSession(
            "jwt-of-the-session-before",
            DateTime.UtcNow.AddMinutes(15),
            "rt-of-the-session-before",
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.Parse(UserId),
                AzureTag = "quiet_saver",
                Email = Email,
                FirstName = "Quiet",
                LastName = "Saver",
            });

    /// <summary>The session the answer's cookie names.</summary>
    private static string SessionIdFrom(WebApplicationFactory<Program> host, HttpResponseMessage response)
    {
        var prefix = CookieName(host) + "=";
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue("the answer sets the session cookie");
        var cookie = cookies!.Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..].Split(';')[0];
    }

    [Theory]
    // Re-authentication signs in again at the API's login, as the user of a live session.
    [InlineData(LoginPath, HttpStatusCode.OK, ApiLoginPath)]
    [InlineData(RegisterPath, HttpStatusCode.Created, ApiRegisterPath)]
    [InlineData(ReauthenticatePath, HttpStatusCode.OK, ApiLoginPath)]
    public async Task TheAnswer_HasTheUser_AndNoTokenByNameOrByValue(
        string door, HttpStatusCode status, string apiPath)
    {
        var (host, upstream) = NewHost();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, door)
        {
            Content = door switch
            {
                RegisterPath => JsonContent.Create(new
                {
                    azureTag = "quiet_saver",
                    email = Email,
                    password = Password,
                    firstName = "Quiet",
                    lastName = "Saver",
                }),
                ReauthenticatePath => JsonContent.Create(new { password = Password }),
                _ => JsonContent.Create(new { email = Email, password = Password }),
            },
        };
        if (door == ReauthenticatePath)
        {
            request.Headers.Add("Cookie", $"{CookieName(host)}={LiveSession(host)}");
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(status, text);
        upstream.Paths.Should().Contain(
            apiPath, "ARRANGE: the door signed in at the API, whose answer carried both tokens and the stamp");
        var session = host.Services.GetRequiredService<ISessionService>().GetSession(SessionIdFrom(host, response));
        session.Should().NotBeNull("ARRANGE: the door opened a session");
        var headerValues = response.Headers.Concat(response.Content.Headers)
            .SelectMany(header => header.Value)
            .ToList();

        using (new AssertionScope())
        {
            // CONTROL: both values came as far as the BFF, which keeps them, and the answer is the
            // success, naming the user.
            session!.AccessToken.Should().Be(AccessToken);
            session!.RefreshToken.Should().Be(Grant);
            text.Should().Contain(UserId);

            text.Should().NotContain(AccessToken, "the access token stays in the BFF's store")
                .And.NotContain(Grant, "the grant stays there too")
                .And.NotContain("oken", "no member of the answer is a token's")
                .And.NotContain("sessionStamp", "the stamp is compared in the BFF, never handed to the browser");
            headerValues.Should().NotContain(
                value => value.Contains(AccessToken, StringComparison.Ordinal)
                    || value.Contains(Grant, StringComparison.Ordinal),
                "the cookie names the session, and no header carries what the session holds");
        }
    }
}
