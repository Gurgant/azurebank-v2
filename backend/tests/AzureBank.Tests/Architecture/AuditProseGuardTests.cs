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
/// derived documents and in the tests. Eleven review rounds each found some and left others, and
/// several defects were a LATER commit of the same branch invalidating an EARLIER commit's prose.
/// </para>
/// <para>
/// A verdict an operator reads under pressure is part of the product. A runbook quoting output the
/// tool no longer prints sends somebody to check a setting that is fine, during the one incident the
/// page exists for. So two of these three assertions are about what the tool SAYS and where that
/// text is quoted, and the third is about names — a document naming a test that does not exist is a
/// pointer to nothing, which happened twice.
/// </para>
/// <para>
/// ⚠️ EVERY ASSERTION HERE DERIVES ITS INPUT FROM THE CODE, and every extraction carries a count
/// assertion, because the failure mode of a guard like this is not a false alarm — it is finding
/// nothing and reporting success. The first version of this file had three such holes, all found by
/// an adversarial sweep rather than by running it: a headline regex that required a colon and so
/// could not see <c>CHAIN BROKEN at sequence n</c>, a transcript comparison that stopped at the
/// first blank line so inserting one shrank what was checked, and a threshold set below what a
/// single file contributes.
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

    private static readonly string[] CommandFiles =
        ["VerifyCommand.cs", "AnchorCommand.cs", "ExportCommand.cs"];

    /*
      HEADLINES THE RUNBOOK DOES NOT COVER, WRITTEN DOWN SO THE ABSENCE IS A DECISION.

      All four belong to `anchor` and `export`, whose failure modes the page treats more briefly than
      `verify`'s, and all four predate the key ring. They are listed rather than quietly excluded so
      that a NEW headline cannot join them by accident: adding one means either documenting it or
      adding it here with a reason, and both are visible in review.
    */
    private static readonly string[] NotInTheRunbook =
        ["CANNOT EXPORT", "NOT EXPORTED", "NOT RECORDED", "NOTHING TO EXPORT"];

    /// <summary>
    /// The first all-caps run inside a string literal, which is what a verdict headline looks like.
    /// </summary>
    /// <remarks>
    /// No trailing colon is required, and that is the correction: the first version demanded one and
    /// therefore could not see <c>CHAIN BROKEN at sequence {n}</c>, <c>ANCHOR {n} recorded.</c>,
    /// <c>GAP MARKER {n} recorded:</c> or <c>EXPORTED {n} anchor records to {path}</c> — four
    /// verdicts an operator meets, invisible to the guard written to find them.
    /// </remarks>
    private static readonly Regex Headline =
        new("\"\\s?([A-Z][A-Z]+(?: [A-Z]+)*)", RegexOptions.Compiled);

    /// <summary>
    /// Yields the lines of a C# file that are neither comments nor string continuations.
    /// </summary>
    /// <remarks>
    /// Block comments are tracked with a state machine rather than by a per-line prefix test, which
    /// is what the first version did — this corpus writes <c>/* … */</c> with unprefixed interior
    /// lines, so a prefix test let prose like "A FAILED WRITE IS ..." through as a headline.
    /// Continuations are skipped because a headline is the FIRST line of a verdict; a line beginning
    /// with <c>+</c> is the middle of one.
    /// </remarks>
    private static IEnumerable<string> CodeLines(string path)
    {
        var inBlockComment = false;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();

            if (inBlockComment)
            {
                if (trimmed.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }

                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*')
                || trimmed.StartsWith('+'))
            {
                continue;
            }

            yield return line;
        }
    }

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

        var headlines = CommandFiles
            .SelectMany(file => CodeLines(
                Path.Combine(RepoRoot().FullName, "backend", "tools", "AzureBank.AuditVerifier",
                    "Commands", file)))
            .SelectMany(line => Headline.Matches(line).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        headlines.Should().HaveCountGreaterThanOrEqualTo(
            12,
            "the extraction found {0}; VerifyCommand alone contributes six, so a threshold below "
            + "that would let a broken regex pass while checking almost nothing — which is how the "
            + "first version of this guard shipped", headlines.Count);

        /*
          The colon is not part of the match and is not required in the page either. Some verdicts
          are named in running prose — "**2** nothing to verify" in the exit-code list — and the
          question this guard asks is whether an operator can FIND the verdict on the page, not
          whether the page reproduces the tool's punctuation.
        */
        var undocumented = headlines
            .Where(h => !runbook.Contains(h, StringComparison.OrdinalIgnoreCase))
            .Where(h => !NotInTheRunbook.Contains(h, StringComparer.Ordinal))
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

        /*
          ⚠️ THE COUNT IS THE GUARD ON THE GUARD. The extraction stops at the first blank line, so a
          single string.Empty inserted into the tool's intact block would shrink what is compared to
          two lines and this test would still pass — measured, seven of nine lines went unchecked.
          The verdict is nine lines long; anything materially shorter means the extraction broke, not
          that the verdict did.
        */
        quoted.Should().HaveCountGreaterThanOrEqualTo(
            8,
            "the extraction found {0} lines of a verdict that is nine long — a shorter block means "
            + "the extraction stopped early, and comparing what is left proves almost nothing",
            quoted.Count);

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
      not symbols and never will be. Measured repo-wide: fourteen false positives against zero real
      ones. Widening it is a separate decision with those to answer for.
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

    /// <summary>
    /// Symbols the corpus cites that are declared outside this solution.
    /// </summary>
    /// <remarks>
    /// Listed rather than pattern-excluded, so that a citation of something that does not exist
    /// anywhere cannot hide behind a rule about interfaces or framework namespaces.
    /// </remarks>
    private static readonly string[] DeclaredElsewhere = ["IDbContextOptionsConfiguration"];

    [Fact]
    public void EverySymbolNamedInTheAuditCorpus_Exists()
    {
        /*
          A DOCUMENT NAMING A TEST THAT DOES NOT EXIST IS A POINTER TO NOTHING, and this corpus has
          done it twice: an ADR cited a name truncated mid-word inside a code span, and a summary in
          AuditChainTests cited `ALegacyRowVerifies…`, which is not a prefix of any test in the
          suite. Both were invisible to every other instrument — the compiler does not read prose,
          and an elided name is exactly the shape a reader cannot check by eye.

          ELISIONS ARE ALLOWED AND ARE THE POINT. The corpus writes `SomeVeryLongTestName…` on
          purpose, and a prefix match is what makes that legal while still checking it resolves.

          ⚠️ IT RESOLVES AGAINST TYPES AS WELL AS METHODS, which the first version did not, so it
          silently skipped every citation without an underscore — including
          `TheEventInventoryThisAdrStatesIsStillTheOneInTheSource`, a real test the ADR names twice.
          A guard that skips what it cannot classify reports success for the wrong reason.
        */
        var root = RepoRoot().FullName;

        var declared = Directory
            .EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(p =>
            {
                var text = File.ReadAllText(p);
                return Regex.Matches(
                        text,
                        @"\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?"
                        + @"(?:sealed\s+)?(?:partial\s+)?(?:class|record|interface|struct|enum)\s+"
                        + @"([A-Za-z_]\w+)")
                    .Select(m => m.Groups[1].Value)
                    .Concat(Regex.Matches(
                            text,
                            @"\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?"
                            + @"[\w<>\[\], ?]+\s+([A-Z][A-Za-z0-9_]{9,})\s*\(")
                        .Select(m => m.Groups[1].Value));
            })
            .Concat(DeclaredElsewhere)
            .ToHashSet(StringComparer.Ordinal);

        declared.Should().HaveCountGreaterThan(
            800, "if the declaration scan comes back thin, every citation resolves against nothing");

        var unresolved = new List<string>();
        var checkedCitations = 0;

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
                      || trimmed.StartsWith('*')
                      || trimmed.StartsWith("/*", StringComparison.Ordinal)
                        ? [lines[i]]
                        : [];

                foreach (var name in candidates
                             .SelectMany(c => Regex.Matches(c, @"[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)*…?")
                                 .Select(m => m.Value))
                             .Where(LooksLikeASymbol))
                {
                    checkedCitations++;
                    var stem = name.TrimEnd('…');
                    if (declared.Any(d => d.StartsWith(stem, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    unresolved.Add($"{relative}:{i + 1} names '{name}'");
                }
            }
        }

        /*
          ⚠️ THE OTHER GUARD ON THE GUARD, and the one the first version was missing. It asserted
          only that the DECLARED set was large — the right-hand side of the comparison. If the
          citation regex or the shape filter stopped matching, the left-hand side would be empty and
          the test would pass having checked nothing at all.
        */
        checkedCitations.Should().BeGreaterThanOrEqualTo(
            20,
            "the scan checked {0} citations across {1} files; the corpus carries about twenty-six, "
            + "so materially fewer means the citation scan broke rather than the corpus improving",
            checkedCitations, AuditCorpus.Length);

        unresolved.Should().BeEmpty(
            "prose that names a symbol is making a claim a reader will go and check; a name that "
            + "resolves to nothing costs them the trip and tells them nothing about what is true");
    }

    /// <summary>
    /// Whether a citation looks like a symbol this repository declares rather than an error code,
    /// an index name or an environment variable.
    /// </summary>
    /// <remarks>
    /// Three shapes, each measured against the corpus: anything ELIDED, because an elision is the
    /// case a reader cannot check by eye and is why this guard exists; an underscore-separated name
    /// with a long segment, which is this suite's test-naming convention; and a long name with no
    /// underscore at all, which is how the corpus cites test CLASSES and production members. The
    /// SCREAMING_SNAKE_CASE identifiers that trip a naive version — every segment upper-case — fail
    /// all three.
    /// </remarks>
    private static bool LooksLikeASymbol(string candidate)
    {
        var stem = candidate.TrimEnd('…');

        if (candidate.EndsWith('…'))
        {
            return stem.Length >= 12;
        }

        var segments = stem.Split('_');
        if (segments.Length >= 2)
        {
            return stem.Length >= 18 && segments.Max(s => s.Length) >= 14;
        }

        return stem.Length >= 30;
    }
}
