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
/// An external transfer whose payee account is closed, before the transfer or while it runs, pays
/// another open account of the same payee: the primary first, then the oldest, then the lowest id.
/// </summary>
/// <remarks>
/// <para>
/// A payer names a handle, and which account receives is the server's choice. The transfer
/// resolves that account once, at its first look, and looks at it again after every reload. When
/// it comes back closed, or its row is gone, the transfer chooses again among the open accounts of
/// the same payee and goes on: 201, the sender debited once, the account chosen credited once,
/// the closed account untouched. Both looks read one list in one order. Only a payee with no open
/// account is refused (<see cref="TransferOrWithdrawalOnClosedAccountSqlServerTests"/> holds that
/// 422).
/// </para>
/// <para>
/// The API closes an account only when it is empty and not the primary one, and a transfer
/// resolves the primary one. So the payee account a real closure can close under a transfer is one
/// its owner first replaced as primary. The control builds that state through the endpoints, with
/// no race; the two races beside it write the same columns in one step, beside the request, and
/// all three are held to one statement of the rows. The other proofs write rows the API does not
/// produce: a closed account still marked primary, two accounts opened at one instant, a row
/// deleted outright. They hold the order among accounts of which none is primary, and what the
/// transfer does when there is no row to read.
/// </para>
/// <para>
/// After a lost save the transfer looks at the payee account in two places, at the top of its loop
/// and inside the attempt, and the first of the two makes the choice. The reads these proofs count
/// are the same whichever of the two makes it: they hold that the choice is made and what it reads,
/// not which place made it.
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
    /// <summary>The text of the read that lists a payee's open accounts in the order they are paid.</summary>
    private const string TheChoice = "ORDER BY [a].[IsPrimary] DESC";

    private static readonly Expected SenderDebited = new(
        new AccountRow(false, Funding - Amount, 2), $"{TransactionType.Deposit}+{TransactionType.TransferOut}");

    private static readonly Expected Paid = new(new AccountRow(false, Amount, 1), $"{TransactionType.TransferIn}");
    private static readonly Expected ClosedAndUntouched = new(new AccountRow(true, 0m, 0), string.Empty);
    private static readonly Expected OpenAndUntouched = new(new AccountRow(false, 0m, 0), string.Empty);
    private static readonly Expected RowGone = new(null, "gone");

    private readonly ITestOutputHelper _output = output;

    /// <summary>What the request had sent when a second closure fired, read before the observation.</summary>
    private OutOfBandClosureInterceptor.Sent? _secondClosure;

    /// <summary>One account as a proof expects it: its row, and the types of its ledger rows in order.</summary>
    private sealed record Expected(AccountRow? Row, string Ledger);

    // ── The payee replaced the primary account and closed the old one ────────

    [SqlServerFact]
    public async Task Control_TransferToAPayeeWhoMadeAnotherAccountPrimaryAndClosedTheOld_PaysTheNewPrimary()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppc");
        var payee = await RegisterWithPinAsync(client, "ppcp");
        var olderSpare = await CreateSpareAsync(client, payee);
        var newPrimary = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);

        // Through the endpoints, as a user does it: the spare becomes primary, then the PIN mints
        // an authorisation and the DELETE closes the old primary account.
        await MakePrimaryThroughTheApiAsync(client, payee, newPrimary);
        await CloseThroughTheApiAsync(client, payee, payee.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race: null,
            sender.PrimaryAccountId, payee.PrimaryAccountId, olderSpare, newPrimary);

        using var scope = new AssertionScope();
        ShouldHavePaidTheNewPrimary(seen);
        ShouldBeTheStoredAnswer(again, seen);
    }

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

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, olderSpare, newPrimary);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen.Sent!, savesAfterwards: 1, rowsWritten: 2, readsAfterwards: 7);
        ShouldHavePaidTheNewPrimary(seen);
        ShouldBeTheStoredAnswer(again, seen);
    }

    [SqlServerFact]
    public async Task Closure_OfThePayeeOldPrimaryBeforeTheTransferFirstReload_PaysTheNewPrimary()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppf");
        var payee = await RegisterAsync(client, "ppfp");
        var olderSpare = await CreateSpareAsync(client, payee);
        var newPrimary = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        /*
          The first look loaded every account of the payee, so the one chosen here is in the
          tracker with the RowVersion it had before it became primary. One save afterwards, and
          not two: the account chosen is read from the store before the attempt goes on, so its
          save does not lose to the write that made it primary.
        */
        var race = ArmClosureOf(payee.PrimaryAccountId, trigger: AuthorisationRead, newPrimaryId: newPrimary);

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, olderSpare, newPrimary);

        using var scope = new AssertionScope();
        ShouldHaveClosedBeforeTheFirstReload(race, seen, readsAfterwards: 6, savesAfterwards: 1, rowsWritten: 2);
        ShouldHavePaidTheNewPrimary(seen);
        ShouldBeTheStoredAnswer(again, seen);
    }

    // ── The payee only replaced the primary account ──────────────────────────

    [SqlServerFact]
    public async Task Collision_TransferRacingThePayeeMakingAnotherAccountPrimary_StillPaysTheAccountItChose()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppk");
        var payee = await RegisterAsync(client, "ppkp");
        var newPrimary = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        /*
          The write a change of primary makes, and no closure: on the transfer's own save the
          account it chose stops being the primary one and stays open. The save loses, the
          reload hands the account back open, and it is paid. Only a closed or missing account
          is replaced, so no list of the payee's accounts is read.
        */
        var race = ArmClosureOf(payee.PrimaryAccountId, newPrimaryId: newPrimary, keepOpen: true);

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, newPrimary);

        using var scope = new AssertionScope();
        race.Fired.Should().BeTrue("the change of primary must actually have run");
        race.OutOfBandRowsAffected.Should().Be(2, "one account stops being primary and one becomes it");
        seen.Sent!.RodeAnAccountUpdate.Should().BeTrue("the change lands on the transfer's own save");
        seen.Sent.AccountUpdatesBefore.Should().Be(1, "that save is the first the transfer sends");
        seen.Sent.AccountReadsAfter.Should().Be(
            5, "the accounts are reloaded, and no list of the payee's is read");
        seen.Sent.AccountUpdatesAfter.Should().Be(1, "the save that lost is sent once more");
        seen.Primary.Should().Equal(
            new bool?[] { true, false, true }, "the account paid is no longer the primary one");
        ShouldHavePaidOnce(seen, SenderDebited, Paid, OpenAndUntouched);
        ShouldBeTheStoredAnswer(again, seen);
    }

    // ── No open account of the payee is primary: the order among the others ──

    [SqlServerFact]
    public async Task Control_TransferToAPayeeWhosePrimaryRowIsClosed_PaysTheOldestOpenAccountAtTheFirstLook()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppq");
        var payee = await RegisterAsync(client, "ppqp");
        var oneSpare = await CreateSpareAsync(client, payee);
        var otherSpare = await CreateSpareAsync(client, payee);
        var (oldest, newer) = await MakeTheOneThatSortsLastByIdTheOldestAsync(oneSpare, otherSpare);
        await FundAsync(client, sender, sender.PrimaryAccountId);

        // Only a direct write closes a primary account. It is closed before the mint, so the
        // first look of the mint and of the transfer both meet a payee with no open primary.
        (await OutOfBandClosureInterceptor.CloseAsync(
            SqlServerFactAttribute.ConnectionString!, payee.PrimaryAccountId)).Should().Be(1);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race: null,
            sender.PrimaryAccountId, payee.PrimaryAccountId, oldest, newer);

        using var scope = new AssertionScope();
        seen.Primary.Should().Equal(
            new bool?[] { true, true, false, false }, "the closed account keeps its mark and no open one has it");
        ShouldHavePaidOnce(seen, SenderDebited, ClosedAndUntouched, Paid, OpenAndUntouched);
        ShouldBeTheStoredAnswer(again, seen);
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

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, oldest, newer);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen.Sent!, savesAfterwards: 1, readsAfterwards: 7);
        seen.Primary.Should().Equal(
            new bool?[] { true, true, false, false }, "the closed account keeps its mark and no open one has it");
        ShouldHavePaidOnce(seen, SenderDebited, ClosedAndUntouched, Paid, OpenAndUntouched);
        ShouldBeTheStoredAnswer(again, seen);
    }

    [SqlServerFact]
    public async Task Collision_TransferRacingTheClosureOfThePrimary_WithTwoAccountsOpenedAtOneInstant_PaysTheLowestId()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppt");
        var payee = await RegisterAsync(client, "pptp");
        var oneSpare = await CreateSpareAsync(client, payee);
        var otherSpare = await CreateSpareAsync(client, payee);
        var (first, second) = await GiveBothOneOpeningInstantAsync(oneSpare, otherSpare);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        var sent = new CommandRecordingInterceptor();
        AddInterceptor(sent);
        var race = ArmClosureOf(payee.PrimaryAccountId);
        sent.Start();

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, first, second);
        sent.Stop();

        /*
          Between two accounts opened at one instant the store is free to return either first,
          unless the id is the last key of the order. On a table this small it returns them by id
          with or without that key, so the rows alone do not hold it: the text of the choice, as
          it reached the server, is read too.
        */
        var choices = sent.Selects.Where(c => c.Contains(TheChoice, StringComparison.Ordinal)).ToList();
        _output.WriteLine(
            $"the choice was sent {choices.Count} times, each ending: "
            + string.Join(" | ", choices.Select(c => c[c.IndexOf(TheChoice, StringComparison.Ordinal)..].Trim())));

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen.Sent!, savesAfterwards: 1, readsAfterwards: 7);
        ShouldHavePaidOnce(seen, SenderDebited, ClosedAndUntouched, Paid, OpenAndUntouched);
        ShouldBeTheStoredAnswer(again, seen);
        choices.Should().HaveCount(2, "the first look and the look after the lost save");
        choices.Should().OnlyContain(
            c => c.TrimEnd().EndsWith($"{TheChoice}, [a].[CreatedAt], [a].[Id]", StringComparison.Ordinal),
            "the primary first, then the oldest, then the lowest id: the id makes the order total");
    }

    // ── The account chosen closes in its turn ────────────────────────────────

    [SqlServerFact]
    public async Task Collision_TheAccountChosenClosesBeforeItIsReadFresh_PaysTheNextOpenAccount()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "ppn");
        var payee = await RegisterAsync(client, "ppnp");
        var oneSpare = await CreateSpareAsync(client, payee);
        var otherSpare = await CreateSpareAsync(client, payee);
        var (oldest, newer) = await MakeTheOneThatSortsLastByIdTheOldestAsync(oneSpare, otherSpare);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        /*
          Two closures, both direct writes. The first closes the primary account on the transfer's
          own save. The second closes the oldest spare on the first command that reads that
          account by its own id: the transfer has only listed it until then, so that command is
          the fresh read of the account just chosen, and the closure lands between the list and
          it. The list still holds the account as open; the fresh read does not.
        */
        var race = ArmClosureOf(payee.PrimaryAccountId);
        var second = ArmClosureOf(oldest, trigger: OutOfBandClosureInterceptor.AccountRead, onTheReadOf: oldest);

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race, second,
            sender.PrimaryAccountId, payee.PrimaryAccountId, oldest, newer);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen.Sent!, savesAfterwards: 1, readsAfterwards: 8);
        second.Fired.Should().BeTrue("the second closure must actually have run");
        second.OutOfBandRowsAffected.Should().Be(1, "and it must have met the chosen account still open");
        _secondClosure!.RodeAnAccountUpdate.Should().BeFalse(
            "it lands on a read, the fresh read of the account chosen");
        _secondClosure.AccountUpdatesBefore.Should().Be(1, "after the save that lost to the first closure");
        _secondClosure.AccountUpdatesAfter.Should().Be(
            1, "and before the one save that pays the next account");
        ShouldHavePaidOnce(seen, SenderDebited, ClosedAndUntouched, ClosedAndUntouched, Paid);
        ShouldBeTheStoredAnswer(again, seen);
    }

    // ── The row of the payee's account is gone ───────────────────────────────

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

        var (seen, again) = await TransferTwiceAsync(
            client, sender, payee, authorizationId, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, spare);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen.Sent!, savesAfterwards: 1, readsAfterwards: 7);
        ShouldHavePaidOnce(seen, SenderDebited, RowGone, Paid);
        ShouldBeTheStoredAnswer(again, seen);
    }

    /// <summary>
    /// Sends the transfer, reads the answer and the database, then sends the same key and bytes
    /// again and reads both once more. A second closure, when there is one, is written to the test
    /// output with the first answer.
    /// </summary>
    private async Task<(Observed Seen, Observed Again)> TransferTwiceAsync(
        HttpClient client, TestUser sender, TestUser payee, Guid authorizationId,
        OutOfBandClosureInterceptor? race, OutOfBandClosureInterceptor? second, params Guid[] accounts)
    {
        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);

        // Read now: the reads of the observation below go through the same interceptor.
        var secondSent = second?.Seen;
        _secondClosure = secondSent;
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race, accounts);
        if (second is not null)
        {
            _output.WriteLine(
                $"second closure: Fired: {second.Fired}, OutOfBandRowsAffected: {second.OutOfBandRowsAffected}, "
                + $"{secondSent}");
        }

        var again = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var after = await ObserveAsync(
            again, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null, accounts);

        return (seen, after);
    }

    private Task<(Observed Seen, Observed Again)> TransferTwiceAsync(
        HttpClient client, TestUser sender, TestUser payee, Guid authorizationId,
        OutOfBandClosureInterceptor? race, params Guid[] accounts) =>
        TransferTwiceAsync(client, sender, payee, authorizationId, race, second: null, accounts);

    /// <summary>
    /// The state a payee leaves by making a spare the primary account and closing the old one, and
    /// a transfer paid into it. One statement for the control, which builds the state through the
    /// endpoints, and for the two races, which write it beside the request: the accounts are the
    /// sender's, the old primary, a spare older than the new primary, and the new primary.
    /// </summary>
    private static void ShouldHavePaidTheNewPrimary(Observed seen)
    {
        seen.Primary.Should().Equal(
            new bool?[] { true, false, false, true }, "the closed account is not primary, the new one is");
        ShouldHavePaidOnce(seen, SenderDebited, ClosedAndUntouched, OpenAndUntouched, Paid);
    }

    /// <summary>
    /// The transfer answered 201 and wrote one movement: the first account debited, one credited,
    /// and on every account the ledger rows named, by type and in order.
    /// </summary>
    private static void ShouldHavePaidOnce(Observed seen, params Expected[] accounts)
    {
        seen.Status.Should().Be(HttpStatusCode.Created, "the payee holds an open account, and is paid on it");
        seen.Replayed.Should().BeFalse();
        seen.Accounts.Should().Equal(accounts.Select(a => a.Row));
        seen.Ledgers.Should().Equal(accounts.Select(a => a.Ledger));
        seen.MoneyAuditRows.Should().Be(1, "one transfer, one audit row");
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed);
        seen.ConsumedAt.Should().NotBeNull("an authorisation that is spent says when");
        seen.IdempotencyRecords.Should().Equal(IdempotencyStatus.Completed);
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>The same key and bytes sent again: the stored 201, byte for byte, and nothing moves a second time.</summary>
    private static void ShouldBeTheStoredAnswer(Observed again, Observed first)
    {
        again.Status.Should().Be(HttpStatusCode.Created);
        again.Replayed.Should().BeTrue("the transfer stored its answer, and the key replays it");
        again.Body.Should().Be(first.Body, "the answer replayed is the answer first given");
        again.Accounts.Should().Equal(first.Accounts, "a replay moves nothing");
        again.Ledgers.Should().Equal(first.Ledgers);
        again.MoneyAuditRows.Should().Be(1);
        again.ConsumedAt.Should().Be(first.ConsumedAt, "and spends nothing a second time");
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

    /// <summary>
    /// Gives two accounts one opening instant, and returns them in the order the store sorts their
    /// ids: SQL Server and .NET do not order a <c>uniqueidentifier</c> the same way, so the order
    /// is asked of the store.
    /// </summary>
    private static async Task<(Guid First, Guid Second)> GiveBothOneOpeningInstantAsync(Guid one, Guid other)
    {
        await using var connection = new SqlConnection(SqlServerFactAttribute.ConnectionString!);
        await connection.OpenAsync();

        await using var level = connection.CreateCommand();
        level.CommandText =
            "UPDATE [Accounts] SET [CreatedAt] = (SELECT [CreatedAt] FROM [Accounts] WHERE [Id] = @one) "
            + "WHERE [Id] = @other";
        level.Parameters.Add(new SqlParameter("@one", one));
        level.Parameters.Add(new SqlParameter("@other", other));
        (await level.ExecuteNonQueryAsync()).Should().Be(1);

        await using var instants = connection.CreateCommand();
        instants.CommandText = "SELECT COUNT(DISTINCT [CreatedAt]) FROM [Accounts] WHERE [Id] IN (@one, @other)";
        instants.Parameters.Add(new SqlParameter("@one", one));
        instants.Parameters.Add(new SqlParameter("@other", other));
        ((int)(await instants.ExecuteScalarAsync())!).Should().Be(1, "the two accounts must hold one opening instant");

        await using var order = connection.CreateCommand();
        order.CommandText = "SELECT TOP (1) [Id] FROM [Accounts] WHERE [Id] IN (@one, @other) ORDER BY [Id]";
        order.Parameters.Add(new SqlParameter("@one", one));
        order.Parameters.Add(new SqlParameter("@other", other));
        var first = (Guid)(await order.ExecuteScalarAsync())!;

        return (first, first == one ? other : one);
    }
}
