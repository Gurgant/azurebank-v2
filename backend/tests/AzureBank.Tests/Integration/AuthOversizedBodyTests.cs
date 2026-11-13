using System.Net;
using System.Text;
using System.Text.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Exceptions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Login, register, set PIN and verify PIN refuse a body over their 32 KB limit with the 413 the
/// mints answer (<see cref="MintOversizedBodyDrainTests"/>), before the action runs. The in-memory
/// host has no body size limit of its own: without the endpoint's limit, each of these bodies is
/// read whole and answered as a small one is.
/// </summary>
public sealed class AuthOversizedBodyTests(CustomWebApplicationFactory factory) : IntegrationTestBase(factory)
{
    [Theory]
    [InlineData("/api/auth/login", 40_000)]
    [InlineData("/api/auth/login", 2_000_000)]
    [InlineData("/api/auth/register", 40_000)]
    [InlineData("/api/auth/register", 2_000_000)]
    [InlineData("/api/auth/pin", 40_000)]
    [InlineData("/api/auth/pin", 2_000_000)]
    [InlineData("/api/auth/pin/verify", 40_000)]
    [InlineData("/api/auth/pin/verify", 2_000_000)]
    public async Task OversizedBody_IsRefusedWith413BeforeTheActionRuns(string path, int size)
    {
        using var request = await PaddedRequestAsync(path, size);
        using var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.PayloadTooLarge);
        problem.RootElement.GetProperty("detail").GetString().Should().Be(PayloadTooLargeException.Detail);
        if (size <= 1_048_576)
        {
            response.Headers.ConnectionClose.Should().NotBe(true, "the whole body was drained");
        }
        else
        {
            response.Headers.ConnectionClose.Should().BeTrue("above the drain's cap the body is left unread");
        }
    }

    // Control: the limit is inclusive. A body of exactly 32,768 bytes reaches the action and gets
    // its usual answer: an unknown email's 401, a registration, an enrolment, a PIN that matches.
    [Theory]
    [InlineData("/api/auth/login", HttpStatusCode.Unauthorized)]
    [InlineData("/api/auth/register", HttpStatusCode.Created)]
    [InlineData("/api/auth/pin", HttpStatusCode.OK)]
    [InlineData("/api/auth/pin/verify", HttpStatusCode.OK)]
    public async Task BodyOfExactlyTheLimit_IsAnsweredByTheAction(string path, HttpStatusCode answer)
    {
        using var request = await PaddedRequestAsync(path, 32_768);
        request.Content!.Headers.ContentLength.Should().Be(32_768, "the padded body is the limit to the byte");

        using var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(answer);
    }

    // The 413 comes before the action, not after it: a PIN that does not match, in a body over the
    // limit, spends no attempt towards the PIN lock. Control: the same wrong PIN in a body
    // within the limit spends one.
    [Fact]
    public async Task OversizedVerifyPinRequest_SpendsNoPinAttempt()
    {
        var (token, userId, _) = await RegisterTestUserAsync();
        await SetPinAsync(token);
        var wrong = JsonSerializer.Serialize(new VerifyPinRequest { Pin = "654321" }, JsonOptions);

        using var oversized = PaddedRequest("/api/auth/pin/verify", wrong, 40_000);
        using var refused = await Client.SendAsync(oversized);

        using var scope = new AssertionScope();
        refused.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await PinAccessFailedCountOfAsync(userId)).Should().Be(0,
            "the action did not run, so the wrong PIN was never compared");

        using var normal = PaddedRequest("/api/auth/pin/verify", wrong, wrong.Length);
        using var answered = await Client.SendAsync(normal);
        answered.StatusCode.Should().Be(HttpStatusCode.OK, "CONTROL: a small body is compared, and answered 200");
        using var answer = JsonDocument.Parse(await answered.Content.ReadAsStringAsync());
        answer.RootElement.GetProperty("data").GetProperty("verified").GetBoolean().Should().BeFalse(
            "CONTROL: the wrong PIN is called wrong");
        (await PinAccessFailedCountOfAsync(userId)).Should().Be(1, "CONTROL: and a wrong PIN there spends one");
    }

    // The registration in a body over the limit creates no account: the same email, in a body within
    // the limit, is then registered as a new one. Had the action run, this answers 409.
    [Fact]
    public async Task OversizedRegisterRequest_CreatesNoAccount()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var json = JsonSerializer.Serialize(
            new RegisterRequest
            {
                AzureTag = $"padded_{unique}",
                Email = $"padded{unique}@example.com",
                Password = TestUserPassword,
                FirstName = "Test",
                LastName = "User",
            },
            JsonOptions);

        using var oversized = PaddedRequest("/api/auth/register", json, 40_000);
        using var refused = await Client.SendAsync(oversized);
        using var normal = PaddedRequest("/api/auth/register", json, json.Length);
        using var registered = await Client.SendAsync(normal);

        using var scope = new AssertionScope();
        refused.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        registered.StatusCode.Should().Be(
            HttpStatusCode.Created, "no account was made by the refused request, so the email is free");
    }

    private async Task<int> PinAccessFailedCountOfAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Users.FindAsync(userId))!.PinAccessFailedCount;
    }

    private static HttpRequestMessage PaddedRequest(string path, string json, int size) =>
        new(HttpMethod.Post, path)
        {
            Content = new StringContent(json.PadRight(size), Encoding.UTF8, "application/json"),
        };

    // A valid body for the route, followed by spaces up to the size asked for: still valid JSON,
    // and ASCII, so the length in characters is the length in bytes. The PIN routes are sent with
    // a fresh user's bearer, without which their 401 would come first; verify PIN with a PIN set.
    private async Task<HttpRequestMessage> PaddedRequestAsync(string path, int size)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        string json;
        switch (path)
        {
            case "/api/auth/login":
                json = JsonSerializer.Serialize(
                    new LoginRequest { Email = $"nobody{unique}@example.com", Password = TestUserPassword },
                    JsonOptions);
                break;
            case "/api/auth/register":
                json = JsonSerializer.Serialize(
                    new RegisterRequest
                    {
                        AzureTag = $"padded_{unique}",
                        Email = $"padded{unique}@example.com",
                        Password = TestUserPassword,
                        FirstName = "Test",
                        LastName = "User",
                    },
                    JsonOptions);
                break;
            case "/api/auth/pin":
                var (enrolling, _, _) = await RegisterTestUserAsync();
                SetAuthHeader(enrolling);
                json = JsonSerializer.Serialize(
                    new SetPinRequest { Pin = "123456", Password = TestUserPassword }, JsonOptions);
                break;
            case "/api/auth/pin/verify":
                var (verifying, _, _) = await RegisterTestUserAsync();
                await SetPinAsync(verifying);
                json = JsonSerializer.Serialize(new VerifyPinRequest { Pin = "123456" }, JsonOptions);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "No body is written for this route.");
        }

        return PaddedRequest(path, json, size);
    }
}
