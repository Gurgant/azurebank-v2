using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Services.Interfaces;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves the PIN failure counter is ATOMIC on real SQL Server: exactly
/// MaxPinAttempts wrong PINs fired in PARALLEL must still lock the account. A
/// read-modify-write counter would lose updates under this contention and leave
/// the account unlocked — letting an attacker exceed the threshold. The set-based
/// ExecuteUpdate increment prevents that. InMemory can't exhibit the race (it has
/// no real concurrency), so this proof is SQL-gated.
///
/// It also proves the bound on what is COMPARED (ADR-0010, decision 5): a locked account at the
/// end says nothing about how many guesses were read on the way to it.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PinLockoutConcurrencySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;
    private WebApplicationFactory<Program>? _countingHost;

    public PinLockoutConcurrencySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task ExactlyThresholdParallelWrongPins_LockTheAccount()
    {
        const string pin = "123456";
        var client = CreateSqlClient();
        var (token, _) = await RegisterAsync(client);
        await SetPinAsync(client, token, pin);

        // Fire EXACTLY MaxPinAttempts wrong PINs in PARALLEL. With an atomic
        // counter every increment lands, so the threshold is reached and the PIN
        // locks; a lost-update read-modify-write could leave the count below it.
        var statuses = await Task.WhenAll(
            Enumerable.Range(0, ValidationRules.MaxPinAttempts)
                .Select(_ => VerifyPinAsync(client, token, "000000")));
        _output.WriteLine("parallel wrong-PIN statuses: " + string.Join(",", statuses.Select(s => (int)s)));

        // The PIN is now locked: even the CORRECT PIN is refused with 429.
        var afterLock = await VerifyPinAsync(client, token, pin);
        afterLock.Should().Be(HttpStatusCode.TooManyRequests,
            "exactly MaxPinAttempts parallel wrong PINs must lock the account (atomic counter, no lost updates)");
    }

    [SqlServerFact]
    public async Task BurstOverThreshold_StaysLocked_NoRaceBypass()
    {
        const string pin = "123456";
        var client = CreateSqlClient();

        // A fresh user each round exercises the unlocked -> burst -> locked path.
        // Firing 3x the threshold in parallel would, before the single-statement
        // atomic fix, let a late increment clear a just-applied lock and leave the
        // account UNLOCKED. It must stay locked every round.
        for (var round = 0; round < 3; round++)
        {
            var (token, tag) = await RegisterAsync(client);
            await SetPinAsync(client, token, pin);

            await Task.WhenAll(Enumerable.Range(0, ValidationRules.MaxPinAttempts * 3)
                .Select(_ => VerifyPinAsync(client, token, "000000")));

            (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.TooManyRequests,
                $"round {round}: a burst of parallel wrong PINs must leave the account LOCKED, never bypassed");

            // The atomic WHERE-guard leaves NO residual count on a just-locked account,
            // so the next window (after expiry) gets a full budget (ADR-0010, symmetric
            // with the login lockout proof).
            (await ReadPinAccessFailedCountAsync(tag)).Should().Be(0,
                $"round {round}: a late concurrent increment must not survive onto a locked row");
        }
    }

    [SqlServerFact]
    public async Task CorrectPinAfterFailures_ResetsWindow_OnSqlServer()
    {
        const string pin = "123456";
        var client = CreateSqlClient();
        var (token, _) = await RegisterAsync(client);
        await SetPinAsync(client, token, pin);

        // (Max-1) wrong PINs stay under the threshold; a correct PIN then resets the
        // counter (proving the relational ResetLockoutAsync ExecuteUpdate persists),
        // so a subsequent wrong PIN is only attempt #1 and does NOT lock (429).
        for (var i = 0; i < ValidationRules.MaxPinAttempts - 1; i++)
        {
            (await VerifyPinAsync(client, token, "000000")).Should().Be(HttpStatusCode.OK);
        }
        (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.OK, "correct PIN resets the window");
        (await VerifyPinAsync(client, token, "000000")).Should().Be(HttpStatusCode.OK,
            "after a reset the next wrong PIN is attempt #1, not a lockout");
    }

    /// <summary>
    /// A right PIN leaves nothing of its own claim on the row, from a clean row too. Only SQL
    /// Server can show it: there the claim is a set-based UPDATE the service's own read of the
    /// user never sees, so a reset decided on that read would leave the claim counted.
    /// </summary>
    [SqlServerFact]
    public async Task RightPinOnACleanRow_LeavesNoAttemptCounted_OnSqlServer()
    {
        const string pin = "123456";
        var client = CreateSqlClient();
        var (token, tag) = await RegisterAsync(client);
        await SetPinAsync(client, token, pin);

        (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.OK);

        (await ReadPinAccessFailedCountAsync(tag)).Should().Be(0,
            "a right PIN takes back the attempt it claimed before it was compared");
        for (var i = 0; i < ValidationRules.MaxPinAttempts - 1; i++)
        {
            (await VerifyPinAsync(client, token, "000000")).Should().Be(HttpStatusCode.OK,
                $"wrong PIN {i + 1} after a right one is under the threshold");
        }
    }

    /// <summary>
    /// Right PINs sent together: the ones beyond the threshold may be refused by the lock that the
    /// claim at the threshold holds while it is compared (ADR-0010, Consequences), but that lock
    /// does not outlive them. None is called wrong, and the row ends clean.
    /// </summary>
    [SqlServerFact]
    public async Task RightPinsSentTogether_LeaveNoLockAndNoCountBehind()
    {
        const string pin = "123456";
        var client = CreateSqlClient();

        for (var round = 0; round < BurstRounds; round++)
        {
            var (token, tag) = await RegisterAsync(client);
            await SetPinAsync(client, token, pin);

            var answers = await SendTogetherAsync(client, token,
                [.. Enumerable.Repeat(pin, 2 * ValidationRules.MaxPinAttempts)]);
            _output.WriteLine($"round {round}: answers {Tally(answers)}");

            answers.Should().OnlyContain(a => a == Answer.Verified || a == Answer.Locked,
                $"round {round}: a right PIN is accepted or refused by the lock, never called wrong");
            answers.Should().Contain(Answer.Verified, $"round {round}: the first claim is always compared");
            (await ReadPinAccessFailedCountAsync(tag)).Should().Be(0,
                $"round {round}: every right PIN that was compared took its claim back");
            (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.OK,
                $"round {round}: no lock is left on the row once the right PINs have ended");
        }
    }

    /// <summary>
    /// The bound itself (ADR-0010): however many PINs arrive at once, the stored PIN is compared
    /// with at most <see cref="ValidationRules.MaxPinAttempts"/> of them before the lock answers.
    /// The count is of the comparisons, taken in the hasher, because the answers alone cannot tell
    /// a guess that was refused unread from one that was compared and then refused.
    /// </summary>
    [SqlServerTheory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task ParallelWrongPins_AreComparedAtMostThresholdTimes(int burst)
    {
        const string pin = "123456";
        var (client, hasher) = CreateSqlClientCountingComparisons();

        for (var round = 0; round < BurstRounds; round++)
        {
            var (token, _) = await RegisterAsync(client);
            await SetPinAsync(client, token, pin);
            hasher.Reset();

            var answers = await SendTogetherAsync(client, token, WrongPins(burst));
            _output.WriteLine($"burst {burst}, round {round}: compared {hasher.Comparisons}, answers {Tally(answers)}");

            hasher.Comparisons.Should().BeLessThanOrEqualTo(ValidationRules.MaxPinAttempts,
                $"round {round}: a burst of {burst} must not buy more guesses than a queue of them");
            hasher.Comparisons.Should().Be(Math.Min(burst, ValidationRules.MaxPinAttempts),
                $"round {round}: the bound must not be reached by refusing guesses the window still owes");
            answers.Should().OnlyContain(a => a == Answer.Wrong || a == Answer.Locked,
                $"round {round}: a burst changes no answer's shape");
            answers.Count(a => a == Answer.Wrong).Should().BeLessThan(ValidationRules.MaxPinAttempts,
                $"round {round}: the attempt that crosses the threshold is answered by the lock");
        }
    }

    /// <summary>
    /// The right PIN gains nothing from arriving inside a burst: it is accepted only when it was
    /// one of the first <see cref="ValidationRules.MaxPinAttempts"/> compared, so a burst that
    /// happens to hold the right PIN is no better than the same guesses sent one by one. Accepted,
    /// it resets the count as it does in a queue, and the guesses still waiting get one window more.
    /// </summary>
    [SqlServerFact]
    public async Task RightPinInsideABurst_IsAcceptedOnlyAmongTheFirstThresholdCompared()
    {
        const string pin = "123456";
        const int burst = 20;
        var (client, hasher) = CreateSqlClientCountingComparisons();

        for (var round = 0; round < BurstRounds; round++)
        {
            var (token, _) = await RegisterAsync(client);
            await SetPinAsync(client, token, pin);
            hasher.Reset();

            // The right PIN goes last in the list; all of them start together.
            var answers = await SendTogetherAsync(client, token, [.. WrongPins(burst - 1), pin]);
            _output.WriteLine($"round {round}: compared {hasher.Comparisons}, right PIN {answers[^1]}, "
                + $"compared as number {hasher.MatchedAt}, answers {Tally(answers)}");

            answers[^1].Should().BeOneOf([Answer.Verified, Answer.Locked],
                $"round {round}: the right PIN is accepted or refused by the lock, never called wrong");
            if (answers[^1] == Answer.Verified)
            {
                hasher.MatchedAt.Should().BeInRange(1, ValidationRules.MaxPinAttempts,
                    $"round {round}: a right PIN that arrives behind the threshold is refused unread");
                hasher.Comparisons.Should().BeLessThanOrEqualTo(2 * ValidationRules.MaxPinAttempts,
                    $"round {round}: an accepted PIN opens one more window, not an open door");
            }
            else
            {
                hasher.MatchedAt.Should().Be(0, $"round {round}: a PIN the lock refused was never compared");
                hasher.Comparisons.Should().Be(ValidationRules.MaxPinAttempts,
                    $"round {round}: the lock answers after the threshold and not before");
            }
        }
    }

    private const int BurstRounds = 6;

    private enum Answer { Verified, Wrong, Locked, Other }

    /// <summary>Counts the comparisons with the stored PIN; everything else is the API's own hasher.</summary>
    private sealed class ComparisonCountingHasher : IPasswordHasher
    {
        private int _comparisons;
        private int _matchedAt;

        public IPasswordHasher? Inner { get; set; }

        public int Comparisons => Volatile.Read(ref _comparisons);

        /// <summary>Which comparison, counted from 1 as they began, first matched; 0 when none did.</summary>
        public int MatchedAt => Volatile.Read(ref _matchedAt);

        public void Reset()
        {
            Volatile.Write(ref _comparisons, 0);
            Volatile.Write(ref _matchedAt, 0);
        }

        public string HashPin(string pin) => Inner!.HashPin(pin);

        public bool VerifyPin(string hash, string pin)
        {
            var number = Interlocked.Increment(ref _comparisons);
            var matched = Inner!.VerifyPin(hash, pin);
            if (matched)
            {
                Interlocked.CompareExchange(ref _matchedAt, number, 0);
            }
            return matched;
        }

        public bool PinNeedsRehash(string hash) => Inner!.PinNeedsRehash(hash);

        public bool PinPepperMissingFor(string hash) => Inner!.PinPepperMissingFor(hash);
    }

    private (HttpClient Client, ComparisonCountingHasher Hasher) CreateSqlClientCountingComparisons()
    {
        var hasher = new ComparisonCountingHasher();
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _countingHost = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IPasswordHasher));
            services.Remove(real);
            services.AddSingleton<IPasswordHasher>(provider =>
            {
                hasher.Inner = (IPasswordHasher)real.ImplementationFactory!(provider);
                return hasher;
            });
        }));
        return (_countingHost.CreateClient(), hasher);
    }

    private static string[] WrongPins(int count) =>
        [.. Enumerable.Range(0, count).Select(i => i.ToString("D6", CultureInfo.InvariantCulture))];

    private static string Tally(IEnumerable<Answer> answers) =>
        string.Join(", ", answers.GroupBy(a => a).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"));

    /// <summary>
    /// Sends one verification per PIN, all released by one signal, and returns the answers in the
    /// order of <paramref name="pins"/>.
    /// </summary>
    private static async Task<Answer[]> SendTogetherAsync(HttpClient client, string token, string[] pins)
    {
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = pins.Select(p => Task.Run(async () =>
        {
            await go.Task;
            return await VerifyPinAnswerAsync(client, token, p);
        })).ToArray();
        go.SetResult();
        return await Task.WhenAll(sends);
    }

    private static async Task<Answer> VerifyPinAnswerAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin/verify")
        {
            Content = JsonContent.Create(new VerifyPinRequest { Pin = pin }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return response.StatusCode switch
        {
            HttpStatusCode.OK => body.RootElement.GetProperty("data").GetProperty("verified").GetBoolean()
                ? Answer.Verified
                : Answer.Wrong,
            HttpStatusCode.TooManyRequests
                when body.RootElement.GetProperty("errorCode").GetString() == ErrorCodes.PinLocked => Answer.Locked,
            _ => Answer.Other,
        };
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<(string Token, string AzureTag)> RegisterAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"pinlock_{uniqueId}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"pinlock{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Pin",
            LastName = "Lock"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return (result!.Data!.Token.AccessToken, azureTag);
    }

    private async Task<int> ReadPinAccessFailedCountAsync(string azureTag)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.Where(u => u.AzureTag == azureTag)
            .Select(u => u.PinAccessFailedCount).SingleAsync();
    }

    private static async Task SetPinAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
        {
            Content = JsonContent.Create(new SetPinRequest { Pin = pin, Password = "TestPass123!" }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpStatusCode> VerifyPinAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin/verify")
        {
            Content = JsonContent.Create(new VerifyPinRequest { Pin = pin }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    public void Dispose()
    {
        _countingHost?.Dispose();
        _factory?.Dispose();
    }
}
