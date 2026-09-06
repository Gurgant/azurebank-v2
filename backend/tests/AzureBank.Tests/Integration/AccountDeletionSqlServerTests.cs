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
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The four properties of an authorised closure (ADR-0049) that only real SQL Server can prove.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>CONSUME AFTER THE SAVE, AND THE SAVE ROLLS BACK WITH IT. The soft delete, its
/// <c>AccountDeleted</c> row and the spend commit as one unit or not at all; a spend that matches
/// zero rows takes the closure down with it. InMemory has no transaction to roll back, so a green
/// run there says nothing (<c>AccountServiceTests</c> pins only the order).</item>
/// <item>ACCEPTED ONCE under real contention: eight concurrent closures presenting one
/// authorisation close the account once and spend the authorisation once. InMemory has no row
/// locks and cannot exhibit the race.</item>
/// <item>THE RE-ENTRANCY DISCIPLINE: under <c>EnableRetryOnFailure</c> the closure delegate re-runs
/// on a transient fault against the SAME DbContext, and a retry must not leave two
/// <c>AccountDeleted</c> rows or a doubled closure. This is the test that makes the discipline in
/// <c>AccountService.DeleteAccountAsync</c> a measured claim rather than a comment.</item>
/// <item>THE GUARDS RUN AGAIN AFTER A RELOAD: a deposit that lands between the first attempt's
/// guard and its UPDATE moves the account's <c>RowVersion</c>, the attempt loses, and the retry
/// must refuse the now-funded account (422) rather than close it on a balance read before the
/// money arrived. InMemory downgrades <c>RowVersion</c> to a plain column, so no attempt there
/// ever goes round the retry loop; only the real provider can exhibit the race, and without this
/// test the guards-again call in the delegate could be deleted with every other test still
/// green (added 2026-09-06 after a review mutated exactly that).</item>
/// </list>
/// <para>
/// Gated by AZUREBANK_TEST_SQLSERVER and serialised with the other SQL proofs, like every file in
/// this directory that needs the real provider.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AccountDeletionSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Pin = "123456";

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public AccountDeletionSqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task WhenTheConsumeMatchesZeroRows_TheSoftDeleteIsRolledBackToo()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "delrb");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintAsync(client, user, spare);

        /*
          The race is lost BETWEEN the validation and the spend: the interceptor consumes the
          authorisation from a second connection on the closure's own UPDATE [Accounts], which is
          issued after ValidateAsync passed and before ConsumeAsync runs. See the fixture for why
          consuming any earlier would prove nothing about the transaction.
        */
        var race = new OutOfBandStepUpConsumeInterceptor(
            SqlServerFactAttribute.ConnectionString!, authorizationId);
        _factory!.AddInterceptor(race);
        race.Arm();

        var response = await DeleteAsync(client, user, spare, authorizationId);

        race.Fired.Should().BeTrue("the out-of-band consume must actually have run, else the test proves nothing");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must have won: the row was still Pending when it ran");

        // Expected from the code: ConsumeAsync matched zero rows, threw INVALID, and the throw
        // rolled the transaction back — so the caller is told the authorisation cannot be used.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.AuthorizationInvalid);

        // A FRESH scope, so the answer comes from SQL Server and not from a tracker that just
        // rolled back.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == spare);
        account.IsDeleted.Should().BeFalse(
            "the soft delete was sent before the spend and must have rolled back with it");
        account.DeletedAt.Should().BeNull();

        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == user.UserId && e.Event == SecurityEvents.AccountDeleted))
            .Should().Be(0, "the success row rode the same transaction and must be gone with it");

        var verification = await chain.VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().BeGreaterThan(0, "an empty read also reports intact");

        _output.WriteLine(
            $"consume matched 0 rows -> {(int)response.StatusCode}, IsDeleted={account.IsDeleted}, "
            + $"chain intact over {verification.Verified} rows");
    }

    [SqlServerFact]
    public async Task EightConcurrentDeletes_PresentingOneAuthorisation_CloseTheAccountOnce()
    {
        const int concurrency = 8;

        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "delcc");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintAsync(client, user, spare);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, concurrency).Select(_ => DeleteAsync(client, user, spare, authorizationId)));

        var statuses = responses.Select(r => r.StatusCode).ToArray();
        _output.WriteLine("concurrent statuses: " + string.Join(",", statuses.Select(s => (int)s)));

        /*
          ONE SUCCESS, and every other attempt refused — but not all refused the SAME way, and the
          test does not pretend otherwise. A loser that validated before the winner committed and
          then reached its own UPDATE finds the account's RowVersion moved, goes round the retry
          loop, reloads a closed account and answers 404 as any later DELETE does; one that validated
          after the spend is refused at the mint's side (401 AUTHORIZATION_INVALID); one whose
          ownership check ran after the commit sees the account gone (404). What every path shares
          is the invariant below: nothing but the winner closes anything, and nobody answers 500.

          The first version of this proof accepted any non-2xx from the losers and printed
          "500,500,500,500,500,200,500,500": the RowVersion refusal surfaced as an unmapped
          DbUpdateConcurrencyException. The retry loop in DeleteAccountAsync is what this now pins.
        */
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1,
            "exactly one concurrent closure may spend a single authorisation");
        statuses.Count(s => (int)s is >= 200 and < 300).Should().Be(1, "no other attempt may succeed");
        statuses.Where(s => s != HttpStatusCode.OK).Should().OnlyContain(
            s => s == HttpStatusCode.NotFound || s == HttpStatusCode.Unauthorized,
            "a loser answers as a later DELETE (404) or as a spent authorisation (401), never 500");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == spare);
        account.IsDeleted.Should().BeTrue("the winner closed it");

        var authorization = await db.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == authorizationId);
        authorization.Status.Should().Be(StepUpAuthorizationStatus.Consumed);
        authorization.ConsumedByTransactionId.Should().BeNull("a closure names no ledger row");

        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == user.UserId && e.Event == SecurityEvents.AccountDeleted))
            .Should().Be(1, "one closure, one row, whatever the interleaving");

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [SqlServerFact]
    public async Task ATransientFault_DoesNotWriteAccountDeletedTwice()
    {
        /*
          THE PRODUCTION WIRING. EnableRetryOnFailure re-runs the closure delegate on a transient
          fault against the same DbContext. Without the discipline at the top of the delegate, the
          retry would find IsDeleted already true in memory and the first attempt's Added
          AuditEvent still tracked, and would commit a second AccountDeleted row for one closure.
          The fault is injected on the closure's own SaveChanges batch — the one carrying the
          INSERT INTO [AuditEvents] — so it hits after the delegate has mutated state and before
          anything committed (Case A in TransferService's terms).
        */
        var fault = new TransientFailureInterceptor("INSERT INTO [AuditEvents]");
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.EnableSqlRetryOnFailure();
        var client = _factory.CreateClient();

        var user = await RegisterWithPinAsync(client, "delrt");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintAsync(client, user, spare);

        // Registered only now: the PIN enrolment above also inserts an audit row, and the one-shot
        // fault must be spent by the closure, not by that.
        _factory.AddInterceptor(fault);

        var response = await DeleteAsync(client, user, spare, authorizationId);

        fault.Fired.Should().BeTrue("the transient must actually have been injected, else the test proves nothing");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the injected transient must be absorbed by the execution strategy, not surfaced");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == user.UserId && e.Event == SecurityEvents.AccountDeleted))
            .Should().Be(1, "one closure, one row: the retry must not re-enlist the failed attempt's row");

        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == spare);
        account.IsDeleted.Should().BeTrue("closed exactly once");

        var authorization = await db.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == authorizationId);
        authorization.Status.Should().Be(StepUpAuthorizationStatus.Consumed, "spent by the attempt that committed");
        authorization.ConsumedByTransactionId.Should().BeNull();

        var verification = await chain.VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);

        _output.WriteLine($"transient absorbed -> {(int)response.StatusCode}, one AccountDeleted row, chain intact");
    }

    [SqlServerFact]
    public async Task ADepositThatRacesTheClosure_IsRefusedOnTheRetry_AndSpendsNothing()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "deldp");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintAsync(client, user, spare);

        /*
          THE RACE THE GUARDS-AGAIN CALL EXISTS FOR. The first attempt read a zero balance, passed
          both guards, validated the authorisation and sent its UPDATE [Accounts]; on that very
          command the interceptor funds the account from a second connection, which moves the
          RowVersion, so the closure's UPDATE (WHERE RowVersion = the one it read) matches no row
          and SaveChanges throws DbUpdateConcurrencyException. The outer loop reloads — balance 5,
          still open — and re-enters the delegate, where RefuseIfNotClosable answers 422 before a
          transaction is opened. Delete that call and the retry closes a funded account with a 200
          (mutated 2026-09-06, see AccountService for the observed output).
        */
        var race = new OutOfBandDepositInterceptor(
            SqlServerFactAttribute.ConnectionString!, spare, amount: 5m);
        _factory!.AddInterceptor(race);
        race.Arm();

        var response = await DeleteAsync(client, user, spare, authorizationId);
        var body = await response.Content.ReadAsStringAsync();

        race.Fired.Should().BeTrue("the out-of-band deposit must actually have run, else the test proves nothing");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must have landed on an account that was still open");

        // Expected from the code and observed 2026-09-06 on LocalDB: 422 NON_ZERO_BALANCE from the
        // retry — the same answer the first-pass guard gives a funded account.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.NonZeroBalance);

        // A FRESH scope, so every answer comes from SQL Server and not from the tracker the first
        // attempt left behind.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == spare);
        account.IsDeleted.Should().BeFalse("a funded account must not be closed on the strength of a stale balance");
        account.DeletedAt.Should().BeNull();
        account.Balance.Should().Be(5m, "the racing deposit is what the retry saw");

        var authorization = await db.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == authorizationId);
        authorization.Status.Should().Be(StepUpAuthorizationStatus.Pending, "a closure the guards refuse spends nothing");
        authorization.ConsumedAt.Should().BeNull();

        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == user.UserId && e.Event == SecurityEvents.AccountDeleted))
            .Should().Be(0, "no closure, no success row: the first attempt's row rolled back with its transaction");
        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == user.UserId && e.Event == SecurityEvents.AccountDeletionRefused))
            .Should().Be(0, "the 422 guards are business validation, log-only per ADR-0044");

        var verification = await chain.VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);

        _output.WriteLine(
            $"deposit raced the closure -> {(int)response.StatusCode} {problem.GetProperty("errorCode").GetString()}, "
            + $"IsDeleted={account.IsDeleted}, Balance={account.Balance}, authorisation {authorization.Status}, "
            + $"chain intact over {verification.Verified} rows");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private sealed record TestUser(string Token, Guid UserId, Guid PrimaryAccountId);

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<TestUser> RegisterWithPinAsync(HttpClient client, string prefix)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"{prefix}_{unique}",
            Email = $"{prefix}{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Closure",
            LastName = "Prover"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var user = new TestUser(
            registered!.Data!.Token.AccessToken, registered.Data.User.Id, registered.Data.Account.Id);

        (await SendAsync(client, user.Token, HttpMethod.Post, "/api/auth/pin",
            new SetPinRequest { Pin = Pin, Password = "TestPass123!" })).EnsureSuccessStatusCode();

        return user;
    }

    /// <summary>
    /// The primary cannot be closed, so every proof opens a second, empty account.
    /// </summary>
    private static async Task<Guid> CreateSpareAsync(HttpClient client, TestUser user)
    {
        var response = await SendAsync(client, user.Token, HttpMethod.Post, "/api/accounts",
            new CreateAccountRequest { Name = "Spare", Type = AccountType.Savings });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    private static async Task<Guid> MintAsync(HttpClient client, TestUser user, Guid accountId)
    {
        var response = await SendAsync(client, user.Token, HttpMethod.Post,
            $"/api/accounts/{accountId}/deletion-authorizations",
            new AccountDeletionAuthorizationRequest { Pin = Pin });
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json))!.Data!.AuthorizationId;
    }

    private static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, TestUser user, Guid accountId, Guid authorizationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/accounts/{accountId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
        return client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, string token, HttpMethod method, string url, T payload)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
