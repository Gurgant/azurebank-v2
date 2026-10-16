using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The bound on the rows a refused PIN writes, on SQL Server, at the four mints, at the PIN change
/// and at the PIN check: one row for each PIN that was compared with the stored one and found wrong,
/// and none for a request that the lock refused unread (ADR-0044).
/// </summary>
/// <remarks>
/// Behind <c>[SqlServerTheory]</c> because the two things it holds exist only there: a refusal
/// committed on its own connection, and requests that reach the user's row together. The count of
/// comparisons is taken in the hasher, because a 429 does not say whether its PIN was read.
/// <c>AuditTrailPersistenceTests</c> holds the counts of a lockout sent one by one on every run.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PinRefusalRowBoundSqlServerTests : IDisposable
{
    private const string Password = "SecurePass123!";
    private const string RightPin = "123456";
    private const string WrongPin = "999999";
    private const int Burst = 20;
    private const int BurstRounds = 6;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private readonly ComparisonCountingHasher _hasher = new();
    private CustomWebApplicationFactory? _factory;
    private WebApplicationFactory<Program>? _host;

    public PinRefusalRowBoundSqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerTheory]
    [InlineData("external")]
    [InlineData("internal")]
    [InlineData("deletion")]
    [InlineData("withdrawal")]
    [InlineData("change")]
    [InlineData("verify")]
    public async Task RequestsAgainstAStandingLock_Answer429_AndWriteNoRow(string route)
    {
        var client = CreateClient();
        var scenario = await ArrangeAsync(client, route);

        // Three guesses, then five requests against the lock they armed, the right PIN among them.
        string[] pins = [WrongPin, WrongPin, WrongPin, WrongPin, WrongPin, RightPin, WrongPin, WrongPin];
        var answers = new List<HttpStatusCode>();
        var locks = new List<LockAnswer>();
        foreach (var pin in pins)
        {
            using var response = await client.PostAsJsonAsync(scenario.Path, scenario.Body(pin), Json);
            answers.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                locks.Add(await ReadLockAnswerAsync(response));
            }
        }

        var rows = await RowsAsync(scenario);
        _output.WriteLine($"{route}: answers {string.Join(",", answers.Select(a => (int)a))}");
        _output.WriteLine($"{route}: rows {Describe(rows)}");
        _output.WriteLine($"{route}: the lock's answer {locks.FirstOrDefault()?.Raw}");
        _output.WriteLine($"{route}: its headers {locks.FirstOrDefault()?.Headers}");

        answers.Should().Equal(
            [
                scenario.Wrong,
                scenario.Wrong,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests,
            ],
            "the third wrong PIN arms the lock, and the lock refuses every request after it, the right PIN too");

        rows.Select(r => r.Detail).Should().Equal(
            [ErrorCodes.InvalidPin, ErrorCodes.InvalidPin, ErrorCodes.PinLocked],
            "a row is written for a PIN that was compared and found wrong: the two under the threshold and "
            + "the one that arms the lock. The five requests the standing lock refused were not read, the "
            + "right PIN among them, and a row for each would be a write the caller repeats at will");
        rows.Should().OnlyContain(r =>
            r.Event == scenario.Event && r.Outcome == AuditOutcome.Refused
            && r.SubjectType == scenario.SubjectType && r.SubjectId == scenario.Subject);

        locks.Should().HaveCount(6);
        foreach (var answer in locks)
        {
            AssertIsTheLocksAnswer(answer, scenario.Path);
        }

        await AssertTheChainVerifiesAsync();
    }

    [SqlServerTheory]
    [InlineData("external")]
    [InlineData("internal")]
    [InlineData("deletion")]
    [InlineData("withdrawal")]
    [InlineData("change")]
    [InlineData("verify")]
    public async Task ABurstOfWrongPins_WritesOneRowForEachPinThatWasCompared(string route)
    {
        var client = CreateClient();

        // Every round is run and written out before any of them fails the test.
        using (new AssertionScope())
        {
            for (var round = 0; round < BurstRounds; round++)
            {
                var scenario = await ArrangeAsync(client, route);
                _hasher.Reset();

                var answers = await SendTogetherAsync(client, scenario, WrongPins(Burst));

                var compared = _hasher.Comparisons;
                var rows = await RowsAsync(scenario);
                _output.WriteLine(
                    $"{route}, round {round}: sent {Burst}, compared {compared}, answers {Tally(answers)}, "
                    + $"rows {rows.Count} ({Describe(rows)})");

                answers.Should().OnlyContain(
                    a => a == scenario.Wrong || a == HttpStatusCode.TooManyRequests,
                    $"round {round}: a wrong PIN is answered {(int)scenario.Wrong}, or 429 by the lock");
                compared.Should().Be(
                    ValidationRules.MaxPinAttempts, $"round {round}: ADR-0010 bounds what a burst has compared");
                rows.Count.Should().Be(
                    compared,
                    $"round {round}: one row for each PIN that was compared and found wrong, and none for the "
                    + $"{Burst - compared} the lock refused unread");
                rows.Should().OnlyContain(
                    r => r.Detail == ErrorCodes.InvalidPin || r.Detail == ErrorCodes.PinLocked,
                    $"round {round}: the detail is the answer its request received");
                rows.Should().Contain(
                    r => r.Detail == ErrorCodes.PinLocked,
                    $"round {round}: the wrong PIN that arms the lock is answered by it, and leaves its row");
                rows.Count(r => r.Detail == ErrorCodes.InvalidPin).Should().Be(
                    answers.Count(a => a == scenario.Wrong),
                    $"round {round}: every {(int)scenario.Wrong} was a PIN compared under the threshold");
            }
        }

        await AssertTheChainVerifiesAsync();
    }

    /// <summary>
    /// A right PIN that is accepted leaves no refusal row and resets the count, and the wrong PINs
    /// on either side of it each leave theirs. Sent one by one, so the right PIN is read on every
    /// run.
    /// </summary>
    [SqlServerTheory]
    [InlineData("external")]
    [InlineData("internal")]
    [InlineData("deletion")]
    [InlineData("withdrawal")]
    [InlineData("change")]
    [InlineData("verify")]
    public async Task ARightPinBetweenWrongOnes_IsAccepted_AndLeavesNoRow(string route)
    {
        var client = CreateClient();
        var scenario = await ArrangeAsync(client, route);
        _hasher.Reset();

        string[] pins = [WrongPin, RightPin, WrongPin, WrongPin];
        var answers = new List<HttpStatusCode>();
        foreach (var pin in pins)
        {
            using var response = await client.PostAsJsonAsync(scenario.Path, scenario.Body(pin), Json);
            answers.Add(response.StatusCode);
        }

        var rows = await RowsAsync(scenario);
        _output.WriteLine(
            $"{route}: answers {string.Join(",", answers.Select(a => (int)a))}, "
            + $"compared {_hasher.Comparisons}, rows {Describe(rows)}");

        answers.Should().Equal(
            [scenario.Wrong, scenario.Accepted, scenario.Wrong, scenario.Wrong],
            "the right PIN is accepted and resets the count, so the two wrong PINs after it are the first "
            + "and the second of a new count and no lock is armed");
        _hasher.Comparisons.Should().Be(4, "every one of the four PINs was read");
        rows.Select(r => r.Detail).Should().Equal(
            [ErrorCodes.InvalidPin, ErrorCodes.InvalidPin, ErrorCodes.InvalidPin],
            "one row for each of the three PINs that were compared and found wrong, and none for the PIN "
            + "that was compared and accepted");
        rows.Should().OnlyContain(r =>
            r.Event == scenario.Event && r.Outcome == AuditOutcome.Refused
            && r.SubjectType == scenario.SubjectType && r.SubjectId == scenario.Subject);

        await AssertTheChainVerifiesAsync();
    }

    /// <summary>
    /// A right PIN that is accepted inside a burst resets the count, so up to three more of that
    /// burst are compared (ADR-0010): the rows follow the comparisons there too. Whether the right
    /// PIN is read before the lock is armed depends on the order in which the requests reach the
    /// user's row, so a round may or may not accept it;
    /// <see cref="ARightPinBetweenWrongOnes_IsAccepted_AndLeavesNoRow"/> holds the accepted PIN on
    /// every run.
    /// </summary>
    [SqlServerFact]
    public async Task ABurstHoldingTheRightPin_WritesOneRowForEachWrongPinThatWasCompared()
    {
        var client = CreateClient();

        // Every round is run and written out before any of them fails the test.
        using (new AssertionScope())
        {
            for (var round = 0; round < BurstRounds; round++)
            {
                var scenario = await ArrangeAsync(client, "withdrawal");
                _hasher.Reset();

                var answers = await SendTogetherAsync(client, scenario, [RightPin, .. WrongPins(Burst - 1)]);

                var compared = _hasher.Comparisons;
                var accepted = answers[0] == HttpStatusCode.Created;
                var rows = await RowsAsync(scenario);
                _output.WriteLine(
                    $"round {round}: sent {Burst}, compared {compared}, right PIN {(int)answers[0]}, "
                    + $"answers {Tally(answers)}, rows {rows.Count} ({Describe(rows)})");

                answers[0].Should().BeOneOf(
                    [HttpStatusCode.Created, HttpStatusCode.TooManyRequests],
                    $"round {round}: the right PIN is accepted or refused by the lock, never called wrong");
                accepted.Should().Be(_hasher.MatchedAt != 0, $"round {round}: a right PIN is accepted when it is read");
                compared.Should().BeLessThanOrEqualTo(
                    2 * ValidationRules.MaxPinAttempts, $"round {round}: ADR-0010 bounds what a burst has compared");
                rows.Count.Should().Be(
                    compared - (accepted ? 1 : 0),
                    $"round {round}: one row for each wrong PIN that was compared; an accepted PIN and a PIN "
                    + "the lock refused unread leave none");
            }
        }

        await AssertTheChainVerifiesAsync();
    }

    /// <summary>
    /// A route under test. <paramref name="Wrong"/> is how it answers a wrong PIN under the threshold
    /// and <paramref name="Accepted"/> how it answers the right one: the mints refuse with 401 and
    /// create with 201, the PIN change answers 401 and 200, and the PIN check answers 200 to both,
    /// with <c>verified</c> false or true.
    /// </summary>
    private sealed record Scenario(
        Guid UserId, string Path, Func<string, object> Body, string Event, string SubjectType, Guid Subject,
        HttpStatusCode Wrong = HttpStatusCode.Unauthorized, HttpStatusCode Accepted = HttpStatusCode.Created);

    private sealed record LockAnswer(string Raw, string Headers, string? MediaType, string[] RetryAfter);

    /// <summary>
    /// The lock's 429 as a caller reads it: the members of the body in their order, their values
    /// apart from the two times and the trace id, and the header that repeats the wait.
    /// </summary>
    private static void AssertIsTheLocksAnswer(LockAnswer answer, string path)
    {
        using var body = JsonDocument.Parse(answer.Raw);
        var root = body.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().Equal(
            [
                "type", "title", "status", "detail", "instance", "errorCode", "traceId",
                "retryAfterSeconds", "lockedUntil",
            ],
            "the body has these members and no other: nothing the server keeps about the refusal is added");
        answer.MediaType.Should().Be("application/json");
        root.GetProperty("type").GetString().Should().Be("https://httpstatuses.com/429");
        root.GetProperty("title").GetString().Should().Be("Too Many Requests");
        root.GetProperty("status").GetInt32().Should().Be(429);
        root.GetProperty("detail").GetString().Should().Be(
            "Too many incorrect PIN attempts. Your PIN is temporarily locked; try again later.");
        root.GetProperty("instance").GetString().Should().Be(path);
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.PinLocked);

        var retryAfterSeconds = root.GetProperty("retryAfterSeconds").GetInt32();
        retryAfterSeconds.Should().BeInRange(1, ValidationRules.PinLockoutMinutes * 60);
        var lockedUntil = root.GetProperty("lockedUntil").GetString()!;
        DateTimeOffset.ParseExact(lockedUntil, "o", CultureInfo.InvariantCulture)
            .Should().BeCloseTo(
                DateTimeOffset.UtcNow.AddMinutes(ValidationRules.PinLockoutMinutes), TimeSpan.FromMinutes(1));
        answer.RetryAfter.Should().Equal(
            [retryAfterSeconds.ToString(CultureInfo.InvariantCulture)],
            "the header repeats the wait the body gives");
    }

    private static async Task<LockAnswer> ReadLockAnswerAsync(HttpResponseMessage response) =>
        new(
            await response.Content.ReadAsStringAsync(),
            string.Join("; ", response.Headers.Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}")),
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.TryGetValues("Retry-After", out var values) ? [.. values] : []);

    /// <summary>
    /// A fresh user with a PIN, and the route under test: its path, its body for a given PIN, the
    /// event a refusal there writes and the subject that row names.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(HttpClient client, string route)
    {
        var (userId, accountId) = await RegisterAsync(client, withPin: true);
        switch (route)
        {
            case "external":
                {
                    var payeeTag = await RegisterPayeeAsync();
                    return new Scenario(userId, "/api/transfers/authorizations",
                        pin => new TransferAuthorizationRequest
                        {
                            FromAccountId = accountId,
                            RecipientAzureTag = payeeTag,
                            Amount = 10m,
                            Pin = pin,
                        },
                        SecurityEvents.MoneyTransferRefused, "Account", accountId);
                }
            case "internal":
                {
                    var spare = await CreateSpareAccountAsync(client);
                    return new Scenario(userId, "/api/transfers/internal/authorizations",
                        pin => new InternalTransferAuthorizationRequest
                        {
                            FromAccountId = accountId,
                            ToAccountId = spare,
                            Amount = 10m,
                            Pin = pin,
                        },
                        SecurityEvents.MoneyTransferRefused, "Account", accountId);
                }
            case "deletion":
                {
                    var spare = await CreateSpareAccountAsync(client);
                    return new Scenario(userId, $"/api/accounts/{spare}/deletion-authorizations",
                        pin => new AccountDeletionAuthorizationRequest { Pin = pin },
                        SecurityEvents.AccountDeletionRefused, "Account", spare);
                }
            case "withdrawal":
                return new Scenario(userId, "/api/transactions/withdraw/authorizations",
                    pin => new WithdrawalAuthorizationRequest { AccountId = accountId, Amount = 10m, Pin = pin },
                    SecurityEvents.MoneyWithdrawalRefused, "Account", accountId);
            case "change":
                return new Scenario(userId, "/api/auth/pin",
                    pin => new SetPinRequest { Pin = "654321", CurrentPin = pin },
                    SecurityEvents.PinChangeRefused, "User", userId, Accepted: HttpStatusCode.OK);
            case "verify":
                return new Scenario(userId, "/api/auth/pin/verify",
                    pin => new VerifyPinRequest { Pin = pin },
                    SecurityEvents.PinVerificationRefused, "User", userId,
                    Wrong: HttpStatusCode.OK, Accepted: HttpStatusCode.OK);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(route), route, "external, internal, deletion, withdrawal, change or verify");
        }
    }

    /// <summary>The refusal rows of the scenario's user, read from SQL Server in a fresh scope.</summary>
    private async Task<List<AuditEvent>> RowsAsync(Scenario scenario)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == scenario.UserId && e.Outcome == AuditOutcome.Refused)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
    }

    private async Task AssertTheChainVerifiesAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var verification = await scope.ServiceProvider.GetRequiredService<IAuditChain>().VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
    }

    /// <summary>
    /// Sends one request per PIN, all released by one signal, and returns the answers in the order
    /// of <paramref name="pins"/>.
    /// </summary>
    private async Task<HttpStatusCode[]> SendTogetherAsync(HttpClient client, Scenario scenario, string[] pins)
    {
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = pins.Select(pin => Task.Run(async () =>
        {
            await go.Task;
            using var response = await client.PostAsJsonAsync(scenario.Path, scenario.Body(pin), Json);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        })).ToArray();
        go.SetResult();
        var answers = await Task.WhenAll(sends);
        foreach (var (status, body) in answers.Where(a => a.StatusCode is not (
            HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Unauthorized
            or HttpStatusCode.TooManyRequests)))
        {
            _output.WriteLine($"an answer that is neither an acceptance, a 401 nor a 429: {(int)status} {body}");
        }
        return [.. answers.Select(a => a.StatusCode)];
    }

    private static string[] WrongPins(int count) =>
        [.. Enumerable.Range(0, count).Select(i => i.ToString("D6", CultureInfo.InvariantCulture))];

    private static string Tally(IEnumerable<HttpStatusCode> answers) =>
        string.Join(", ", answers.GroupBy(a => (int)a).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"));

    private static string Describe(IEnumerable<AuditEvent> rows) =>
        string.Join(", ", rows.GroupBy(r => $"{r.Event} {r.Detail}").Select(g => $"{g.Key} x{g.Count()}"));

    private HttpClient CreateClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(_hasher.InstallIn));
        return _host.CreateClient();
    }

    private static async Task<(Guid UserId, Guid AccountId)> RegisterAsync(HttpClient client, bool withPin)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rowb_{unique}",
            Email = $"rowb{unique}@example.com",
            Password = Password,
            FirstName = "Row",
            LastName = "Bound",
        }, Json);
        response.EnsureSuccessStatusCode();

        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        if (withPin)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
            (await client.PostAsJsonAsync(
                    "/api/auth/pin", new SetPinRequest { Pin = RightPin, Password = Password }, Json))
                .EnsureSuccessStatusCode();
        }

        return (registered.User.Id, registered.Account.Id);
    }

    /// <summary>A second user to pay, registered on a client of its own so the actor's stays put.</summary>
    private async Task<string> RegisterPayeeAsync()
    {
        using var payeeClient = _host!.CreateClient();
        var (payeeId, _) = await RegisterAsync(payeeClient, withPin: false);
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == payeeId)).AzureTag;
    }

    private static async Task<Guid> CreateSpareAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/accounts", new CreateAccountRequest { Name = "Spare", Type = AccountType.Savings }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    public void Dispose()
    {
        _host?.Dispose();
        _factory?.Dispose();
    }
}
