using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Regression proof for the EF transient-retry defect on transfers (R2):
/// with EnableRetryOnFailure active (production wiring), a transient fault
/// inside <c>strategy.ExecuteAsync</c> re-runs the transfer delegate against
/// the SAME DbContext. Before the fix, the leftover Added transactions +
/// already-mutated balances (Case A) or an already-committed transfer
/// (Case B) were re-applied → duplicate transactions and a double debit.
///
/// This is the coverage that was missing: NO existing test forced the retry
/// (the InMemory / plain-SQL factories register the context WITHOUT retry, so
/// the delegate only ever ran once). Here a one-shot interceptor injects the
/// transient exactly once, on a context configured WITH retry.
///
/// Gated by AZUREBANK_TEST_SQLSERVER (SqlServerFactAttribute); serialized with
/// the other SQL proofs (fresh-database migration race).
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class TransferTransientRetrySqlServerTests : IDisposable
{
    /// <summary>The PIN these tests enrol and then send in-band (ADR-0041).</summary>
    private const string TestPin = "123456";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public TransferTransientRetrySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---- Case A: transient at the transfer's first SaveChanges ----

    [SqlServerFact]
    public async Task ExternalTransfer_TransientAtFirstSaveChanges_ExecutesExactlyOnce()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AtFirstSaveChanges);
        var client = CreateRetryingClient(fault);

        var sender = await RegisterAsync(client, "sxa");
        var recipient = await RegisterAsync(client, "rxa");
        await DepositAsync(client, sender, 1000m);

        fault.Arm();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Transient-retry proof (external, Case A)"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the injected transient must be absorbed by the execution strategy, not surfaced");
        fault.Fired.Should().BeTrue("the transient must actually have been injected (else the test proves nothing)");

        await AssertSingleTransferAsync(client, sender, recipient, funding: 1000m, amount: 100m);
        (await GetIdempotencyStatusAsync(sender.UserId, "POST api/transfers"))
            .Should().Be(IdempotencyStatus.Completed, "the successful transfer's record is stored for replay");
    }

    [SqlServerFact]
    public async Task InternalTransfer_TransientAtFirstSaveChanges_ExecutesExactlyOnce()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AtFirstSaveChanges);
        var client = CreateRetryingClient(fault);

        var user = await RegisterAsync(client, "sia");
        var savingsId = await CreateSecondAccountAsync(client, user);
        await DepositAsync(client, user, 1000m);

        fault.Arm();
        var response = await InternalTransferAsync(client, user, new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savingsId,
            Amount = 300m,
            Description = "Transient-retry proof (internal, Case A)"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        fault.Fired.Should().BeTrue("the transient must actually have been injected");

        (await GetBalanceAsync(client, user, user.AccountId)).Should().Be(700m, "debited exactly once");
        (await GetBalanceAsync(client, user, savingsId)).Should().Be(300m, "credited exactly once");
        (await CountTransactionsAsync(client, user, user.AccountId)).Should().Be(2, "funding + ONE transfer-out");
        (await CountTransactionsAsync(client, user, savingsId)).Should().Be(1, "ONE transfer-in");
        (await GetIdempotencyStatusAsync(user.UserId, "POST api/transfers/internal"))
            .Should().Be(IdempotencyStatus.Completed);
    }

    // ---- Case B: transient right AFTER the commit (commit ack lost) ----

    [SqlServerFact]
    public async Task ExternalTransfer_TransientAfterCommit_DoesNotDoubleExecute()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AfterCommit);
        var client = CreateRetryingClient(fault);

        var sender = await RegisterAsync(client, "sxb");
        var recipient = await RegisterAsync(client, "rxb");
        await DepositAsync(client, sender, 1000m);

        fault.Arm();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Transient-retry proof (external, Case B)"
        });

        // The transfer committed on attempt 1; the retry must recognise the
        // already-committed record and refuse to re-execute.
        var problem = await AssertResultUnknownAsync(response);
        fault.Fired.Should().BeTrue("the transient must actually have been injected");

        await AssertSingleTransferAsync(client, sender, recipient, funding: 1000m, amount: 100m);
        (await GetIdempotencyStatusAsync(sender.UserId, "POST api/transfers"))
            .Should().Be(IdempotencyStatus.Executed,
                "committed but the response was lost: never Completed, never re-executed");

        // The retry read the record under its key as Executed, so it knows the commit landed.
        AssertSaysTheOperationWasApplied(problem);
    }

    [SqlServerFact]
    public async Task InternalTransfer_TransientAfterCommit_DoesNotDoubleExecute()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AfterCommit);
        var client = CreateRetryingClient(fault);

        var user = await RegisterAsync(client, "sib");
        var savingsId = await CreateSecondAccountAsync(client, user);
        await DepositAsync(client, user, 1000m);

        fault.Arm();
        var response = await InternalTransferAsync(client, user, new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savingsId,
            Amount = 300m,
            Description = "Transient-retry proof (internal, Case B)"
        });

        var problem = await AssertResultUnknownAsync(response);
        fault.Fired.Should().BeTrue("the transient must actually have been injected");

        (await GetBalanceAsync(client, user, user.AccountId)).Should().Be(700m, "debited exactly once");
        (await GetBalanceAsync(client, user, savingsId)).Should().Be(300m, "credited exactly once");
        (await CountTransactionsAsync(client, user, user.AccountId)).Should().Be(2, "funding + ONE transfer-out");
        (await CountTransactionsAsync(client, user, savingsId)).Should().Be(1, "ONE transfer-in");
        (await GetIdempotencyStatusAsync(user.UserId, "POST api/transfers/internal"))
            .Should().Be(IdempotencyStatus.Executed);

        AssertSaysTheOperationWasApplied(problem);
    }

    // ---- The record gone when the retry looks: nothing is proven, and nothing is claimed ----

    /// <summary>
    /// The first save faults before it is sent, so nothing commits; at that instant the record
    /// under the key is deleted, as a takeover of a stale claim or the cleanup would delete it. The
    /// re-run's reload then finds no row.
    /// </summary>
    /// <remarks>
    /// Construction: the delete is made from a connection of its own as the command fault fires,
    /// BEFORE the faulted batch is sent, so the flip's <c>UPDATE</c> has not locked the row. It
    /// runs under <c>SET LOCK_TIMEOUT</c>: were the row locked after all, the test fails on the
    /// delete's row count instead of hanging.
    /// </remarks>
    [SqlServerFact]
    public async Task ExternalTransfer_RecordGoneWhenTheRetryReloadsIt_Answers409WithoutApplied_AndNothingMoved()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AtFirstSaveChanges);
        var client = CreateRetryingClient(fault);

        var sender = await RegisterAsync(client, "sxg");
        var recipient = await RegisterAsync(client, "rxg");
        await DepositAsync(client, sender, 1000m);

        var key = Guid.NewGuid();
        int? deleted = null;
        fault.BeforeCommandFault = () =>
            deleted = DeleteRecordOutOfBand(sender.UserId, "POST api/transfers", key);

        fault.Arm();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Record gone before the retry"
        }, key);
        var text = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"record deleted as the first save faulted ({deleted} row): {(int)response.StatusCode} {text}");

        fault.Fired.Should().BeTrue("the transient must actually have been injected");
        deleted.Should().Be(1, "the record must have been there, unlocked, and deleted before the retry");

        var problem = await AssertResultUnknownAsync(response);

        (await GetBalanceAsync(client, sender, sender.AccountId)).Should().Be(1000m, "nothing was debited");
        (await GetBalanceAsync(client, recipient, recipient.AccountId)).Should().Be(0m, "nothing was credited");
        (await CountTransactionsAsync(client, sender, sender.AccountId)).Should().Be(1, "the funding deposit only");
        (await CountTransactionsAsync(client, recipient, recipient.AccountId)).Should().Be(0, "no transfer row");
        (await FindRecordAsync(sender.UserId, "POST api/transfers", key)).Should().BeNull("the record is gone");
        (await AuthorisationStatusesAsync(sender.UserId)).Should().Equal(
            new[] { StepUpAuthorizationStatus.Pending }, "nothing was spent");

        // Nothing committed, and the answering request read nothing that says otherwise.
        problem.TryGetProperty("applied", out _).Should().BeFalse(
            "the record was not read as committed, so applied is absent: never true, never false");
        problem.GetProperty("detail").GetString().Should().Be(
            "A request with this idempotency key may have been executed: its record is no longer there, "
            + "so the outcome is not known. Verify via GET /api/transactions before sending it again with "
            + "a new key.");
    }

    /// <summary>
    /// A row under the key is not enough: after a stale claim was released, another body can own
    /// it. Its status proves nothing about this request, and its Processing claim is not ours to
    /// re-arm. The replacement lands before the faulted batch can lock or write the original row.
    /// </summary>
    [SqlServerTheory]
    [InlineData(IdempotencyStatus.Processing)]
    [InlineData(IdempotencyStatus.Executed)]
    [InlineData(IdempotencyStatus.Completed)]
    public async Task ExternalTransfer_RecordClaimedAgainWithOtherBytes_Answers409WithoutApplied_AndNothingMoved(
        IdempotencyStatus stored)
    {
        var fault = new TransferTransientFault(TransferFaultMode.AtFirstSaveChanges);
        var client = CreateRetryingClient(fault);

        var sender = await RegisterAsync(client, "sxh");
        var recipient = await RegisterAsync(client, "rxh");
        await DepositAsync(client, sender, 1000m);

        var key = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var replacement = new AzureBank.Shared.Entities.IdempotencyRecord
        {
            UserId = sender.UserId,
            Endpoint = "POST api/transfers",
            Key = key,
            ClaimId = Guid.NewGuid(),
            RequestHash = new string('b', 64),
            Status = stored,
            ResponseStatusCode = stored == IdempotencyStatus.Completed ? 201 : null,
            ResponseContentType = stored == IdempotencyStatus.Completed ? "application/json" : null,
            ResponseBody = stored == IdempotencyStatus.Completed
                ? """{"data":null,"message":"the answer to the other body"}"""
                : null,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        };
        int? deleted = null;
        int? inserted = null;
        fault.BeforeCommandFault = () =>
        {
            deleted = DeleteRecordOutOfBand(sender.UserId, replacement.Endpoint, key, replacement.RequestHash);
            inserted = InsertRecordOutOfBand(replacement);
        };

        fault.Arm();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Another body's record before the retry"
        }, key);
        var text = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"record replaced as the first save faulted ({deleted} deleted, {inserted} inserted, {stored}): {(int)response.StatusCode} {text}");

        fault.Fired.Should().BeTrue("the transient must actually have been injected");
        deleted.Should().Be(1, "the original claim must have a different hash and be deleted before the retry");
        inserted.Should().Be(1, "the replacement must have been written, unlocked, before the retry");

        var problem = await AssertResultUnknownAsync(response);

        (await GetBalanceAsync(client, sender, sender.AccountId)).Should().Be(1000m, "nothing was debited");
        (await GetBalanceAsync(client, recipient, recipient.AccountId)).Should().Be(0m, "nothing was credited");
        (await CountTransactionsAsync(client, sender, sender.AccountId)).Should().Be(1, "the funding deposit only");
        (await CountTransactionsAsync(client, recipient, recipient.AccountId)).Should().Be(0, "no transfer row");
        (await AuthorisationStatusesAsync(sender.UserId)).Should().Equal(
            new[] { StepUpAuthorizationStatus.Pending }, "nothing was spent");
        (await FindRecordAsync(sender.UserId, replacement.Endpoint, key)).Should().BeEquivalentTo(
            replacement, "the other body's record must survive without a re-arm, overwrite or release");

        problem.TryGetProperty("applied", out _).Should().BeFalse(
            "the record is about another body, so applied is absent: never true, never false");
        problem.GetProperty("detail").GetString().Should().Be(
            "This idempotency key now holds another request's record, so the outcome of this request "
            + "is not known. Verify via GET /api/transactions before sending it again with a new key.");
    }

    // ---- What "the record says Executed" stands on: the flip commits with the money or not at all ----

    [SqlServerFact]
    public async Task ExternalTransfer_RefusedBeforeAnyWrite_LeavesNoRecordUnderItsKey()
    {
        var client = CreatePlainClient();
        var sender = await RegisterAsync(client, "sxn");
        var recipient = await RegisterAsync(client, "rxn");
        await DepositAsync(client, sender, 50m);

        var key = Guid.NewGuid();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "More than the balance"
        }, key);

        await AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.InsufficientFunds);
        (await FindRecordAsync(sender.UserId, "POST api/transfers", key)).Should().BeNull(
            "the refusal came before any write, so the claim was released whole");
        (await GetBalanceAsync(client, sender, sender.AccountId)).Should().Be(50m);
        (await CountTransactionsAsync(client, sender, sender.AccountId)).Should().Be(1, "the funding deposit only");
    }

    [SqlServerFact]
    public async Task InternalTransfer_RefusedBeforeAnyWrite_LeavesNoRecordUnderItsKey()
    {
        var client = CreatePlainClient();
        var user = await RegisterAsync(client, "sin");
        var savingsId = await CreateSecondAccountAsync(client, user);
        await DepositAsync(client, user, 50m);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(client, user, new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savingsId,
            Amount = 100m,
            Description = "More than the balance"
        }, key);

        await AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.InsufficientFunds);
        (await FindRecordAsync(user.UserId, "POST api/transfers/internal", key)).Should().BeNull(
            "the refusal came before any write, so the claim was released whole");
        (await GetBalanceAsync(client, user, user.AccountId)).Should().Be(50m);
        (await GetBalanceAsync(client, user, savingsId)).Should().Be(0m);
    }

    /// <summary>
    /// A refusal AFTER the flip to <c>Executed</c> was written: the authorisation is spent from a
    /// second connection as the transfer's first batch goes out, the batch runs inside the
    /// transaction, the consume then matches no row and the transaction rolls back.
    /// </summary>
    /// <remarks>
    /// Were the rollback not to take the flip back, the record would be left <c>Executed</c> over a
    /// transfer that never happened, and the next request with the key would be told it was applied.
    /// </remarks>
    [SqlServerFact]
    public async Task ExternalTransfer_RefusedAfterItsFirstSaveRan_LeavesNoExecutedRecord()
    {
        var client = CreatePlainClient();
        var sender = await RegisterAsync(client, "sxq");
        var recipient = await RegisterAsync(client, "rxq");
        await DepositAsync(client, sender, 1000m);

        var body = new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Authorisation spent under the transfer"
        };
        var authorization = await AuthoriseAsync(
            client, sender.Token, "/api/transfers/authorizations",
            new TransferAuthorizationRequest
            {
                FromAccountId = body.FromAccountId,
                RecipientAzureTag = body.RecipientAzureTag,
                Amount = body.Amount,
                Pin = TestPin
            });

        var (race, sent) = RaceTheConsume(authorization);
        var key = Guid.NewGuid();
        var response = await PostMonetaryAsync(client, sender.Token, "/api/transfers", body, authorization, key);
        sent.Stop();

        AssertTheRaceWasLost(race);
        await AssertRefusedAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.AuthorizationInvalid);

        await AssertNoExecutedRecordAsync(sender.UserId, "POST api/transfers", key);
        (await GetBalanceAsync(client, sender, sender.AccountId)).Should().Be(1000m, "the debit rolled back");
        (await GetBalanceAsync(client, recipient, recipient.AccountId)).Should().Be(0m, "the credit rolled back");
        (await CountTransactionsAsync(client, sender, sender.AccountId)).Should().Be(1, "the funding deposit only");
        (await CountTransactionsAsync(client, recipient, recipient.AccountId)).Should().Be(0, "no transfer row");
        AssertTheFlipWentOutWithTheLedgerRows(sent);
    }

    [SqlServerFact]
    public async Task InternalTransfer_RefusedAfterItsFirstSaveRan_LeavesNoExecutedRecord()
    {
        var client = CreatePlainClient();
        var user = await RegisterAsync(client, "siq");
        var savingsId = await CreateSecondAccountAsync(client, user);
        await DepositAsync(client, user, 1000m);

        var body = new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savingsId,
            Amount = 300m,
            Description = "Authorisation spent under the transfer"
        };
        var authorization = await AuthoriseAsync(
            client, user.Token, "/api/transfers/internal/authorizations",
            new InternalTransferAuthorizationRequest
            {
                FromAccountId = body.FromAccountId,
                ToAccountId = body.ToAccountId,
                Amount = body.Amount,
                Pin = TestPin
            });

        var (race, sent) = RaceTheConsume(authorization);
        var key = Guid.NewGuid();
        var response = await PostMonetaryAsync(
            client, user.Token, "/api/transfers/internal", body, authorization, key);
        sent.Stop();

        AssertTheRaceWasLost(race);
        await AssertRefusedAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.AuthorizationInvalid);

        await AssertNoExecutedRecordAsync(user.UserId, "POST api/transfers/internal", key);
        (await GetBalanceAsync(client, user, user.AccountId)).Should().Be(1000m, "the debit rolled back");
        (await GetBalanceAsync(client, user, savingsId)).Should().Be(0m, "the credit rolled back");
        (await CountTransactionsAsync(client, user, user.AccountId)).Should().Be(1, "the funding deposit only");
        (await CountTransactionsAsync(client, user, savingsId)).Should().Be(0, "no transfer row");
        AssertTheFlipWentOutWithTheLedgerRows(sent);
    }

    /// <summary>
    /// A flip written, rolled back, and read again: the first save runs, the commit is refused as it
    /// starts, the strategy re-runs the attempt, and its reload must read <c>Processing</c>.
    /// </summary>
    /// <remarks>
    /// The direct negative of the answer a re-run gives when it reads <c>Executed</c>: were the
    /// rollback not to undo the flip, this transfer would be answered 409 "applied" with no money
    /// moved, instead of running.
    /// </remarks>
    [SqlServerFact]
    public async Task ExternalTransfer_CommitRefusedAsItStarts_RunsAgainAndMovesTheMoneyOnce()
    {
        var fault = new TransferTransientFault(TransferFaultMode.BeforeCommit);
        var client = CreateRetryingClient(fault);

        var sender = await RegisterAsync(client, "sxc");
        var recipient = await RegisterAsync(client, "rxc");
        await DepositAsync(client, sender, 1000m);

        fault.Arm();
        var key = Guid.NewGuid();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Commit refused as it starts"
        }, key);
        _output.WriteLine($"commit refused as it started: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        fault.Fired.Should().BeTrue("the commit must actually have been refused, else the test proves nothing");
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "nothing committed, so the re-run reads Processing and executes");

        await AssertSingleTransferAsync(client, sender, recipient, funding: 1000m, amount: 100m);
        (await FindRecordAsync(sender.UserId, "POST api/transfers", key))!.Status
            .Should().Be(IdempotencyStatus.Completed);
        (await AuthorisationStatusesAsync(sender.UserId)).Should().Equal(
            new[] { StepUpAuthorizationStatus.Consumed }, "spent once, by the attempt that committed");
    }

    [SqlServerFact]
    public async Task InternalTransfer_CommitRefusedAsItStarts_RunsAgainAndMovesTheMoneyOnce()
    {
        var fault = new TransferTransientFault(TransferFaultMode.BeforeCommit);
        var client = CreateRetryingClient(fault);

        var user = await RegisterAsync(client, "sic");
        var savingsId = await CreateSecondAccountAsync(client, user);
        await DepositAsync(client, user, 1000m);

        fault.Arm();
        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(client, user, new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savingsId,
            Amount = 300m,
            Description = "Commit refused as it starts"
        }, key);
        _output.WriteLine($"commit refused as it started: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        fault.Fired.Should().BeTrue("the commit must actually have been refused, else the test proves nothing");
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "nothing committed, so the re-run reads Processing and executes");

        (await GetBalanceAsync(client, user, user.AccountId)).Should().Be(700m, "debited exactly once");
        (await GetBalanceAsync(client, user, savingsId)).Should().Be(300m, "credited exactly once");
        (await CountTransactionsAsync(client, user, user.AccountId)).Should().Be(2, "funding + ONE transfer-out");
        (await CountTransactionsAsync(client, user, savingsId)).Should().Be(1, "ONE transfer-in");
        (await FindRecordAsync(user.UserId, "POST api/transfers/internal", key))!.Status
            .Should().Be(IdempotencyStatus.Completed);
        (await AuthorisationStatusesAsync(user.UserId)).Should().Equal(
            new[] { StepUpAuthorizationStatus.Consumed }, "spent once, by the attempt that committed");
    }

    // ---- infrastructure ----

    private HttpClient CreateRetryingClient(TransferTransientFault fault)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.EnableSqlRetryOnFailure();
        _factory.AddInterceptor(new TransferCommandFaultInterceptor(fault));
        _factory.AddInterceptor(new TransferCommitFaultInterceptor(fault));
        return _factory.CreateClient();
    }

    /// <summary>The same database, with no retry and no fault: for a refusal, which nothing re-runs.</summary>
    private HttpClient CreatePlainClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    /// <summary>
    /// Arms the out-of-band spend of <paramref name="authorization"/> and starts recording what the
    /// request sends, so the test can show the flip's <c>UPDATE</c> went out before the refusal.
    /// </summary>
    private (OutOfBandStepUpConsumeInterceptor Race, CommandRecordingInterceptor Sent) RaceTheConsume(Guid authorization)
    {
        var race = new OutOfBandStepUpConsumeInterceptor(SqlServerFactAttribute.ConnectionString!, authorization);
        var sent = new CommandRecordingInterceptor();
        _factory!.AddInterceptor(race);
        _factory.AddInterceptor(sent);
        race.Arm();
        sent.Start();
        return (race, sent);
    }

    private static void AssertTheRaceWasLost(OutOfBandStepUpConsumeInterceptor race)
    {
        race.Fired.Should().BeTrue(
            "the out-of-band consume must actually have run, else the test proves nothing");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must have won: the row was still Pending when it ran");
    }

    /// <summary>
    /// The premise of the refusal-after-the-flip tests, read from what reached the server: the
    /// record's <c>UPDATE</c> went out, and in the batch that wrote the ledger rows. A flip sent on
    /// its own, ahead of that batch, is a save between the flip and the business commit.
    /// </summary>
    private void AssertTheFlipWentOutWithTheLedgerRows(CommandRecordingInterceptor sent)
    {
        var flips = sent.Writes
            .Where(c => c.Contains("UPDATE [IdempotencyRecords]", StringComparison.Ordinal))
            .ToList();
        _output.WriteLine($"writes sent: {sent.Writes.Count}, of which naming UPDATE [IdempotencyRecords]: {flips.Count}");
        flips.Should().NotBeEmpty(
            "the flip to Executed must have been sent inside the transaction that then rolled back; "
            + "a refusal before it was written is the other test");
        flips.Should().Contain(
            c => c.Contains("[Transactions]", StringComparison.Ordinal),
            "and it went out in the batch that wrote the ledger rows");
    }

    /// <summary>Deletes the key's record from a connection of its own; returns the rows deleted.</summary>
    private static int DeleteRecordOutOfBand(Guid userId, string endpoint, Guid key, string? otherHash = null)
    {
        using var connection = new SqlConnection(SqlServerFactAttribute.ConnectionString!);
        connection.Open();
        using var delete = connection.CreateCommand();
        delete.CommandText =
            "SET LOCK_TIMEOUT 5000; "
            + "DELETE FROM [IdempotencyRecords] WHERE [UserId] = @user AND [Endpoint] = @endpoint AND [Key] = @key "
            + "AND (@otherHash IS NULL OR [RequestHash] <> @otherHash)";
        delete.Parameters.Add(new SqlParameter("@user", userId));
        delete.Parameters.Add(new SqlParameter("@endpoint", endpoint));
        delete.Parameters.Add(new SqlParameter("@key", key));
        // A replacement must be for different bytes; the row count proves that premise too.
        delete.Parameters.Add(new SqlParameter("@otherHash", (object?)otherHash ?? DBNull.Value));
        return delete.ExecuteNonQuery();
    }

    /// <summary>Inserts a record from a connection of its own; returns the rows inserted.</summary>
    private static int InsertRecordOutOfBand(AzureBank.Shared.Entities.IdempotencyRecord record)
    {
        using var connection = new SqlConnection(SqlServerFactAttribute.ConnectionString!);
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "SET LOCK_TIMEOUT 5000; "
            + "INSERT INTO [IdempotencyRecords] "
            + "([UserId], [Endpoint], [Key], [ClaimId], [RequestHash], [Status], "
            + "[ResponseStatusCode], [ResponseContentType], [ResponseBody], [CreatedAt], [ExpiresAt]) "
            + "VALUES (@user, @endpoint, @key, @claim, @hash, @status, @code, @type, @body, @created, @expires)";
        insert.Parameters.Add(new SqlParameter("@user", record.UserId));
        insert.Parameters.Add(new SqlParameter("@endpoint", record.Endpoint));
        insert.Parameters.Add(new SqlParameter("@key", record.Key));
        insert.Parameters.Add(new SqlParameter("@claim", record.ClaimId));
        insert.Parameters.Add(new SqlParameter("@hash", record.RequestHash));
        insert.Parameters.Add(new SqlParameter("@status", record.Status.ToString()));
        insert.Parameters.Add(new SqlParameter("@code", (object?)record.ResponseStatusCode ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@type", (object?)record.ResponseContentType ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@body", (object?)record.ResponseBody ?? DBNull.Value));
        // Match datetime2 rather than rounding through a datetime parameter: every stored field
        // is compared after the refusal, including the timestamps the request must leave alone.
        insert.Parameters.Add(new SqlParameter("@created", System.Data.SqlDbType.DateTime2) { Value = record.CreatedAt });
        insert.Parameters.Add(new SqlParameter("@expires", System.Data.SqlDbType.DateTime2) { Value = record.ExpiresAt });
        return insert.ExecuteNonQuery();
    }

    private sealed record TestUser(string Token, Guid UserId, Guid AccountId, string AzureTag);

    private static async Task<TestUser> RegisterAsync(HttpClient client, string prefix)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"{prefix}_{uniqueId}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"{prefix}{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Retry",
            LastName = "Prover"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var user = new TestUser(
            result!.Data!.Token.AccessToken, result.Data.User.Id, result.Data.Account.Id, azureTag);

        // ADR-0042: every user this file creates goes on to move money, and a transfer is now
        // authorised by a minted reference. Without enrolment the mint is refused 422 PIN_REQUIRED
        // and the transient retry these tests exist to prove would never be reached.
        using var setPin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
        {
            Content = JsonContent.Create(new SetPinRequest { Pin = TestPin, Password = "TestPass123!" }, options: Json)
        };
        setPin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        (await client.SendAsync(setPin)).EnsureSuccessStatusCode();

        return user;
    }

    private static async Task<Guid> CreateSecondAccountAsync(HttpClient client, TestUser user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts")
        {
            Content = JsonContent.Create(new CreateAccountRequest
            {
                Name = "Savings",
                Type = AccountType.Savings
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json);
        return result!.Data!.Id;
    }

    private static async Task DepositAsync(HttpClient client, TestUser user, decimal amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new DepositRequest
            {
                AccountId = user.AccountId,
                Amount = amount,
                Description = "Funding"
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /*
      Both helpers mint from the body they are about to send, so the binding cannot drift from the
      transfer in four separate call sites — and so these tests stay about the transient retry.

      MINTING HAPPENS AFTER `fault.Arm()`, and that is safe rather than lucky: the injector latches
      only on a command that is both an INSERT and names `[Transactions]`
      (TransferTransientFault.IsTransferInsert). A mint writes `[StepUpAuthorizations]`, so it
      cannot consume the one-shot fault the transfer under test needs.
    */
    private static async Task<HttpResponseMessage> TransferAsync(
        HttpClient client, TestUser sender, TransferRequest body, Guid? idempotencyKey = null)
    {
        var authorization = await AuthoriseAsync(
            client, sender.Token, "/api/transfers/authorizations",
            new TransferAuthorizationRequest
            {
                FromAccountId = body.FromAccountId,
                RecipientAzureTag = body.RecipientAzureTag,
                Amount = body.Amount,
                Pin = TestPin
            });
        return await PostMonetaryAsync(
            client, sender.Token, "/api/transfers", body, authorization, idempotencyKey);
    }

    private static async Task<HttpResponseMessage> InternalTransferAsync(
        HttpClient client, TestUser user, InternalTransferRequest body, Guid? idempotencyKey = null)
    {
        var authorization = await AuthoriseAsync(
            client, user.Token, "/api/transfers/internal/authorizations",
            new InternalTransferAuthorizationRequest
            {
                FromAccountId = body.FromAccountId,
                ToAccountId = body.ToAccountId,
                Amount = body.Amount,
                Pin = TestPin
            });
        return await PostMonetaryAsync(
            client, user.Token, "/api/transfers/internal", body, authorization, idempotencyKey);
    }

    private static async Task<Guid> AuthoriseAsync<T>(
        HttpClient client, string token, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var minted = await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json);
        return minted!.Data!.AuthorizationId;
    }

    private static async Task<HttpResponseMessage> PostMonetaryAsync<T>(
        HttpClient client, string token, string url, T body, Guid? stepUpAuthorizationId = null,
        Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, (idempotencyKey ?? Guid.NewGuid()).ToString());
        if (stepUpAuthorizationId is { } authorizationId)
        {
            request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
        }
        return await client.SendAsync(request);
    }

    private async Task AssertSingleTransferAsync(
        HttpClient client, TestUser sender, TestUser recipient, decimal funding, decimal amount)
    {
        (await GetBalanceAsync(client, sender, sender.AccountId))
            .Should().Be(funding - amount, "the sender must be debited EXACTLY once");
        (await GetBalanceAsync(client, recipient, recipient.AccountId))
            .Should().Be(amount, "the recipient must be credited EXACTLY once");
        (await CountTransactionsAsync(client, sender, sender.AccountId))
            .Should().Be(2, "funding deposit + EXACTLY ONE outgoing transfer");
        (await CountTransactionsAsync(client, recipient, recipient.AccountId))
            .Should().Be(1, "EXACTLY ONE incoming transfer");
    }

    private static async Task<JsonElement> AssertResultUnknownAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a transfer that committed, or may have, must not re-execute; the retry yields RESULT_UNKNOWN");
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyResultUnknown);
        return problem;
    }

    private static void AssertSaysTheOperationWasApplied(JsonElement problem)
    {
        problem.TryGetProperty("applied", out var applied).Should().BeTrue(
            "the re-run read the record under its key as committed, so the answer says it was applied");
        applied.ValueKind.Should().Be(JsonValueKind.True, "the boolean true, not a string and not false");
        problem.GetProperty("detail").GetString().Should().Be(
            "The operation sent with this idempotency key was applied, but this request cannot return "
            + "its result. Do not send it again with a new key: look for it with GET /api/transactions.");
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        JsonSerializer.Deserialize<JsonElement>(text).GetProperty("errorCode").GetString().Should().Be(errorCode);
    }

    /// <summary>The key has no record, or one that is still <c>Processing</c>: never a committed one.</summary>
    private async Task AssertNoExecutedRecordAsync(Guid userId, string endpoint, Guid key)
    {
        var record = await FindRecordAsync(userId, endpoint, key);
        _output.WriteLine($"record under the key after the refusal: {record?.Status.ToString() ?? "none"}");
        (record?.Status).Should().NotBe(IdempotencyStatus.Executed,
            "the flip was written inside a transaction that rolled back; a record left Executed "
            + "would tell the next request with this key that the money moved");
        (record?.Status).Should().NotBe(IdempotencyStatus.Completed);
    }

    private static async Task<decimal> GetBalanceAsync(HttpClient client, TestUser user, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{accountId}/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(Json);
        return result!.Data!.Balance;
    }

    private static async Task<int> CountTransactionsAsync(HttpClient client, TestUser user, Guid accountId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/transactions?accountId={accountId}&pageSize=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json);
        return result!.Pagination.TotalItems;
    }

    private async Task<IdempotencyStatus> GetIdempotencyStatusAsync(Guid userId, string endpoint)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var record = await db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.Endpoint == endpoint);
        record.Should().NotBeNull($"the transfer must have claimed an idempotency record for {endpoint}");
        return record!.Status;
    }

    private async Task<AzureBank.Shared.Entities.IdempotencyRecord?> FindRecordAsync(
        Guid userId, string endpoint, Guid key)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId && r.Endpoint == endpoint && r.Key == key);
    }

    /// <summary>The status of every authorisation the user minted; each test here mints one.</summary>
    private async Task<List<StepUpAuthorizationStatus>> AuthorisationStatusesAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.StepUpAuthorizations
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.Status)
            .ToListAsync();
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
