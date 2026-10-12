using System.Net;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A transfer whose save failed tries again from the balance the database holds, not from the one
/// the failed attempt left in memory.
/// </summary>
/// <remarks>
/// <para>
/// An attempt debits the tracked account before it saves. When the save fails and the transaction
/// rolls back, the database still holds the whole balance and the tracked row holds what was left
/// after the debit. The next turn of the loop checks the funds before anything else, so it must
/// read the accounts again first (<c>ConcurrencyRetry.PrepareNextAttemptAsync</c>).
/// </para>
/// <para>
/// MORE THAN HALF THE BALANCE, ON PURPOSE. A transfer of less than half still fits in what the
/// failed attempt left, and passes the funds check on the wrong balance: 5 out of 20 would pass
/// with or without the reload. 15 out of 20 leaves 5 in memory, and without the reload the second
/// turn answers 422 <c>INSUFFICIENT_FUNDS</c> for an account that holds 20.
/// </para>
/// <para>
/// Two failures are injected, one save each: a transaction number that already exists, which SQL
/// Server refuses on <c>IX_Transactions_TransactionNumber</c>, and a source row another write moved,
/// which the save loses on its <c>RowVersion</c>. Neither exists on the in-memory provider.
/// <c>TransactionNumberCollisionRecoverySqlServerTests</c> holds the collision for a deposit, and
/// <c>PayeeAccountClosedMeanwhileSqlServerTests</c> the lost save for an external transfer.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class TransferRetryFromTheStoredBalanceSqlServerTests(ITestOutputHelper output)
    : ClosedAccountSqlServerProofs(output)
{
    /// <summary>More than half of <see cref="ClosedAccountSqlServerProofs.Funding"/>.</summary>
    private const decimal MostOfTheBalance = 15m;

    private static readonly AccountRow DebitedOnce = new(false, Funding - MostOfTheBalance, 2);
    private static readonly AccountRow CreditedOnce = new(false, MostOfTheBalance, 1);

    private readonly ITestOutputHelper _output = output;

    [SqlServerFact]
    public async Task AnExternalTransferOfMostOfTheBalance_WhoseNumberCollides_IsMadeOnce()
    {
        var client = CreateSqlClient();
        var payer = await RegisterWithPinAsync(client, "tc");
        var payee = await RegisterAsync(client, "tp");
        await FundAsync(client, payer, payer.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(
            client, payer, payer.PrimaryAccountId, payee.AzureTag, MostOfTheBalance);
        var key = Guid.NewGuid();

        var collision = await CollideOnceWithTheFundingRowAsync(payer.PrimaryAccountId);
        using var response = await TransferAsync(
            client, payer, payer.PrimaryAccountId, payee.AzureTag, authorizationId, key, MostOfTheBalance);
        var seen = await ObserveAsync(
            response, payer, SecurityEvents.MoneyTransferred, authorizationId, key, race: null,
            payer.PrimaryAccountId, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        collision.Fired.Should().BeTrue("the proof is void unless the duplicate number was sent");
        ShouldHaveBeenMadeOnce(seen);
    }

    [SqlServerFact]
    public async Task AnInternalTransferOfMostOfTheBalance_WhoseNumberCollides_IsMadeOnce()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "ic");
        var destination = await CreateSpareAsync(client, user);
        await FundAsync(client, user, user.PrimaryAccountId);
        var authorizationId = await MintInternalAsync(client, user, destination);
        var key = Guid.NewGuid();

        var collision = await CollideOnceWithTheFundingRowAsync(user.PrimaryAccountId);
        using var response = await SendInternalAsync(client, user, destination, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race: null,
            user.PrimaryAccountId, destination);

        using var scope = new AssertionScope();
        collision.Fired.Should().BeTrue("the proof is void unless the duplicate number was sent");
        ShouldHaveBeenMadeOnce(seen);
    }

    [SqlServerFact]
    public async Task AnInternalTransferOfMostOfTheBalance_ThatLosesOneSave_IsMadeOnTheNextAttempt()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterWithPinAsync(client, "il");
        var destination = await CreateSpareAsync(client, user);
        await FundAsync(client, user, user.PrimaryAccountId);
        var authorizationId = await MintInternalAsync(client, user, destination);
        var key = Guid.NewGuid();

        race.Arm(user.PrimaryAccountId, saves: 1);
        using var response = await SendInternalAsync(client, user, destination, authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race: null,
            user.PrimaryAccountId, destination);

        _output.WriteLine($"saves of the source made to lose: {race.Fired}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(1, "the proof is void unless exactly one save lost the source's row");
        ShouldHaveBeenMadeOnce(seen);
    }

    /// <summary>
    /// 201, the source debited once and the other account credited once, one pair of ledger rows
    /// beside the funding deposit, one audit row, the authorisation spent and the key completed.
    /// </summary>
    private static void ShouldHaveBeenMadeOnce(Observed seen)
    {
        seen.Status.Should().Be(
            HttpStatusCode.Created,
            $"the account holds the amount, and a save that failed is tried again; the body was: {seen.Body}");
        seen.Accounts.Should().Equal(
            [DebitedOnce, CreditedOnce], "each balance moves once, and each account gets one row of the pair");
        seen.Ledgers.Should().Equal(
            $"{TransactionType.Deposit}+{TransactionType.TransferOut}", $"{TransactionType.TransferIn}");
        seen.MoneyAuditRows.Should().Be(1, "one transfer was made, however many attempts it took");
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed);
        seen.ConsumedAt.Should().NotBeNull();
        seen.IdempotencyRecords.Should().Equal([IdempotencyStatus.Completed]);
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>
    /// From now on, the first ledger row a request inserts carries the number of the deposit that
    /// funded <paramref name="accountId"/>: one collision, and the insert after it is left alone.
    /// </summary>
    private async Task<DuplicateTransactionNumberInterceptor> CollideOnceWithTheFundingRowAsync(Guid accountId)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var existingNumber = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .Select(t => t.TransactionNumber)
            .SingleAsync();

        var collision = new DuplicateTransactionNumberInterceptor(existingNumber);
        AddInterceptor(collision);
        return collision;
    }

    private static Task<Guid> MintInternalAsync(HttpClient client, TestUser user, Guid destination) =>
        MintAsync(client, user, "/api/transfers/internal/authorizations", new InternalTransferAuthorizationRequest
        {
            FromAccountId = user.PrimaryAccountId,
            ToAccountId = destination,
            Amount = MostOfTheBalance,
            Pin = Pin,
        });

    private static Task<HttpResponseMessage> SendInternalAsync(
        HttpClient client, TestUser user, Guid destination, Guid authorizationId, Guid key) =>
        SendAsync(client, user, HttpMethod.Post, "/api/transfers/internal", new InternalTransferRequest
        {
            FromAccountId = user.PrimaryAccountId,
            ToAccountId = destination,
            Amount = MostOfTheBalance,
            Description = "retry from the stored balance",
        }, authorizationId, key);
}
