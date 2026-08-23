using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// What the operator-runnable verifier SAYS, and what a script reads from its exit code.
/// </summary>
/// <remarks>
/// The mapping from a verification result to a verdict is the whole tool; everything around it is
/// plumbing. It is tested here rather than through the console because the outcomes that matter
/// most -- an empty table, and a break at the first row -- are the ones nobody would think to
/// reproduce by hand during an incident.
/// </remarks>
public class AuditVerifierReportTests
{
    [Fact]
    public void AnEmptyTableIsNOTReportedAsIntact()
    {
        /*
          THE TRAP THIS PROJECT HAS ALREADY FALLEN INTO ONCE. VerifyAsync reports IsIntact for zero
          rows, which is true and useless: a chain of nothing links perfectly. A table truncated to
          nothing therefore looks exactly like a freshly migrated one, and printing "intact" over it
          would tell an operator the opposite of what happened.
        */
        var (exitCode, lines) = VerifyCommand.Report(new AuditChainVerification(0, null, null), null, null);

        exitCode.Should().Be(
            VerifyCommand.NothingToVerify,
            "a script that treated this as success would pass a check it never performed");
        exitCode.Should().NotBe(VerifyCommand.Intact);
        string.Join(" ", lines).Should().NotContain(
            "INTACT", "the word itself is the thing that would mislead");
    }

    [Fact]
    public void ABreakAtTheFirstRow_PointsAtTheKEYBeforeAnAttacker()
    {
        /*
          A WRONG key is well-formed, so the options validation passes it, and it mismatches from
          row one -- every time, because the first hash it recomputes is already different. A real
          tamper breaks where it happened instead. Position is the only tell available, and without
          it an operator opens an incident about an attacker who does not exist.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(0, 1, "Row ... does not match its own hash"), 1, 500);

        exitCode.Should().Be(VerifyCommand.Broken);
        string.Join(" ", lines).Should().Contain(
            "Audit:ChainKey",
            "breaking at sequence 1 is far more often the wrong key than tampering");
    }

    [Fact]
    public void ABreakInTheMIDDLE_DoesNotBlameTheKey()
    {
        // The negative control. If the hint appeared on every break it would be noise, and the one
        // case where it means something would be indistinguishable from the ones where it does not.
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(4_312, 4_313, "Row ... expected to follow ..."), 1, 9_000);

        exitCode.Should().Be(VerifyCommand.Broken);
        string.Join(" ", lines).Should().NotContain(
            "Audit:ChainKey",
            "a break deep in the table is where a tamper would show, and misdirecting to the key "
            + "would send the operator away from it");
        string.Join(" ", lines).Should().Contain("4,313", "the operator needs the position");
        string.Join(" ", lines).Should().Contain("4,312", "and how much verified before it");
    }

    [Fact]
    public void AnIntactChainReportsTheCOUNT_AndDoesNotOverclaim()
    {
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(40_006, null, null), 1, 40_006);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Intact);
        text.Should().Contain(
            "40,006",
            "'intact' without a count is the assertion that has misled this project before -- a "
            + "verification that read nothing also says intact");
        text.Should().Contain("Sequence range", "a count means nothing without the range it covers");
        text.Should().Contain(
            "NOT prove",
            "tail truncation is undetectable by construction, and the tool must say so where the "
            + "operator reads the good news rather than only in the ADR");
    }
}
