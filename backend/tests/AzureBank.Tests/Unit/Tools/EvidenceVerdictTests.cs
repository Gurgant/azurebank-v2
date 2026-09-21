using AzureBank.AuditVerifier.Commands;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// The second-factor verdict of the evidence pack, one input shape at a time.
/// </summary>
/// <remarks>
/// <para>
/// <c>EvidencePackTests</c> drives the real command over a real store and pins the three outcomes a
/// transfer can reach through the API: bound, row gone, row re-pointed. This file pins the function
/// those outcomes are printed by, so every branch is named next to the input that reaches it —
/// including the two the API can no longer produce: a pre-binding row, written before success rows
/// named the authorisation they consumed (2026-09-14), and a movement with no audit row at all.
/// </para>
/// <para>
/// The first line of each verdict is what an operator searches the runbook for, so it is asserted
/// as a whole line here rather than as a fragment; <c>AuditProseGuardTests</c> separately checks
/// that the runbook names every headline this function can print.
/// </para>
/// </remarks>
public class EvidenceVerdictTests
{
    private static readonly Guid MovementId = Guid.Parse("9d3ab0f2-6c4e-4c1d-9a5c-1f2e3d4c5b6a");
    private static readonly Guid BoundId = Guid.Parse("0b1c2d3e-4f50-4617-8899-aabbccddeeff");
    private static readonly Guid OtherId = Guid.Parse("ffeeddcc-bbaa-4998-8776-655443322110");
    private static readonly Guid OwnerId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid StrangerId = Guid.Parse("66666666-7777-4888-8999-000000000000");

    /// <summary>The outgoing leg of an EXTERNAL transfer: a recipient handle marks the rail.</summary>
    private static Transaction Outgoing() => new()
    {
        Id = MovementId,
        TransactionNumber = "TXN-20260914-0000000001X",
        Type = TransactionType.TransferOut,
        Amount = 25m,
        RecipientAzureTag = "janesmith",
        Account = new Account { UserId = OwnerId, AccountNumber = "AB-0000-0000-00", Name = "Checking", User = null! },
    };

    /// <summary>
    /// A WITHDRAWAL: cash out, so no recipient handle and no destination — which is exactly why the
    /// verdict cannot tell the rails apart by the handle alone (ADR-0056).
    /// </summary>
    private static Transaction Withdrawal() => new()
    {
        Id = MovementId,
        TransactionNumber = "TXN-20260921-0000000002X",
        Type = TransactionType.Withdrawal,
        Amount = 25m,
        RecipientAzureTag = null,
        Account = new Account { UserId = OwnerId, AccountNumber = "AB-0000-0000-00", Name = "Checking", User = null! },
    };

    private static string[] WithdrawalVerdict(
        StepUpAuthorization? pointer,
        bool hasSuccessRow,
        Guid? boundId,
        StepUpAuthorization? bound,
        bool detailUnreadable = false) =>
        EvidenceCommand.StrongAuthentication(
            Withdrawal(), pointer, hasSuccessRow, detailUnreadable, boundId, bound).ToArray();

    /*
      The OPERATION is a parameter because the verdict checks it against the movement's shape: a
      withdrawal expects `StepUpOperation.Withdrawal`, and a Transfer-operation row against a
      withdrawal is a MISMATCH -- correctly, since the operation name is inside the binding hash.
      The first version of the withdrawal cases below left this at Transfer and was answered
      "BOUND AUTHORISATION DOES NOT MATCH", which was the check working rather than a test bug.
    */
    private static StepUpAuthorization Consumed(
        Guid id, Guid? by, StepUpOperation operation = StepUpOperation.Transfer) => new()
    {
        Id = id,
        UserId = OwnerId,
        Operation = operation,
        BindingHash = "not read by the verdict",
        Status = by is null ? StepUpAuthorizationStatus.Pending : StepUpAuthorizationStatus.Consumed,
        CreatedAt = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc),
        ExpiresAt = new DateTime(2026, 9, 14, 10, 5, 0, DateTimeKind.Utc),
        ConsumedAt = by is null ? null : new DateTime(2026, 9, 14, 10, 0, 30, DateTimeKind.Utc),
        ConsumedByTransactionId = by,
    };

    private static string[] Verdict(
        StepUpAuthorization? pointer,
        bool hasSuccessRow,
        Guid? boundId,
        StepUpAuthorization? bound,
        bool detailUnreadable = false) =>
        EvidenceCommand.StrongAuthentication(
            Outgoing(), pointer, hasSuccessRow, detailUnreadable, boundId, bound).ToArray();

    [Fact]
    public void ABoundRowThatPaid_IsSTRONGLYAUTHENTICATEDBOUNDINTHECHAIN()
    {
        var bound = Consumed(BoundId, MovementId);

        var lines = Verdict(pointer: bound, hasSuccessRow: true, BoundId, bound);

        lines[0].Should().Be(
            "STRONGLY AUTHENTICATED, BOUND IN THE CHAIN: the audit row for this movement names"
            + $" authorisation {BoundId:D}, and that row paid for it.");
        lines.Should().Contain("  PIN proved (minted) 2026-09-14T10:00:00.0000000Z");
        lines.Should().Contain("  Spent (consumed)   2026-09-14T10:00:30.0000000Z");
        lines.Should().NotContain(
            l => l.Contains("second authorisation", StringComparison.Ordinal),
            "the pointer and the chained name agree, so there is no second claimant to warn about");
        lines.Should().NotContain(l => l.Contains("pre-binding", StringComparison.Ordinal));
    }

    [Fact]
    public void ABoundRowThatPaid_WhileAnotherRowAlsoPointsHere_WarnsAboutTheSecondClaimant()
    {
        var bound = Consumed(BoundId, MovementId);
        var impostor = Consumed(OtherId, MovementId);

        var lines = Verdict(pointer: impostor, hasSuccessRow: true, BoundId, bound);

        lines[0].Should().StartWith("STRONGLY AUTHENTICATED, BOUND IN THE CHAIN:");
        lines.Should().Contain($"  ⚠️ A second authorisation, {OtherId:D}, also claims this movement");
    }

    [Fact]
    public void ABoundRowThatIsGone_IsBOUNDAUTHORISATIONMISSING_AndNamesTheIdTheChainCarries()
    {
        var lines = Verdict(pointer: null, hasSuccessRow: true, BoundId, bound: null);

        lines[0].Should().Be(
            "BOUND AUTHORISATION MISSING: the chained audit row names authorisation"
            + $" {BoundId:D}, and no such row exists.");
        lines.Should().Contain("  The chain vouches for the NAME: this movement was paid for by that");
        lines.Should().NotContain(
            l => l.StartsWith("NOT STRONGLY AUTHENTICATED", StringComparison.Ordinal),
            "a missing bound row is a finding about a named authorisation, not an absence of one");
    }

    [Fact]
    public void ABoundRowThatIsGone_WhileAnotherRowPointsHere_NamesTheImpostor()
    {
        var impostor = Consumed(OtherId, MovementId);

        var lines = Verdict(pointer: impostor, hasSuccessRow: true, BoundId, bound: null);

        lines[0].Should().StartWith("BOUND AUTHORISATION MISSING:");
        lines.Should().Contain($"  ⚠️ A different authorisation, {OtherId:D}, claims to have paid");
    }

    [Fact]
    public void ABoundRowRePointedAtAnotherMovement_IsBOUNDAUTHORISATIONDOESNOTMATCH()
    {
        var rePointed = Consumed(BoundId, OtherId);

        var lines = Verdict(pointer: null, hasSuccessRow: true, BoundId, rePointed);

        lines[0].Should().Be(
            "BOUND AUTHORISATION DOES NOT MATCH: the chained audit row names authorisation"
            + $" {BoundId:D},");
        lines[1].Should().Be($"  and that row says it paid for {OtherId:D} instead.");
    }

    [Fact]
    public void ABoundRowNeverSpent_IsBOUNDAUTHORISATIONDOESNOTMATCH_WithTheStatus()
    {
        var unspent = Consumed(BoundId, by: null);

        var lines = Verdict(pointer: null, hasSuccessRow: true, BoundId, unspent);

        lines[0].Should().StartWith("BOUND AUTHORISATION DOES NOT MATCH:");
        lines[1].Should().Be("  and that row records no movement against it (status Pending).");
    }

    [Fact]
    public void ABoundRowWhoseStatusWasRewritten_IsBOUNDAUTHORISATIONDOESNOTMATCH_AndSaysWhichHalf()
    {
        // The pointer still names this movement; only the status was written around the application.
        // The first wording printed "paid for <this very movement> instead" here, found in review.
        var tampered = Consumed(BoundId, MovementId);
        tampered.Status = StepUpAuthorizationStatus.Pending;

        var lines = Verdict(pointer: null, hasSuccessRow: true, BoundId, tampered);

        lines[0].Should().StartWith("BOUND AUTHORISATION DOES NOT MATCH:");
        lines[1].Should().Be("  and that row points at this movement, but its status is Pending, not Consumed.");
    }

    [Fact]
    public void ABoundRowMintedByAnotherUser_IsBOUNDAUTHORISATIONDOESNOTMATCH_AndNamesBothOwners()
    {
        // Pointer and status intact; only the owner was written around the application.
        var stranger = Consumed(BoundId, MovementId);
        stranger.UserId = StrangerId;

        var lines = Verdict(pointer: null, hasSuccessRow: true, BoundId, stranger);

        lines[0].Should().StartWith("BOUND AUTHORISATION DOES NOT MATCH:");
        lines[1].Should().Be(
            $"  and that row was minted by {StrangerId:D}, not by this account's owner {OwnerId:D}.");
    }

    [Fact]
    public void ABoundRowMintedForTheOtherOperation_IsBOUNDAUTHORISATIONDOESNOTMATCH_AndNamesBoth()
    {
        var wrongRail = Consumed(BoundId, MovementId);
        wrongRail.Operation = StepUpOperation.InternalTransfer;

        var lines = Verdict(pointer: null, hasSuccessRow: true, BoundId, wrongRail);

        lines[0].Should().StartWith("BOUND AUTHORISATION DOES NOT MATCH:");
        lines[1].Should().Be("  and that row was minted for InternalTransfer, not for the Transfer this movement is.");
    }

    [Fact]
    public void ABoundRowMarkedConsumedWithNoInstant_IsBOUNDAUTHORISATIONDOESNOTMATCH()
    {
        var noInstant = Consumed(BoundId, MovementId);
        noInstant.ConsumedAt = null;

        var lines = Verdict(pointer: null, hasSuccessRow: true, BoundId, noInstant);

        lines[0].Should().StartWith("BOUND AUTHORISATION DOES NOT MATCH:");
        lines[1].Should().Be("  and that row is marked Consumed but records no instant of spending.");
    }

    [Fact]
    public void AnInternalTransfer_BoundToAnInternalAuthorisation_IsSTRONGLYAUTHENTICATEDBOUNDINTHECHAIN()
    {
        // No recipient handle: the internal rail, so the expected operation is InternalTransfer.
        var movement = Outgoing();
        movement.RecipientAzureTag = null;
        var bound = Consumed(BoundId, MovementId);
        bound.Operation = StepUpOperation.InternalTransfer;

        var lines = EvidenceCommand.StrongAuthentication(movement, bound, true, false, BoundId, bound).ToArray();

        lines[0].Should().StartWith("STRONGLY AUTHENTICATED, BOUND IN THE CHAIN:");
    }

    [Fact]
    public void ARePointedRow_WhileAnotherRowPointsHere_NamesTheImpostorToo()
    {
        var rePointed = Consumed(BoundId, OtherId);
        var impostor = Consumed(OtherId, MovementId);

        var lines = Verdict(pointer: impostor, hasSuccessRow: true, BoundId, rePointed);

        lines[0].Should().StartWith("BOUND AUTHORISATION DOES NOT MATCH:");
        lines.Should().Contain($"  ⚠️ A different authorisation, {OtherId:D}, claims this movement");
    }

    [Fact]
    public void ARowWhoseDetailCannotBeRead_IsReportedAsUnreadable_NotAsPreBinding()
    {
        var lines = Verdict(pointer: null, hasSuccessRow: true, boundId: null, bound: null, detailUnreadable: true);

        lines[0].Should().StartWith("NOT STRONGLY AUTHENTICATED:");
        lines.Should().Contain("  Bound authorisation: UNREADABLE. The audit row for this movement carries a");
        lines.Should().NotContain(l => l.Contains("pre-binding", StringComparison.Ordinal));
    }

    [Fact]
    public void APreBindingRowWithAPointer_IsSTRONGLYAUTHENTICATED_AndSaysItIsPreBinding()
    {
        var pointer = Consumed(OtherId, MovementId);

        var lines = Verdict(pointer, hasSuccessRow: true, boundId: null, bound: null);

        lines[0].Should().Be(
            $"STRONGLY AUTHENTICATED: authorisation {OtherId:D} paid for this transfer.");
        lines.Should().Contain("  ⚠️ This row is evidence the application wrote, and it is NOT inside the");
        lines.Should().Contain(
            "  Bound authorisation: none. The audit row for this movement is a pre-binding row,");
        lines.Should().Contain(
            "  (2026-09-14); the pointer above is all the binding this movement has.");
    }

    [Fact]
    public void APreBindingRowWithNoPointer_IsNOTSTRONGLYAUTHENTICATED_TheOldHonestShape()
    {
        var lines = Verdict(pointer: null, hasSuccessRow: true, boundId: null, bound: null);

        lines[0].Should().Be(
            "NOT STRONGLY AUTHENTICATED: no consumed authorisation names this transaction.");
        lines.Should().Contain(
            "  Bound authorisation: none. The audit row for this movement is a pre-binding row,");
    }

    [Fact]
    public void AMovementWithNoAuditRowAtAll_SaysSoInsteadOfCallingItPreBinding()
    {
        var lines = Verdict(pointer: null, hasSuccessRow: false, boundId: null, bound: null);

        lines[0].Should().StartWith("NOT STRONGLY AUTHENTICATED:");
        lines.Should().Contain(
            // "movement", not "transfer", since ADR-0056: a withdrawal has a MoneyWithdrawn
            // success row and this sentence is about whichever row names the movement.
            "  Bound authorisation: none, because no Succeeded movement row names this");
        lines.Should().NotContain(l => l.Contains("pre-binding", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeposit_IsNOAUTHORISATIONAPPLIES_WhateverTheOtherInputsSay()
    {
        var movement = Outgoing();
        movement.Type = TransactionType.Deposit;
        var bound = Consumed(BoundId, MovementId);

        var lines = EvidenceCommand.StrongAuthentication(movement, bound, true, false, BoundId, bound).ToArray();

        lines[0].Should().StartWith("NO AUTHORISATION APPLIES");
    }

    [Fact]
    public void TheDetailRoundTrips_AndAnythingElseReadsAsNoName()
    {
        AuditDetails.ConsumedAuthorisation(BoundId).Should().Be(
            "{\"authorizationId\":\"0b1c2d3e-4f50-4617-8899-aabbccddeeff\"}",
            "the shape is fixed by hand so a serializer setting cannot move it");
        AuditDetails.ConsumedAuthorisationOf(AuditDetails.ConsumedAuthorisation(BoundId)).Should().Be(BoundId);

        AuditDetails.ConsumedAuthorisationOf(null).Should().BeNull("a pre-binding row");
        AuditDetails.ConsumedAuthorisationOf("").Should().BeNull();
        AuditDetails.ConsumedAuthorisationOf("AUTHORIZATION_REQUIRED").Should().BeNull(
            "a refusal row carries an error code, not JSON, and the reader must not throw on it");
        AuditDetails.ConsumedAuthorisationOf("{\"authorizationId\":\"not-a-guid\"}").Should().BeNull();
        AuditDetails.ConsumedAuthorisationOf("{\"authorizationId\":42}").Should().BeNull();
        AuditDetails.ConsumedAuthorisationOf("[\"0b1c2d3e-4f50-4617-8899-aabbccddeeff\"]").Should().BeNull();
        AuditDetails.ConsumedAuthorisationOf("{\"authorizationId\":\"{0b1c2d3e-4f50-4617-8899-aabbccddeeff}\"}")
            .Should().BeNull("only the D format is written, so only the D format is read");
        /*
          NOT A JsonException. JsonDocument.Parse refuses a lone surrogate with an ArgumentException
          ("Cannot transcode invalid UTF-16 string to UTF-8 JSON text", observed on .NET 10.0.12),
          and an nvarchar written around the application can hold one. The first reader caught
          JsonException only, so this input escaped to the command's outer catch and printed
          CANNOT ASSEMBLE -- blaming configuration for a tampered row. Red before the second catch.
        */
        AuditDetails.ConsumedAuthorisationOf("{\"authorizationId\":\"\uD800\"}").Should().BeNull(
            "a lone surrogate is not JSON this reader can use, and it is not an exception either");
    }

    /*
      THE WITHDRAWAL CASES, added with ADR-0056. Before it, a withdrawal was answered
      "NO AUTHORISATION APPLIES" and never reached any of this.

      Both of the widenings it needed are asserted here because neither would have failed anything
      on its own: `appliesToType` let the withdrawal in, and the NOUN had to stop being the word
      "transfer". The first attempt at that noun used `movement.Type.ToString()`, which renders
      "transferout" and silently changed every TRANSFER's report -- caught by the two cases above,
      which is why they pin the sentence and not just its prefix.
    */
    [Fact]
    public void AWithdrawalWithABoundRow_IsSTRONGLYAUTHENTICATEDBOUNDINTHECHAIN()
    {
        var bound = Consumed(BoundId, MovementId, StepUpOperation.Withdrawal);

        var lines = WithdrawalVerdict(pointer: bound, hasSuccessRow: true, BoundId, bound);

        lines[0].Should().StartWith("STRONGLY AUTHENTICATED, BOUND IN THE CHAIN:");
        lines.Should().NotContain(
            l => l.Contains("NO AUTHORISATION APPLIES", StringComparison.Ordinal),
            "a withdrawal mints and spends an authorisation since ADR-0056, so that verdict "
            + "against one is a FINDING rather than the expected answer");
    }

    [Fact]
    public void AWithdrawalWithAPointerOnly_SaysWITHDRAWAL_NotTransfer()
    {
        var pointer = Consumed(OtherId, MovementId, StepUpOperation.Withdrawal);

        var lines = WithdrawalVerdict(pointer, hasSuccessRow: true, boundId: null, bound: null);

        lines[0].Should().Be(
            $"STRONGLY AUTHENTICATED: authorisation {OtherId:D} paid for this withdrawal.",
            "the operator is reading this line to find out what happened; calling a withdrawal a "
            + "transfer is wrong in the one place that cannot afford it");
    }

    [Fact]
    public void AWithdrawalWithNoSuccessRow_SaysSoWithoutNamingARail()
    {
        // The positive control: with no success row there is nothing to name the authorisation, and
        // the sentence must not claim a TRANSFER row is the one missing.
        var lines = WithdrawalVerdict(
            pointer: Consumed(OtherId, MovementId, StepUpOperation.Withdrawal),
            hasSuccessRow: false, boundId: null, bound: null);

        lines.Should().Contain("  Bound authorisation: none, because no Succeeded movement row names this");
    }
}
