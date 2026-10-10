using System.Net;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// An external transfer whose payee account closes while the transfer runs pays another open
/// account of the same payee: the primary first, or else the oldest.
/// </summary>
/// <remarks>
/// <para>
/// A payer names a handle, and which account receives is the server's choice. The transfer
/// resolves that account once, at its first look, and looks at it again after every reload. When
/// it comes back closed, or its row is gone, the transfer chooses again among the open accounts of
/// the same payee and goes on: 201, the sender debited once, the account chosen credited once,
/// the closed account untouched. Only a payee with no open account is refused
/// (<see cref="TransferOrWithdrawalOnClosedAccountSqlServerTests"/> holds that 422).
/// </para>
/// <para>
/// The API closes an account only when it is empty and not the primary one, and a transfer
/// resolves the primary one. So the payee account a real closure can close under a transfer is one
/// its owner first replaced as primary: the first two proofs write exactly those columns, in one
/// step, beside the request. The other two write rows the API does not produce, a closed account
/// still marked primary and a row deleted outright, and hold what the transfer does when no open
/// account is primary and when there is no row to read.
/// </para>
/// <para>
/// Gated by AZUREBANK_TEST_SQLSERVER and serialised with the other SQL proofs: the closure is
/// written by a raw <c>SqlConnection</c> beside the request, and the races at the save need a
/// <c>rowversion</c> to lose.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PayeeAccountClosedMeanwhileSqlServerTests(ITestOutputHelper output)
    : ClosedAccountSqlServerProofs(output)
{
    [SqlServerFact]
    public async Task Collision_TransferRacingThePayeeMakingAnotherAccountPrimaryAndClosingTheOld_PaysTheNewPrimary()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "pps");
        var payee = await RegisterAsync(client, "ppsp");
        // Opened before the account that becomes primary: the oldest open account of the payee
        // after the closure, and still not the one paid, because a primary account comes first.
        var olderSpare = await CreateSpareAsync(client, payee);
        var newPrimary = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        var race = ArmClosureOf(payee.PrimaryAccountId, newPrimaryId: newPrimary);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, olderSpare, newPrimary);

        var again = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var after = await ObserveAsync(
            again, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null,
            sender.PrimaryAccountId, payee.PrimaryAccountId, olderSpare, newPrimary);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 1, rowsWritten: 2);
        seen.Primary.Should().Equal(
            new bool?[] { true, false, false, true }, "the closed account is not primary, the new one is");
        ShouldHavePaidOnce(
            seen,
            new AccountRow(false, Funding - Amount, 2),
            new AccountRow(true, 0m, 0),
            new AccountRow(false, 0m, 0),
            new AccountRow(false, Amount, 1));
        seen.Ledgers.Should().Equal(
            $"{TransactionType.Deposit}+{TransactionType.TransferOut}", string.Empty, string.Empty,
            $"{TransactionType.TransferIn}");
        ShouldBeTheStoredAnswer(after, seen);
    }

    [SqlServerFact]
    public async Task Closure_OfThePayeeOldPrimaryBeforeTheTransferFirstReload_PaysTheNewPrimary()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppf");
        var payee = await RegisterAsync(client, "ppfp");
        var newPrimary = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        /*
          The first look loaded both accounts of the payee, so the one chosen here is in the
          tracker with the RowVersion it had before it became primary. One save afterwards, and
          not two: the account chosen is read from the store before the attempt goes on, so its
          save does not lose to the write that made it primary.
        */
        var race = ArmClosureOf(payee.PrimaryAccountId, trigger: AuthorisationRead, newPrimaryId: newPrimary);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, newPrimary);

        var again = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var after = await ObserveAsync(
            again, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null,
            sender.PrimaryAccountId, payee.PrimaryAccountId, newPrimary);

        using var scope = new AssertionScope();
        ShouldHaveClosedBeforeTheFirstReload(race, seen, savesAfterwards: 1, rowsWritten: 2);
        seen.Primary.Should().Equal(new bool?[] { true, false, true });
        ShouldHavePaidOnce(
            seen,
            new AccountRow(false, Funding - Amount, 2),
            new AccountRow(true, 0m, 0),
            new AccountRow(false, Amount, 1));
        seen.Ledgers.Should().Equal(
            $"{TransactionType.Deposit}+{TransactionType.TransferOut}", string.Empty,
            $"{TransactionType.TransferIn}");
        ShouldBeTheStoredAnswer(after, seen);
    }

    [SqlServerFact]
    public async Task Collision_TransferRacingTheClosureOfAPayeeAccountStillMarkedPrimary_PaysTheOldestOpenAccount()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppo");
        var payee = await RegisterAsync(client, "ppop");
        var oneSpare = await CreateSpareAsync(client, payee);
        var otherSpare = await CreateSpareAsync(client, payee);
        var (oldest, newer) = await MakeTheOneThatSortsLastByIdTheOldestAsync(oneSpare, otherSpare);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        /*
          Only a direct write produces this row: the API refuses to close a primary account, so a
          closed account still marked primary, beside open accounts of which none is primary, is
          not a state a user can reach. It is the one that holds the order among accounts that are
          not primary: the oldest, by the instant it was opened and not by its id.
        */
        var race = ArmClosureOf(payee.PrimaryAccountId);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, oldest, newer);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 1);
        seen.Primary.Should().Equal(
            new bool?[] { true, true, false, false }, "the closed account keeps its mark and no open one has it");
        ShouldHavePaidOnce(
            seen,
            new AccountRow(false, Funding - Amount, 2),
            new AccountRow(true, 0m, 0),
            new AccountRow(false, Amount, 1),
            new AccountRow(false, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_TransferRacingTheRemovalOfThePayeeAccountRow_PaysTheOpenAccount()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppg");
        var payee = await RegisterAsync(client, "ppgp");
        var spare = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        // The payee's account has no ledger row, so nothing refers to it and the DELETE goes
        // through. The reload that follows the lost save finds no row.
        var race = ArmClosureOf(payee.PrimaryAccountId, remove: true);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, spare);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 1);
        ShouldHavePaidOnce(
            seen,
            new AccountRow(false, Funding - Amount, 2),
            null,
            new AccountRow(false, Amount, 1));
    }

    /// <summary>The transfer answered 201 and wrote one movement: the first account debited, one credited.</summary>
    private static void ShouldHavePaidOnce(Observed seen, params AccountRow?[] accounts)
    {
        seen.Status.Should().Be(HttpStatusCode.Created, "the payee holds an open account, and is paid on it");
        seen.Replayed.Should().BeFalse();
        seen.Accounts.Should().Equal(accounts);
        seen.MoneyAuditRows.Should().Be(1, "one transfer, one audit row");
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed);
        seen.IdempotencyRecords.Should().Equal(IdempotencyStatus.Completed);
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>The same key and bytes sent again: the stored 201, and nothing moves a second time.</summary>
    private static void ShouldBeTheStoredAnswer(Observed again, Observed first)
    {
        again.Status.Should().Be(HttpStatusCode.Created);
        again.Replayed.Should().BeTrue("the transfer stored its answer, and the key replays it");
        again.Accounts.Should().Equal(first.Accounts, "a replay moves nothing");
        again.Ledgers.Should().Equal(first.Ledgers);
        again.MoneyAuditRows.Should().Be(1);
        again.IdempotencyRecords.Should().Equal(IdempotencyStatus.Completed);
        again.ChainIntact.Should().BeTrue(because: again.ChainReason);
    }

    /// <summary>
    /// Of two accounts, finds the one SQL Server sorts last by id and moves the instant it was
    /// opened one day back, so the oldest of the two is never also the first by id. Returns the
    /// pair as (oldest, newer).
    /// </summary>
    private static async Task<(Guid Oldest, Guid Newer)> MakeTheOneThatSortsLastByIdTheOldestAsync(Guid one, Guid other)
    {
        await using var connection = new SqlConnection(SqlServerFactAttribute.ConnectionString!);
        await connection.OpenAsync();

        await using var order = connection.CreateCommand();
        order.CommandText = "SELECT TOP (1) [Id] FROM [Accounts] WHERE [Id] IN (@one, @other) ORDER BY [Id] DESC";
        order.Parameters.Add(new SqlParameter("@one", one));
        order.Parameters.Add(new SqlParameter("@other", other));
        var last = (Guid)(await order.ExecuteScalarAsync())!;

        await using var age = connection.CreateCommand();
        age.CommandText = "UPDATE [Accounts] SET [CreatedAt] = DATEADD(day, -1, [CreatedAt]) WHERE [Id] = @id";
        age.Parameters.Add(new SqlParameter("@id", last));
        (await age.ExecuteNonQueryAsync()).Should().Be(1);

        return (last, last == one ? other : one);
    }
}
