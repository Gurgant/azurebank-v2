using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The withdrawal's half of the step-up rail, proven on real SQL Server (ADR-0056).
///
/// <para>
/// ADR-0056 D6 states that the withdrawal was given an explicit transaction so a zero-row consume
/// rolls the withdrawal back, and D7 that the spend names the ledger row. Both were CLAIMS with
/// nothing behind them when the ADR was written -- the gap was found in review on #198, and this
/// file is it. The transfers and the closure each have their own equivalents; a rail with three
/// operations and proofs for two is a rail whose newest operation is the unproven one.
/// </para>
///
/// <para>
/// SQL-gated throughout, and that is not caution. InMemory has no row locks, no real transactions
/// and downgrades <c>RowVersion</c> to a plain column, so every race below is unobservable there
/// and a green run would assert the opposite of what it claims. Set
/// <c>AZUREBANK_TEST_SQLSERVER</c> or these skip.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class WithdrawalStepUpSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Pin = "123456";
    private const decimal Amount = 10m;

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public WithdrawalStepUpSqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 3.E.1 — one authorisation, eight concurrent withdrawals, exactly one debit.
    /// </summary>
    /// <remarks>
    /// THE ACCOUNT IS FUNDED FOR ALL EIGHT ON PURPOSE. With a balance that covered only one, the
    /// funds guard would refuse the losers and the test would pass while proving nothing about
    /// single use -- ADR-0056 D4 puts that guard ABOVE the authorisation, so it would mask exactly
    /// the result under test. `BalanceConcurrencySqlServerTests` is the overdraft proof and mints
    /// one authorisation per racer for the mirror-image reason.
    /// </remarks>
    [SqlServerFact]
    public async Task OneAuthorisation_SpentByEightConcurrentWithdrawals_MovesMoneyExactlyOnce()
    {
        const int concurrency = 8;

        var client = CreateSqlClient();
        // 500 covers eight withdrawals of 10 several times over.
        var (token, accountId) = await RegisterFundedAsync(client, funding: 500m);
        var authorizationId = await MintWithdrawalAsync(client, token, accountId, Amount);

        var before = await BalanceOfAsync(client, token, accountId);

        // Every attempt carries a DIFFERENT idempotency key: this must be decided by the
        // authorisation's single-use guarantee, not by the idempotency layer replaying a result.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, concurrency).Select(_ =>
                WithdrawAsync(client, token, accountId, Amount, authorizationId, Guid.NewGuid())));

        var statuses = responses.Select(r => r.StatusCode).ToArray();
        _output.WriteLine("concurrent statuses: " + string.Join(",", statuses.Select(s => (int)s)));

        statuses.Count(s => s == HttpStatusCode.Created).Should().Be(1,
            "exactly one concurrent withdrawal may spend a single authorisation");
        statuses.Count(s => s == HttpStatusCode.Unauthorized).Should().Be(concurrency - 1,
            "every loser must be refused, not silently dropped");

        // The COUNT alone is not the claim. A 401 AUTHORIZATION_REQUIRED satisfies it just as well,
        // and that one means the header never arrived -- the rail could be entirely broken and this
        // test would still be green. Every loser must be refused for the reason under test.
        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Unauthorized))
        {
            var refusal = JsonSerializer.Deserialize<JsonElement>(
                await loser.Content.ReadAsStringAsync());
            refusal.GetProperty("errorCode").GetString().Should().Be(
                ErrorCodes.AuthorizationInvalid,
                "the losers lost the AUTHORISATION race; they did not fail the header check");
        }
        statuses.Should().NotContain(HttpStatusCode.UnprocessableEntity,
            "the balance covers all eight, so a 422 here would mean the funds guard decided this "
            + "instead of the authorisation -- which is the masking this funding level prevents");

        var after = await BalanceOfAsync(client, token, accountId);
        (before - after).Should().Be(Amount,
            "the money must move exactly once, whatever the interleaving");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var authorization = await db.StepUpAuthorizations.AsNoTracking()
            .SingleAsync(a => a.Id == authorizationId);
        authorization.Status.Should().Be(StepUpAuthorizationStatus.Consumed);
        authorization.ConsumedByTransactionId.Should().NotBeNull(
            "ADR-0056 D7: a withdrawal produces a ledger row, so the spend names it -- unlike a "
            + "closure, which passes null because the evidence verb's join could never match");

        var withdrawals = await db.Transactions.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.Type == TransactionType.Withdrawal)
            .ToListAsync();
        withdrawals.Should().HaveCount(1, "one debit, one ledger row");
        authorization.ConsumedByTransactionId.Should().Be(withdrawals[0].Id,
            "and the spend names THAT row, not merely some row");

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// 3.E.2 — a consume inside a rolled-back transaction leaves the authorisation spendable.
    /// </summary>
    /// <remarks>
    /// Proven at the seam that carries the risk: consume inside an explicit transaction on a REAL
    /// relational context, roll it back, then read through a FRESH context. A fresh one matters --
    /// the consuming context's change tracker would happily report whatever it last wrote.
    /// </remarks>
    [SqlServerFact]
    public async Task ConsumeAsync_InsideARolledBackTransaction_LeavesTheWithdrawalAuthorisationSpendable()
    {
        var client = CreateSqlClient();
        var (token, accountId) = await RegisterFundedAsync(client, funding: 100m);
        var authorizationId = await MintWithdrawalAsync(client, token, accountId, Amount);

        Guid userId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            userId = await db.StepUpAuthorizations.AsNoTracking()
                .Where(a => a.Id == authorizationId).Select(a => a.UserId).SingleAsync();
        }

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpAuthorizationService>();

            await using var transaction = await db.Database.BeginTransactionAsync();
            await stepUp.ConsumeAsync(userId, authorizationId, Guid.CreateVersion7());

            // The withdrawal this consumption belonged to fails after the row was marked spent.
            await transaction.RollbackAsync();
        }

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var authorization = await db.StepUpAuthorizations.AsNoTracking()
                .SingleAsync(a => a.Id == authorizationId);

            authorization.Status.Should().Be(StepUpAuthorizationStatus.Pending,
                "a rolled-back withdrawal must leave the authorisation spendable -- otherwise the "
                + "user has to prove their PIN again for money that never moved");
            authorization.ConsumedAt.Should().BeNull();
            authorization.ConsumedByTransactionId.Should().BeNull();
        }

        // And it really is still spendable, not merely Pending on paper.
        var response = await WithdrawAsync(
            client, token, accountId, Amount, authorizationId, Guid.NewGuid());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Dispose();
    }

    /// <summary>
    /// 3.E.3 — when the consume matches zero rows, the WHOLE withdrawal rolls back: the ledger row,
    /// the balance and the <c>MoneyWithdrawn</c> audit row.
    /// </summary>
    /// <remarks>
    /// This is the one ADR-0056 D6 asserts and nothing proved. The race is lost BETWEEN the
    /// validation and the spend: the interceptor consumes the authorisation from a second
    /// connection on the withdrawal's own <c>UPDATE [Accounts]</c> -- the balance decrement, issued
    /// after <c>ValidateAsync</c> passed and before <c>ConsumeAsync</c> runs. The same seam the
    /// closure uses, and the withdrawal emits that statement for its own reason.
    /// </remarks>
    [SqlServerFact]
    public async Task WhenTheConsumeMatchesZeroRows_TheWithdrawalIsRolledBackToo()
    {
        var client = CreateSqlClient();
        var (token, accountId) = await RegisterFundedAsync(client, funding: 100m);
        var authorizationId = await MintWithdrawalAsync(client, token, accountId, Amount);

        var before = await BalanceOfAsync(client, token, accountId);

        /*
          SCOPED TO THIS ACTOR, and the first version of this test was not. The SQL proofs share one
          database, so an unscoped `MoneyWithdrawn` count sees the row that the eight-way proof in
          this same class legitimately commits, and the assertion below failed reporting 1 where the
          rollback had in fact left 0. The closure's equivalent scopes by ActorUserId for exactly
          this reason; copying its shape without its filter is what produced a red that looked like
          a product defect and was a test defect.
        */
        Guid userId;
        using (var lookup = _factory!.Services.CreateScope())
        {
            var db0 = lookup.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            userId = await db0.StepUpAuthorizations.AsNoTracking()
                .Where(a => a.Id == authorizationId).Select(a => a.UserId).SingleAsync();
        }

        var race = new OutOfBandStepUpConsumeInterceptor(
            SqlServerFactAttribute.ConnectionString!, authorizationId);
        _factory!.AddInterceptor(race);
        race.Arm();

        var response = await WithdrawAsync(
            client, token, accountId, Amount, authorizationId, Guid.NewGuid());

        race.Fired.Should().BeTrue(
            "the out-of-band consume must actually have run, else the test proves nothing");
        race.OutOfBandRowsAffected.Should().Be(1,
            "and it must have won: the row was still Pending when it ran");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.AuthorizationInvalid);
        response.Dispose();

        // A FRESH scope, so the answer comes from SQL Server and not from a tracker that just
        // rolled back.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        (await BalanceOfAsync(client, token, accountId)).Should().Be(before,
            "the balance was decremented before the spend and must have rolled back with it");

        (await db.Transactions.AsNoTracking()
                .CountAsync(x => x.AccountId == accountId && x.Type == TransactionType.Withdrawal))
            .Should().Be(0, "the ledger row rode the same transaction and must be gone with it");

        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == userId && e.Event == SecurityEvents.MoneyWithdrawn))
            .Should().Be(0, "and so did the success row -- ADR-0044 D1 keeps them one unit");

        var verification = await chain.VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().BeGreaterThan(0, "an empty read also reports intact");

        _output.WriteLine(
            $"consume matched 0 rows -> {(int)response.StatusCode}, balance {before} unchanged, "
            + $"chain intact over {verification.Verified} rows");
    }

    /// <summary>
    /// 3.E.4 — Case B: a transient fault does not withdraw twice.
    /// </summary>
    /// <remarks>
    /// THE PRODUCTION WIRING. <c>EnableRetryOnFailure</c> re-runs the delegate on a transient fault
    /// against the same DbContext. Without the re-entrancy discipline at the top of it, the retry
    /// would find the balance already decremented in memory and the first attempt's Added
    /// AuditEvent still tracked, and would commit a second <c>MoneyWithdrawn</c> row for one
    /// withdrawal. The fault is injected on the withdrawal's own SaveChanges batch -- the one
    /// carrying the INSERT INTO [AuditEvents] -- so it hits after the delegate has mutated state
    /// and before anything committed.
    /// </remarks>
    [SqlServerFact]
    public async Task ATransientFault_DoesNotWithdrawTwice()
    {
        var fault = new TransientFailureInterceptor("INSERT INTO [AuditEvents]");
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.EnableSqlRetryOnFailure();
        var client = _factory.CreateClient();

        var (token, accountId) = await RegisterFundedAsync(client, funding: 100m);
        var authorizationId = await MintWithdrawalAsync(client, token, accountId, Amount);

        var before = await BalanceOfAsync(client, token, accountId);

        // Scoped to this actor for the same reason 3.E.3 is: the SQL proofs share one database, so
        // an unscoped MoneyWithdrawn count sees every withdrawal this class legitimately commits.
        Guid userId;
        using (var lookup = _factory.Services.CreateScope())
        {
            var db0 = lookup.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            userId = await db0.StepUpAuthorizations.AsNoTracking()
                .Where(a => a.Id == authorizationId).Select(a => a.UserId).SingleAsync();
        }

        // Registered only now: the PIN enrolment and the funding deposit above also insert audit
        // rows, and the one-shot fault must be spent by the withdrawal, not by those.
        _factory.AddInterceptor(fault);

        var response = await WithdrawAsync(
            client, token, accountId, Amount, authorizationId, Guid.NewGuid());

        fault.Fired.Should().BeTrue(
            "the transient must actually have been injected, else the test proves nothing");
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the injected transient must be absorbed by the execution strategy, not surfaced");
        response.Dispose();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == userId && e.Event == SecurityEvents.MoneyWithdrawn))
            .Should().Be(1, "one withdrawal, one row: the retry must not re-enlist the failed "
                + "attempt's row");

        (await db.Transactions.AsNoTracking()
                .CountAsync(x => x.AccountId == accountId && x.Type == TransactionType.Withdrawal))
            .Should().Be(1, "and one ledger row");

        (await BalanceOfAsync(client, token, accountId)).Should().Be(before - Amount,
            "the money moved exactly once, not twice");

        var authorization = await db.StepUpAuthorizations.AsNoTracking()
            .SingleAsync(a => a.Id == authorizationId);
        authorization.Status.Should().Be(StepUpAuthorizationStatus.Consumed,
            "spent by the attempt that committed");
        authorization.ConsumedByTransactionId.Should().NotBeNull(
            "and it names the ledger row, unlike a closure's null (ADR-0056 D7)");

        var verification = await chain.VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);

        _output.WriteLine(
            $"transient absorbed -> {(int)response.StatusCode}, one MoneyWithdrawn row, "
            + "chain intact");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<(string Token, Guid AccountId)> RegisterFundedAsync(
        HttpClient client, decimal funding)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"wdraw_{unique}",
            Email = $"wdraw{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "With",
            LastName = "Draw"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var token = registered!.Data!.Token.AccessToken;
        var accountId = registered.Data.Account.Id;

        await SendAsync(client, token, HttpMethod.Post, "/api/auth/pin",
            new SetPinRequest { Pin = Pin, Password = "TestPass123!" });

        using var deposit = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = funding, Description = "funding" },
                options: Json)
        };
        deposit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        deposit.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        (await client.SendAsync(deposit)).EnsureSuccessStatusCode();

        return (token, accountId);
    }

    private static async Task<Guid> MintWithdrawalAsync(
        HttpClient client, string token, Guid accountId, decimal amount)
    {
        var response = await SendAsync(
            client, token, HttpMethod.Post, "/api/transactions/withdraw/authorizations",
            new WithdrawalAuthorizationRequest { AccountId = accountId, Amount = amount, Pin = Pin });
        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json);
        return body!.Data!.AuthorizationId;
    }

    private static Task<HttpResponseMessage> WithdrawAsync(
        HttpClient client, string token, Guid accountId, decimal amount,
        Guid authorizationId, Guid idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/withdraw")
        {
            Content = JsonContent.Create(new WithdrawRequest
            {
                AccountId = accountId,
                Amount = amount,
                Description = "step-up proof"
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, idempotencyKey.ToString());
        request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
        return client.SendAsync(request);
    }

    private static async Task<decimal> BalanceOfAsync(HttpClient client, string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{accountId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<AzureBank.Shared.DTOs.Account.AccountResponse>>(Json);
        return body!.Data!.Balance;
    }

    private static Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, string token, HttpMethod method, string url, T payload)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    public void Dispose() => _factory?.Dispose();
}
