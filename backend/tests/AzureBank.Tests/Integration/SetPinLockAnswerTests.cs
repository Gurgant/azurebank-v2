using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What a first PIN's password proof answers once the login's lock stands: the answer the PIN setup
/// page counts down from. The page reads <c>retryAfterSeconds</c> from the body and falls back to
/// <c>Retry-After</c>; until this test, the lock's answer was held to its status and its
/// <c>errorCode</c> only (<see cref="AuthEndpointTests"/>), and either field could have gone
/// without a test saying so.
/// </summary>
public class SetPinLockAnswerTests : IntegrationTestBase
{
    private readonly ITestOutputHelper _output;

    public SetPinLockAnswerTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
        : base(factory) => _output = output;

    [Fact]
    public async Task TheLocksAnswer_CarriesTheWaitInTheBody_AndTheSameWaitInRetryAfter()
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var wrong = new SetPinRequest { Pin = "424242", Password = "NotThePassword1!" };

        // Every wrong password up to the limit is a 401, the one that crosses it included: the lock
        // is first said on the attempt after it.
        for (var i = 0; i < ValidationRules.MaxLoginAttempts; i++)
        {
            (await Client.PostAsJsonAsync("/api/auth/pin", wrong, JsonOptions)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, "attempt {0} is a wrong password, not yet the lock", i + 1);
        }

        var correct = new SetPinRequest { Pin = "424242", Password = TestUserPassword };
        var sentAt = DateTimeOffset.UtcNow;
        using var locked = await Client.PostAsJsonAsync("/api/auth/pin", correct, JsonOptions);

        var text = await locked.Content.ReadAsStringAsync();
        _output.WriteLine($"status {(int)locked.StatusCode}");
        _output.WriteLine($"Retry-After {locked.Headers.RetryAfter}");
        _output.WriteLine(text);

        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        var seconds = root.GetProperty("retryAfterSeconds").GetInt32();
        var lockedUntil = DateTimeOffset.Parse(root.GetProperty("lockedUntil").GetString()!);
        using (new AssertionScope())
        {
            root.GetProperty("status").GetInt32().Should().Be(429);
            root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.AccountLocked);
            root.GetProperty("detail").GetString().Should().Be(
                "Too many failed login attempts. Your account is temporarily locked; try again later.");
            seconds.Should().BeInRange(1, ValidationRules.LoginLockoutMinutes * 60,
                "a lock just taken has its whole window, or nearly, still to run");
            (locked.Headers.RetryAfter?.Delta).Should().Be(TimeSpan.FromSeconds(seconds),
                "the header says the same wait as the body");
            (lockedUntil - sentAt).TotalSeconds.Should().BeApproximately(seconds, 2,
                "retryAfterSeconds is the time left until lockedUntil");
        }
    }
}
