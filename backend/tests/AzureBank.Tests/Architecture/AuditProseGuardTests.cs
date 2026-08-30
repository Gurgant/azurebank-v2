using System.Reflection;
using System.Text.RegularExpressions;
using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the audit-chain prose from drifting away from the audit-chain code.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ THIS IS A GUARD ABOUT DOCUMENTS, WHICH IS UNUSUAL, AND THE REASON IS MEASURED RATHER THAN
/// stylistic. The key-ring work changed shape five times — a ring, an upper boundary, a founding key,
/// a lower boundary, a typed exception — and every change invalidated sentences written for the
/// previous shape, in source comments, in printed console output, in an ADR, in a runbook, in two
/// derived documents and in the tests. Ten review rounds each found some and left others, and three
/// of the defects were a LATER commit of the same branch invalidating an EARLIER commit's prose.
/// </para>
/// <para>
/// A verdict an operator reads under pressure is part of the product. A runbook quoting output the
/// tool no longer prints sends somebody to check a setting that is fine, during the one incident the
/// page exists for. So two of these three assertions are about what the tool SAYS and where that
/// text is quoted, and the third is about names — a document naming a test that does not exist is a
/// pointer to nothing, which happened twice.
/// </para>
/// <para>
/// The cost is real and worth stating: this couples the test suite to the documentation tree, so
/// moving a file breaks a test for a reason that is not behaviour. That is accepted rather than
/// hidden — <see cref="RepoRoot"/> fails loudly when the tree is missing rather than skipping,
/// because a guard that quietly does nothing is the failure mode this whole branch is about.
/// </para>
/// </remarks>
public class AuditProseGuardTests
{
    /// <summary>Walks up from the test assembly until it finds the repository root.</summary>
    /// <remarks>
    /// Copied in shape from <c>SourceHygieneTests</c>, which established that a source-reading guard
    /// belongs in this folder and must FAIL rather than skip when it cannot find what it reads.
    /// </remarks>
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(
            because: "these guards read the sources and the documents; one that cannot run must say "
            + "so rather than pass");
        return dir!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepoRoot().FullName, Path.Combine(parts)));

    private const string Runbook = "docs/runbooks/audit-chain-unavailable.md";

    /*
      HEADLINES THE RUNBOOK DOES NOT COVER, WRITTEN DOWN SO THE ABSENCE IS A DECISION.

      All four belong to `anchor` and `export`, whose failure modes the page treats more briefly than
      `verify`'s, and all four predate the key ring. They are listed rather than quietly excluded so
      that a NEW headline cannot join them by accident: adding one means either documenting it or
      adding it here with a reason, and both are visible in review.
    */
    private static readonly string[] NotInTheRunbook =
    [
        "NOT RECORDED:",
        "CANNOT EXPORT:",
        "NOT EXPORTED:",
        "NOTHING TO EXPORT:",
    ];

    [Fact]
    public void EveryVerdictHeadlineTheToolCanPrint_IsAccountedForInTheRunbook()
    {
        /*
          THE HEADLINES ARE READ OUT OF THE TOOL, not listed here, which is the whole point. A list
          in this file would be a second place to state the same fact and would drift exactly as the
          runbook did. Anything the verifier can print as the first line of a verdict has to be
          findable on the page an operator opens when they see it.
        */
        var runbook = Read(Runbook);

        var headlines = new[] { "VerifyCommand.cs", "AnchorCommand.cs", "ExportCommand.cs" }
            .SelectMany(file => Regex.Matches(
                Read("backend", "tools", "AzureBank.AuditVerifier", "Commands", file),
                "\"([A-Z][A-Z]+(?: [A-Z]+)*:)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        headlines.Should().HaveCountGreaterThan(
            5, "if the extraction stops finding them the guard passes for the wrong reason");

        /*
          The colon is dropped before matching, on purpose. The page names some verdicts in running
          prose -- "**2** nothing to verify" in the exit-code list -- and the question this guard asks
          is whether an operator can FIND the verdict on the page, not whether the page reproduces the
          tool's punctuation. Requiring the colon would have made this fail on a page that documents
          the verdict perfectly well.
        */
        var undocumented = headlines
            .Where(h => !runbook.Contains(h.TrimEnd(':'), StringComparison.OrdinalIgnoreCase))
            .Where(h => !NotInTheRunbook.Contains(h))
            .ToList();

        undocumented.Should().BeEmpty(
            "a verdict the tool can print and the runbook never names is one an operator meets for "
            + "the first time during the incident it belongs to — either document it, or add it to "
            + "NotInTheRunbook with the reason");
    }

    [Fact]
    public void TheIntactVerdictQuotedInTheDeferredDocument_IsWhatTheToolPrints()
    {
        /*
          THIS EXACT QUOTATION DRIFTED TWICE. docs/deferred/anchoring-the-audit-trail.md reproduces
          the intact verdict verbatim under a heading that says "What is true today", and the verdict
          was rewritten twice on this branch — once when the ring narrowed the claim from
          Audit:ChainKey to the ring, and again when the epoch narrowed it from the ring to the one
          key whose epoch contains the row. Both times the quotation was caught by hand.

          Comparing the whole block rather than a phrase is deliberate: a phrase match would survive
          a rewrite that kept one sentence, which is precisely how the second drift looked.
        */
        var (_, lines) = VerifyCommand.Report(new AuditChainVerification(3, null, null), 1, 3);

        var quoted = lines
            .SkipWhile(l => !l.Contains("This proves no row was altered", StringComparison.Ordinal))
            .TakeWhile(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.TrimStart())
            .ToList();

        quoted.Should().NotBeEmpty("the extraction has to find the block, or this passes vacuously");

        var document = Read("docs", "deferred", "anchoring-the-audit-trail.md");

        foreach (var line in quoted)
        {
            document.Should().Contain(
                line,
                "the document reproduces this verdict as what the tool prints, so a line the tool "
                + "no longer prints makes it a transcript of nothing");
        }
    }

    /*
      THE FILES THIS GUARD POLICES. Scoped to the audit-chain corpus rather than the repository,
      because elsewhere the same shape catches error codes, index names and environment variables —
      SELF_TRANSFER_NOT_ALLOWED, IX_Accounts_AccountNumber, OTEL_EXPORTER_OTLP_ENDPOINT — which are
      not test names and never will be. Widening it is a separate decision with its own false
      positives to answer for.
    */
    private static readonly string[] AuditCorpus =
    [
        "backend/src/AzureBank.Infrastructure/Data/AuditChain.cs",
        "backend/src/AzureBank.Infrastructure/Data/AuditAnchorChain.cs",
        "backend/src/AzureBank.Infrastructure/Data/AuditKeyRingException.cs",
        "backend/src/AzureBank.Shared/Options/AuditOptions.cs",
        "backend/src/AzureBank.Shared/Options/RetiredChainKey.cs",
        "backend/src/AzureBank.Shared/Entities/AuditEvent.cs",
        "backend/src/AzureBank.Shared/Entities/AuditAnchor.cs",
        "backend/tools/AzureBank.AuditVerifier/Commands/VerifyCommand.cs",
        "backend/tools/AzureBank.AuditVerifier/Commands/AnchorCommand.cs",
        "backend/tools/AzureBank.AuditVerifier/Commands/ExportCommand.cs",
        "backend/tests/AzureBank.Tests/Unit/Data/AuditChainTests.cs",
        "backend/tests/AzureBank.Tests/Unit/Tools/AuditVerifierReportTests.cs",
        "backend/tests/AzureBank.Tests/Unit/Tools/UncoveredWindowTests.cs",
        "backend/tests/AzureBank.Tests/Unit/Tools/RealCompositionRootRefusalTests.cs",
        "docs/adr/0044-the-audit-trail-is-append-only-and-chained.md",
        "docs/runbooks/audit-chain-unavailable.md",
        "docs/deferred/anchoring-the-audit-trail.md",
        "docs/audit-trail-against-real-practice.md",
    ];

    [Fact]
    public void EveryTestNamedInTheAuditCorpus_Exists()
    {
        /*
          A DOCUMENT NAMING A TEST THAT DOES NOT EXIST IS A POINTER TO NOTHING, and this corpus has
          done it twice: an ADR cited a name truncated mid-word inside a code span, and a summary in
          AuditChainTests cited `ALegacyRowVerifies…`, which is not a prefix of any test in the suite.
          Both were invisible to every other instrument — the compiler does not read prose, and an
          elided name is exactly the shape a reader cannot check by eye.

          ELISIONS ARE ALLOWED AND ARE THE POINT. The corpus writes `SomeVeryLongTestName…` on
          purpose, and a prefix match is what makes that legal while still checking it resolves.

          The shape test is deliberately narrow: at least eighteen characters, and either an ellipsis
          or an underscore-separated segment of at least fourteen. Measured over the corpus, that
          admits all nineteen genuine citations and excludes `Trusted_Connection` and
          `ConsoleHost_history`, which are a connection-string keyword and a filename.
        */
        var root = RepoRoot().FullName;

        var declared = Directory
            .EnumerateFiles(Path.Combine(root, "backend", "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(p => Regex.Matches(
                File.ReadAllText(p),
                @"\b(?:public|private|internal)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\], ?]+\s+"
                + @"([A-Z][A-Za-z0-9_]{9,})\s*\(").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        declared.Should().HaveCountGreaterThan(
            200, "if the declaration scan comes back thin, every citation resolves against nothing");

        var unresolved = new List<string>();

        foreach (var relative in AuditCorpus)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"{relative} is in the policed corpus and must be there");

            var markdown = relative.EndsWith(".md", StringComparison.Ordinal);
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                var candidates = markdown
                    ? Regex.Matches(lines[i], "`([^`]+)`").Select(m => m.Groups[1].Value)
                    : trimmed.StartsWith("//", StringComparison.Ordinal)
                      || trimmed.StartsWith("*", StringComparison.Ordinal)
                      || trimmed.StartsWith("/*", StringComparison.Ordinal)
                        ? [lines[i]]
                        : [];

                foreach (var name in candidates
                             .SelectMany(c => Regex.Matches(c, @"[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)*…?")
                                 .Select(m => m.Value))
                             .Where(LooksLikeATestName))
                {
                    var stem = name.TrimEnd('…');
                    if (declared.Any(d => d.StartsWith(stem, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    unresolved.Add($"{relative}:{i + 1} names '{name}'");
                }
            }
        }

        unresolved.Should().BeEmpty(
            "prose that names a test is making a claim a reader will go and check; a name that "
            + "resolves to nothing costs them the trip and tells them nothing about what is true");
    }

    private static bool LooksLikeATestName(string candidate)
    {
        var stem = candidate.TrimEnd('…');
        if (stem.Length < 18)
        {
            return false;
        }

        if (candidate.EndsWith('…'))
        {
            return true;
        }

        var segments = stem.Split('_');
        return segments.Length >= 2 && segments.Max(s => s.Length) >= 14;
    }
}
