using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Shared.Constants;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The lock's refusal of a first PIN, as the BFF hands it to the browser: the API's 429 with its
/// body as it was written and its <c>Retry-After</c>, which the PIN setup page counts down from.
/// No other test sent set-pin a refusal with a wait in it.
/// </summary>
public class SetPinLockForwardingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int RetryAfterSeconds = 900;

    /// <summary>
    /// The API's answer, as <c>SetPinLockAnswerTests</c> in AzureBank.Tests measured it on the API's
    /// own host, with its trace and its lock's end fixed.
    /// </summary>
    private const string ApiLockAnswer =
        """{"type":"https://httpstatuses.com/429","title":"Too Many Requests","status":429,"detail":"Too many failed login attempts. Your account is temporarily locked; try again later.","instance":"/api/auth/pin","errorCode":"ACCOUNT_LOCKED","traceId":"19db54a5fdc94d33a9760e07c0f9fb83","retryAfterSeconds":900,"lockedUntil":"2026-10-25T17:41:09.7222673+00:00"}""";

    private const string LoginSuccessJson = """
        {
          "data": {
            "token": {
              "accessToken": "jwt-1",
              "refreshToken": "fake-refresh",
              "expiresIn": 899,
              "tokenType": "Bearer",
              "expiresAt": "2030-01-01T00:00:00Z"
            },
            "user": {
              "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "azureTag": "lockeduser",
              "email": "lockeduser@azurebank.dev",
              "firstName": "Locked",
              "lastName": "User",
              "hasPin": false
            }
          },
          "message": "Login successful"
        }
        """;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public SetPinLockForwardingTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task TheApisLock_IsForwardedWithItsBodyAsWritten_AndItsRetryAfter()
    {
        var pinCalls = 0;
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(request =>
                    {
                        var path = request.RequestUri!.AbsolutePath;
                        if (path == "/api/auth/login")
                        {
                            return Json(HttpStatusCode.OK, LoginSuccessJson);
                        }
                        if (path == "/api/auth/pin")
                        {
                            pinCalls++;
                            var locked = Json(HttpStatusCode.TooManyRequests, ApiLockAnswer, "application/problem+json");
                            locked.Headers.Add("Retry-After", RetryAfterSeconds.ToString());
                            return locked;
                        }
                        return Json(HttpStatusCode.OK, """{"message":"ok"}""");
                    }))));

        var client = host.CreateClient();
        var signIn = await client.PostAsJsonAsync(
            "/bff/auth/login", new { email = "lockeduser@azurebank.dev", password = "Password123!" });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        // CreateClient() keeps no cookies, so the session's is carried by hand.
        client.DefaultRequestHeaders.Add("Cookie", signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0]);

        using var response = await client.PostAsJsonAsync(
            "/bff/auth/set-pin", new { pin = "424242", password = "Password123!" });

        var text = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"status {(int)response.StatusCode}");
        _output.WriteLine($"Retry-After {response.Headers.RetryAfter}");
        _output.WriteLine(text);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
        using var forwarded = JsonDocument.Parse(text);
        using var sent = JsonDocument.Parse(ApiLockAnswer);
        using (new AssertionScope())
        {
            JsonElement.DeepEquals(forwarded.RootElement, sent.RootElement).Should().BeTrue(
                "the API's body reaches the browser as the API wrote it, retryAfterSeconds and lockedUntil included");
            forwarded.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.AccountLocked);
            forwarded.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(RetryAfterSeconds);
            (response.Headers.RetryAfter?.Delta).Should().Be(
                TimeSpan.FromSeconds(RetryAfterSeconds), "the API's Retry-After comes with its refusal");
            pinCalls.Should().Be(1, "the refusal is the API's: the BFF asked it once");
        }
    }

    [Fact]
    public async Task AnOverLongPassword_IsRefusedHere_AndNeverReachesTheApi()
    {
        var pinCalls = 0;
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(request =>
                    {
                        var path = request.RequestUri!.AbsolutePath;
                        if (path == "/api/auth/login")
                        {
                            return Json(HttpStatusCode.OK, LoginSuccessJson);
                        }
                        if (path == "/api/auth/pin")
                        {
                            pinCalls++;
                            return Json(HttpStatusCode.Unauthorized,
                                """{"errorCode":"INVALID_CREDENTIALS"}""", "application/problem+json");
                        }
                        return Json(HttpStatusCode.OK, """{"message":"ok"}""");
                    }))));

        var client = host.CreateClient();
        var signIn = await client.PostAsJsonAsync(
            "/bff/auth/login", new { email = "lockeduser@azurebank.dev", password = "Password123!" });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        // CreateClient() keeps no cookies, so the session's is carried by hand.
        client.DefaultRequestHeaders.Add("Cookie", signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0]);

        using var response = await client.PostAsJsonAsync(
            "/bff/auth/set-pin", new { pin = "424242", password = new string('a', ValidationRules.PasswordMaxLength + 1) });

        var text = await response.Content.ReadAsStringAsync();
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
            pinCalls.Should().Be(0, "the BFF refuses it before it asks the API");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}
