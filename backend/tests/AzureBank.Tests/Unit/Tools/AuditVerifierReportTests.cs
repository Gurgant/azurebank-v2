using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using AzureBank.Shared.Options;
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
    public async Task AnUnreachableDatabase_IsNoVerdict_AndMustNotLookLikeABrokenChain()
    {
        /*
          THE DANGEROUS COLLISION, MEASURED BEFORE IT WAS FIXED. System.CommandLine turns any
          exception escaping a handler into exit 1 -- which in this tool means CHAIN BROKEN. So an
          unreachable server, a malformed connection string and a missing one all reported the same
          code as a tampered audit trail, in the one tool whose whole purpose is telling those apart.
          An automated check would have paged somebody about a possible attack over a typo in an
          environment variable.

          localhost,1 with a two-second connect timeout: refused fast, and nothing about the failure
          depends on which machine runs the test.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseSqlServer(
            "Server=localhost,1;Database=Nope;User Id=u;Password=p;TrustServerCertificate=True;Connect Timeout=2"));
        services.AddSingleton<IAuditChain>(new AuditChain(
            Options.Create(new AuditOptions { ChainKey = new string('k', 32) }),
            NullLogger<AuditChain>.Instance));

        using var provider = services.BuildServiceProvider();

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(
            VerifyCommand.Misconfigured,
            "a database this tool cannot reach is a fact about the invocation, not about the bank");
        exitCode.Should().NotBe(
            VerifyCommand.Broken,
            "this is THE collision that mattered: automation reading 1 would treat an unreachable "
            + "server as a tampered audit trail");
        text.Should().Contain("CANNOT VERIFY");
        text.Should().Contain(
            "NOT a statement about the chain",
            "the operator has to be told what the result does not mean, not only what it is");
    }

    [Fact]
    public void TheExitCodesAreAllDistinct()
    {
        /*
          THE REGRESSION THAT ACTUALLY HAPPENED WAS A REUSED VALUE, not a wrong branch. Parse
          failures arrived as exit 1 from System.CommandLine, which this tool had already spent on
          CHAIN BROKEN, so running it with no arguments reported a tampered audit trail. Whatever
          else changes, no two of these may ever collapse onto one number.
        */
        var codes = new[]
        {
            VerifyCommand.Intact, VerifyCommand.Broken,
            VerifyCommand.NothingToVerify, VerifyCommand.Misconfigured, VerifyCommand.UsageError,
        };

        codes.Should().OnlyHaveUniqueItems(
            "an exit code shared by two meanings is a signal automation cannot read");
        VerifyCommand.Intact.Should().Be(0, "every runner treats 0 and only 0 as success");
    }

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
        /*
          THE COUNT AND THE RANGE MUST BE TELLABLE APART, which the first version of this test could
          not do: it passed 40,006 as BOTH the verified count and the highest sequence, so asserting
          the text contained "40,006" was satisfied by the range line alone. Deleting the count from
          the report entirely would have left this green -- the exact regression its own rationale
          says it exists to catch.

          Distinct numbers now, and they are distinct in a way that also matters: a chain whose
          sequences run 7..91,234 while only 40,006 rows verify is a chain with GAPS, and an
          operator reading those two numbers together is the only one who can notice.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(40_006, null, null), 7, 91_234);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Intact);
        lines.Should().Contain(
            line => line.Contains("40,006") && !line.Contains("Sequence range"),
            "the COUNT has to appear on its own line -- asserting only that the text contains it "
            + "lets the range line satisfy the assertion while the count is gone");
        lines.Should().Contain(
            line => line.Contains("Sequence range") && line.Contains("7") && line.Contains("91,234"),
            "and the range has to carry both ends, or it cannot be compared with anything");
        text.Should().Contain(
            "NOT prove",
            "tail truncation is undetectable by construction, and the tool must say so where the "
            + "operator reads the good news rather than only in the ADR");
    }
}
