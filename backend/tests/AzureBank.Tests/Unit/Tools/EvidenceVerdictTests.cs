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

    private static Transaction Outgoing() => new()
    {
        Id = MovementId,
        TransactionNumber = "TXN-20260914-0000000001X",
        Type = TransactionType.TransferOut,
        Amount = 25m,
        Account = null!,
    };

    private static StepUpAuthorization Consumed(Guid id, Guid? by) => new()
    {
        Id = id,
        UserId = Guid.NewGuid(),
        Operation = StepUpOperation.Transfer,
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
            "  Bound authorisation: none, because no Succeeded transfer row names this");
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
}
