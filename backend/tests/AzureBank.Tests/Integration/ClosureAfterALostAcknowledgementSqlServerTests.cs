using System.Net;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A write that emptied its account and committed, whose acknowledgement was lost, and whose
/// account was closed before the write was looked at again: the answer is the one of an applied
/// write, 409 <c>IDEMPOTENCY_RESULT_UNKNOWN</c> with <c>applied: true</c>, and never the 404 of a
/// closed account.
/// </summary>
/// <remarks>
/// <para>
/// The execution strategy runs the delegate again after a transient fault. That second run reloads
/// the account and then reads the claim under the idempotency key. Here the reload brings back a
/// closed account and the claim says the write was applied. The claim is read first: a 404 would
/// tell the client that nothing happened, about money that moved. This is what holds the place of
/// the closed-account check, after the idempotent preparation and not inside it.
/// </para>
/// <para>
/// THE CLOSURE IS THE BANK'S OWN. The write takes everything the account holds, so once it has
/// committed the account is empty and, being a spare, closable: as the acknowledgement is lost the
/// account's owner mints a closure with the PIN and sends the DELETE, through the endpoints, on
/// the same host. Nothing here is written to the store directly.
/// </para>
/// <para>
/// Gated by AZUREBANK_TEST_SQLSERVER and serialised with the other SQL proofs: the fault is at a
/// real transaction's commit, on a host that retries transient failures as production does.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class ClosureAfterALostAcknowledgementSqlServerTests(ITestOutputHelper output)
    : ClosedAccountSqlServerProofs(output)
{
    private const string AppliedDetail =
        "The operation sent with this idempotency key was applied, but this request cannot return "
        + "its result. Do not send it again with a new key: look for it with GET /api/transactions.";

    private readonly ITestOutputHelper _output = output;

    [SqlServerFact]
    public async Task AWithdrawalOfEverything_AcknowledgementLost_ThenItsAccountClosed_AnswersThatItWasApplied()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AfterCommit);
        var client = CreateSqlClient(fault);
        var user = await RegisterWithPinAsync(client, "lw");
        var account = await CreateSpareAsync(client, user);
        await FundAsync(client, user, account);
        var authorizationId = await MintWithdrawalAsync(client, user, account, Funding);

        var closed = false;
        fault.BeforeAcknowledgementFault = async () =>
        {
            await CloseThroughTheApiAsync(client, user, account);
            closed = true;
        };

        // Armed only now: the funding deposit also inserts a ledger row.
        fault.Arm();
        var key = Guid.NewGuid();
        var response = await WithdrawAsync(client, user, account, authorizationId, key, Funding);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyWithdrawn, authorizationId, key, race: null, account);
        _output.WriteLine($"fault fired: {fault.Fired}, closed through the API after the commit: {closed}");

        using var scope = new AssertionScope();
        ShouldBeAnsweredAsApplied(fault, closed, seen);
        seen.Accounts.Should().Equal(new AccountRow(true, 0m, 2));
        seen.Ledgers.Should().Equal($"{TransactionType.Deposit}+{TransactionType.Withdrawal}");
    }

    [SqlServerFact]
    public async Task ATransferOfEverything_AcknowledgementLost_ThenItsSourceClosed_AnswersThatItWasApplied()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AfterCommit);
        var client = CreateSqlClient(fault);
        var sender = await RegisterWithPinAsync(client, "lx");
        var payee = await RegisterAsync(client, "lxp");
        var source = await CreateSpareAsync(client, sender);
        await FundAsync(client, sender, source);
        var authorizationId = await MintTransferAsync(client, sender, source, payee.AzureTag, Funding);

        var closed = false;
        fault.BeforeAcknowledgementFault = async () =>
        {
            await CloseThroughTheApiAsync(client, sender, source);
            closed = true;
        };

        fault.Arm();
        var key = Guid.NewGuid();
        var response = await TransferAsync(client, sender, source, payee.AzureTag, authorizationId, key, Funding);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null,
            source, payee.PrimaryAccountId);
        _output.WriteLine($"fault fired: {fault.Fired}, closed through the API after the commit: {closed}");

        using var scope = new AssertionScope();
        ShouldBeAnsweredAsApplied(fault, closed, seen);
        seen.Accounts.Should().Equal(new AccountRow(true, 0m, 2), new AccountRow(false, Funding, 1));
        seen.Ledgers.Should().Equal(
            $"{TransactionType.Deposit}+{TransactionType.TransferOut}", $"{TransactionType.TransferIn}");
    }

    /// <summary>
    /// The answer of a write that landed and cannot return its result, on an account that is
    /// closed by now: one movement, the authorisation spent, the key kept as executed.
    /// </summary>
    private static void ShouldBeAnsweredAsApplied(TransferTransientFault fault, bool closed, Observed seen)
    {
        fault.Fired.Should().BeTrue("the acknowledgement must actually have been lost");
        closed.Should().BeTrue("and the account must actually have been closed before the write was looked at again");

        seen.Status.Should().Be(HttpStatusCode.Conflict, "the write landed: a 404 would say that nothing happened");
        seen.ErrorCode.Should().Be(ErrorCodes.IdempotencyResultUnknown);
        seen.Detail.Should().Be(AppliedDetail);
        var problem = JsonSerializer.Deserialize<JsonElement>(seen.Body);
        problem.TryGetProperty("applied", out var applied).Should().BeTrue("the claim was read as committed");
        applied.ValueKind.Should().Be(JsonValueKind.True);

        seen.MoneyAuditRows.Should().Be(1, "one movement, written once");
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed);
        seen.ConsumedAt.Should().NotBeNull();
        seen.IdempotencyRecords.Should().Equal(
            new[] { IdempotencyStatus.Executed }, "the key is kept: released, it would let the write run again");
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }
}
