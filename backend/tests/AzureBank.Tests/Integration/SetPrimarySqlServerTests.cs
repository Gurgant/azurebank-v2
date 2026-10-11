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
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Making a second account primary, on the provider that enforces "one primary per user".
/// </summary>
/// <remarks>
/// <para>
/// <c>UX_Accounts_UserId_Primary</c> is a filtered unique index (<c>IsPrimary = 1 AND IsDeleted = 0</c>).
/// <c>SetPrimaryAccountAsync</c> cleared the old primary and set the new one in ONE SaveChanges,
/// and SQL Server ran the new row's UPDATE first. Found by Schemathesis on 2026-09-15, the first
/// run of the conformance gate: <c>PATCH /api/accounts/{id}/set-primary</c> answered 500,
/// "Cannot insert duplicate key row in object 'dbo.Accounts' with unique index
/// 'UX_Accounts_UserId_Primary'. The duplicate key value is (&lt;userId&gt;, 1)." The InMemory
/// provider enforces no such index, which is why <c>AccountEndpointTests</c> has passed this
/// scenario since the endpoint existed; this lives behind <c>[SqlServerFact]</c> for that reason.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class SetPrimarySqlServerTests(ITestOutputHelper output) : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;
    private Guid _userId;

    [SqlServerFact]
    public async Task ASecondAccountBecomesPrimary_AndTheFirstStopsBeingIt()
    {
        var (client, first) = await SeedAccountAsync();
        var created = await client.PostAsJsonAsync(
            "/api/accounts", new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, Json);
        created.EnsureSuccessStatusCode();
        var second = (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;

        var response = await client.PatchAsync($"/api/accounts/{second}/set-primary", null);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "one primary per user is what the index enforces, and the service has to clear the old one "
            + "before it sets the new one: {0}", await response.Content.ReadAsStringAsync());
        (await client.GetFromJsonAsync<ApiResponse<AccountResponse>>($"/api/accounts/{second}", Json))!
            .Data!.IsPrimary.Should().BeTrue();
        (await client.GetFromJsonAsync<ApiResponse<AccountResponse>>($"/api/accounts/{first}", Json))!
            .Data!.IsPrimary.Should().BeFalse("the old primary was cleared in the same act");
    }

    /// <summary>
    /// Swaps racing for the same user all succeed, and exactly one account is primary afterwards.
    /// </summary>
    /// <remarks>
    /// Found in review on 2026-09-15: the service loaded the target and the current primary BEFORE
    /// its transaction, so two concurrent swaps each cleared a primary the other had already moved.
    /// Measured red before the fix: the loser threw DbUpdateConcurrencyException ("expected to affect
    /// 1 row(s), but actually affected 0") and answered 500, because the execution strategy retries
    /// transient faults only. Each round fires every account at once, twice over, so every request
    /// races at least one other swap.
    /// <para>
    /// And the retry alone was not the fix it looked like. Green five runs in five on an idle
    /// machine, it failed five in five on 2026-09-16 with eight CPU-bound processes running beside
    /// it: twelve swaps all clear the same primary row, one wins each round, and the rest run out
    /// of ConcurrencyRetry's eight attempts and answer 500. The per-user lock in
    /// <c>SetPrimaryAccountAsync</c> makes them take turns.
    /// </para>
    /// </remarks>
    [SqlServerFact]
    public async Task ConcurrentSwapsForOneUser_AllSucceed_AndExactlyOneAccountEndsPrimary()
    {
        var (client, first) = await SeedAccountAsync();
        var accounts = new List<Guid> { first };
        for (var i = 0; i < 5; i++)
        {
            var created = await client.PostAsJsonAsync(
                "/api/accounts", new CreateAccountRequest { Name = $"Extra {i}", Type = AccountType.Savings }, Json);
            created.EnsureSuccessStatusCode();
            accounts.Add((await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id);
        }

        var failures = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            var responses = await Task.WhenAll(
                accounts.Concat(accounts).Select(id => client.PatchAsync($"/api/accounts/{id}/set-primary", null)));
            foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                failures.Add($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }

        failures.Should().BeEmpty("a swap that loses a race with another swap for the same user runs again");
        var listed = (await client.GetFromJsonAsync<ApiResponse<List<AccountResponse>>>("/api/accounts", Json))!.Data!;
        listed.Count(a => a.IsPrimary).Should().Be(1, "the index allows one primary per user, and the service must leave one");
    }

    /// <summary>
    /// A swap racing deposits on the same accounts reloads and runs again instead of answering 500.
    /// </summary>
    /// <remarks>
    /// Deposits do not take the swap's lock, and each one moves the account's RowVersion, so a swap
    /// that read an account before a deposit landed saves against a stale token. The bounded retry in
    /// <c>SetPrimaryAccountAsync</c> is what absorbs it. Measured 2026-09-15 as the positive control:
    /// with that catch disabled this test answered 500 in five runs of five, and passed five of five
    /// with it (six rounds caught it only two runs in three, hence fifteen). Balances are asserted
    /// too, so a retry that replayed a deposit, or a swap that lost one, cannot pass.
    /// </remarks>
    [SqlServerFact]
    public async Task SwapsRacingDepositsOnTheSameAccounts_AllSucceed_AndNoDepositIsLost()
    {
        var (client, first) = await SeedAccountAsync();
        var accounts = new List<Guid> { first };
        for (var i = 0; i < 3; i++)
        {
            var created = await client.PostAsJsonAsync(
                "/api/accounts", new CreateAccountRequest { Name = $"Race {i}", Type = AccountType.Savings }, Json);
            created.EnsureSuccessStatusCode();
            accounts.Add((await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id);
        }

        const int rounds = 15;
        const decimal amount = 10m;
        var failures = new List<string>();
        for (var round = 0; round < rounds; round++)
        {
            var work = accounts.SelectMany(id => new Func<Task<HttpResponseMessage>>[]
            {
                () => client.PatchAsync($"/api/accounts/{id}/set-primary", null),
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
                    {
                        Content = JsonContent.Create(new DepositRequest { AccountId = id, Amount = amount }, options: Json),
                    };
                    request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
                    return client.SendAsync(request);
                },
            });
            var responses = await Task.WhenAll(work.Select(start => start()));
            foreach (var response in responses.Where(r => !r.IsSuccessStatusCode))
            {
                failures.Add($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }

        failures.Should().BeEmpty("a RowVersion moved by a deposit is retried by the swap, not surfaced");
        var listed = (await client.GetFromJsonAsync<ApiResponse<List<AccountResponse>>>("/api/accounts", Json))!.Data!;
        listed.Count(a => a.IsPrimary).Should().Be(1);
        listed.Sum(a => a.Balance).Should().Be(
            amount * rounds * accounts.Count, "every deposit landed exactly once while the swaps ran");
    }

    /// <summary>
    /// A swap whose target row is deleted at its own save answers as a swap to an account that is
    /// gone does, and the primary stays where it was.
    /// </summary>
    /// <remarks>
    /// The demo pool's recycler deletes the rows of an expired copy where every other writer closes
    /// them. The removal rides the second save of the swap, the one that sets the new primary, after
    /// the first cleared the old one inside the same transaction: the save matches no row, the
    /// transaction rolls back, and the next attempt reads the target from the store and finds none.
    /// </remarks>
    [SqlServerFact]
    public async Task ARemovalOfTheNewPrimaryRowAtItsSave_AnswersNotFound_AndThePrimaryStaysWhereItWas()
    {
        var (client, first) = await SeedAccountAsync();
        var second = await CreateAccountAsync(client, "Removed target");
        var before = await ReadAsync(first, second);

        // Narrowed to the command that carries the target's id: the swap's first save is the old primary's.
        var race = new OutOfBandClosureInterceptor(
            SqlServerFactAttribute.ConnectionString!, second, remove: true, onTheReadOf: second);
        _factory!.AddInterceptor(race);
        race.Arm();

        var response = await client.PatchAsync($"/api/accounts/{second}/set-primary", null);
        var body = await response.Content.ReadAsStringAsync();
        var sent = race.Seen;
        var after = await ReadAsync(first, second);

        var later = await client.PatchAsync($"/api/accounts/{second}/set-primary", null);
        var laterBody = await later.Content.ReadAsStringAsync();

        output.WriteLine(
            $"new primary row removed at its save -> {(int)response.StatusCode}, body: {body}, "
            + $"race.Fired: {race.Fired}, race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}, {sent}, "
            + $"before: {before}, after: {after}, the same swap again: {(int)later.StatusCode}, body: {laterBody}");

        using var assertions = new AssertionScope();
        race.Fired.Should().BeTrue("the removal must run for the proof to be about a row that is gone");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must take the row the swap is about to write");
        sent.RodeAnAccountUpdate.Should().BeTrue("the removal lands on a save of the swap");
        sent.AccountUpdatesBefore.Should().Be(2, "the old primary's save first, then the target's own");
        sent.AccountUpdatesAfter.Should().Be(0, "the next attempt finds no target and saves nothing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a swap to an account whose row is gone must not answer that it made it primary");
        ErrorCodeOf(body).Should().Be(ErrorCodes.AccountNotFound);
        later.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ErrorCodeOf(laterBody).Should().Be(ErrorCodes.AccountNotFound,
            "the race answers what a swap to an account already gone answers");

        after.Should().Be(
            before with { Accounts = [before.Accounts[0], Gone], AccountRowsOfTheUser = 1 },
            "the first save rolled back with the transaction: the old primary is the row it was");
        before.Primaries.Should().Be(1);
    }

    /// <summary>
    /// A swap whose old primary row is deleted at the save that clears it still makes the target
    /// primary, and leaves the user with that one primary account.
    /// </summary>
    /// <remarks>
    /// The request is on the target, and the target is there. From the row that stops being primary
    /// the swap needs one thing, that it no longer holds the user's one primary slot in the unique
    /// index, and a row that is gone holds none. The save that loses sends the swap round its retry,
    /// which reads the primary from the store, finds none to clear and sets the target.
    /// </remarks>
    [SqlServerFact]
    public async Task ARemovalOfTheOldPrimaryRowAtItsSave_StillMakesTheTargetTheOnePrimary()
    {
        var (client, first) = await SeedAccountAsync();
        var second = await CreateAccountAsync(client, "Target");
        var before = await ReadAsync(first, second);

        var race = new OutOfBandClosureInterceptor(SqlServerFactAttribute.ConnectionString!, first, remove: true);
        _factory!.AddInterceptor(race);
        race.Arm();

        var response = await client.PatchAsync($"/api/accounts/{second}/set-primary", null);
        var body = await response.Content.ReadAsStringAsync();
        var sent = race.Seen;
        var after = await ReadAsync(first, second);

        output.WriteLine(
            $"old primary row removed at its save -> {(int)response.StatusCode}, body: {body}, "
            + $"race.Fired: {race.Fired}, race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}, {sent}, "
            + $"before: {before}, after: {after}");

        using var assertions = new AssertionScope();
        race.Fired.Should().BeTrue("the removal must run for the proof to be about a row that is gone");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must take the row the swap is about to clear");
        sent.RodeAnAccountUpdate.Should().BeTrue("the removal lands on a save of the swap");
        sent.AccountUpdatesBefore.Should().Be(1, "the old primary's save is the first the swap sends");
        sent.AccountUpdatesAfter.Should().Be(1, "the next attempt has no primary to clear and saves the target only");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the target is there, and nothing else holds the primary slot");
        after.Accounts[0].Should().Be(Gone);
        after.Accounts[1].Should().StartWith("primary: True, closed: False", "the target is the primary account");
        after.Primaries.Should().Be(1, "the user is left with exactly one primary account");
        after.AccountRowsOfTheUser.Should().Be(1, "the swap does not write the removed row back");
        after.AuditRowsOfTheUser.Should().Be(before.AuditRowsOfTheUser, "a swap writes no audit row");
        before.Primaries.Should().Be(1);
    }

    private const string Gone = "gone";

    /// <summary>
    /// What the database holds for one user, as a fresh scope reads it. <c>Accounts</c> is in the
    /// order the accounts were asked for, each with its row version, and "gone" for a row that is
    /// not there.
    /// </summary>
    private sealed record Rows(
        IReadOnlyList<string> Accounts, int AccountRowsOfTheUser, int Primaries, int AuditRowsOfTheUser)
    {
        public bool Equals(Rows? other) =>
            other is not null
            && Accounts.SequenceEqual(other.Accounts)
            && AccountRowsOfTheUser == other.AccountRowsOfTheUser
            && Primaries == other.Primaries
            && AuditRowsOfTheUser == other.AuditRowsOfTheUser;

        public override int GetHashCode() =>
            HashCode.Combine(string.Join("|", Accounts), AccountRowsOfTheUser, Primaries, AuditRowsOfTheUser);

        public override string ToString() =>
            $"[{string.Join("; ", Accounts)}], account rows of the user: {AccountRowsOfTheUser}, "
            + $"open primaries: {Primaries}, audit rows of the user: {AuditRowsOfTheUser}";
    }

    private async Task<Rows> ReadAsync(params Guid[] accountIds)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var rows = await db.Accounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.UserId == _userId).ToListAsync();
        var accounts = accountIds
            .Select(id => rows.SingleOrDefault(a => a.Id == id) is { } row
                ? $"primary: {row.IsPrimary}, closed: {row.IsDeleted}, row version: {Convert.ToHexString(row.RowVersion)}"
                : Gone)
            .ToList();

        return new Rows(
            accounts, rows.Count, rows.Count(a => a.IsPrimary && !a.IsDeleted),
            await db.AuditEvents.AsNoTracking().CountAsync(e => e.ActorUserId == _userId));
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync(
            "/api/accounts", new CreateAccountRequest { Name = name, Type = AccountType.Savings }, Json);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    private static string? ErrorCodeOf(string body)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("errorCode", out var code)
            ? code.GetString()
            : null;
    }

    private async Task<(HttpClient Client, Guid AccountId)> SeedAccountAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"prim_{unique}",
            Email = $"prim{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Set",
            LastName = "Primary",
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        _userId = registered.User.Id;
        return (client, registered.Account.Id);
    }

    public void Dispose() => _factory?.Dispose();
}
