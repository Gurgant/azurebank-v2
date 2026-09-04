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
/// pointer to nothing, which happened three times on this branch.
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
        ["VerifyCommand.cs", "AnchorCommand.cs", "ExportCommand.cs", "EvidenceCommand.cs", "NotifyCommand.cs"];

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
    /// <para>
    /// ⚠️ A WARNING PREFIX MADE A HEADLINE INVISIBLE TOO, and that is the second correction. The
    /// pattern required an upper-case letter straight after the quote, so a literal opening
    /// <c>"⚠️ …"</c> matched nothing at all. Raised in review against
    /// <c>⚠️ UNCOVERED WINDOW: NEGATIVE…</c>, which turned out to be covered anyway — the plain
    /// <c>UNCOVERED WINDOW</c> is extracted from another line and the runbook documents the NEGATIVE
    /// case twice. What the blindness actually hid was <c>ExportCommand</c>'s
    /// <c>⚠️ THE ANCHOR CHAIN DID NOT VERIFY</c>, which no version of this guard had ever checked.
    /// Measured: widening it takes the extraction from 15 headlines to 16, and that one is the
    /// difference.
    /// </para>
    /// </remarks>
    private static readonly Regex Headline =
        new("\"(?:⚠️\\s*)?\\s?([A-Z][A-Z]+(?: [A-Z]+)*)", RegexOptions.Compiled);

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
    private static IEnumerable<string> CodeLines(string path) =>
        Classify(path)
            .Where(l => !l.IsComment && !l.Line.TrimStart().StartsWith('+'))
            .Select(l => l.Line);

    /// <summary>
    /// The ONE comment/code classifier both extractions run on.
    /// </summary>
    /// <remarks>
    /// ⚠️ THERE USED TO BE TWO, AND A COMMENT CLAIMED THEY SHARED A STATE MACHINE. They did not:
    /// <c>CodeLines</c> and <c>CommentLines</c> were separate loops with a private
    /// <c>inBlockComment</c> each, so the divergence that sentence said was prevented was exactly
    /// what the shape allowed — and had already happened once, when the block-comment fix landed in
    /// one of them and not the other. Now there is one, and the claim is true because there is
    /// nothing left to diverge.
    /// </remarks>
    private static IEnumerable<(string Line, bool IsComment)> Classify(string path)
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

                yield return (line, true);
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);
                yield return (line, true);
                continue;
            }

            yield return (
                line,
                trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'));
        }
    }

    /// <summary>
    /// The inverse of <see cref="CodeLines"/>: the comment lines, block interiors included.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE CITATION SCAN CLASSIFIED COMMENTS BY A PER-LINE PREFIX, which is the defect this file
    /// had already fixed for the headline extraction and left standing here. This corpus writes
    /// <c>/* … */</c> with unprefixed interior lines, so the scan never read inside a block comment
    /// — and one was hiding a dead citation the whole time: <c>ExportCommand</c> named a test that
    /// does not exist, inside a block, and the guard written to catch exactly that could not see it.
    /// Both extractions now run on <see cref="Classify"/>, so there is no second state machine left
    /// to diverge.
    /// </remarks>
    private static IEnumerable<string> CommentLines(string path) =>
        Classify(path).Where(l => l.IsComment).Select(l => l.Line);

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

        var perFile = CommandFiles.ToDictionary(
            file => file,
            file => CodeLines(
                    Path.Combine(RepoRoot().FullName, "backend", "tools", "AzureBank.AuditVerifier",
                        "Commands", file))
                .SelectMany(line => Headline.Matches(line).Select(m => m.Groups[1].Value))
                .ToList(),
            StringComparer.Ordinal);

        var headlines = perFile.Values
            .SelectMany(found => found)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        /*
          ⚠️ THE BREAKDOWN IS COMPUTED, NOT NARRATED, AND THAT IS THE POINT OF THIS VARIABLE. Every
          previous version of this block wrote the per-file counts into the prose by hand, and every
          time the extraction changed they were left behind -- most recently in the very commit that
          widened the pattern and moved the assertion, so a reader who made this test redden was
          handed two different derivations of the same number, in one file, thirty lines apart. The
          numbers are read out of this run now; the prose keeps only the reasoning, which does not
          drift because it is not a measurement.
        */
        var shared = perFile.Values
            .SelectMany(found => found.Distinct(StringComparer.Ordinal))
            .GroupBy(headline => headline, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        var breakdown = string.Join(
                "; ",
                perFile.Select(file =>
                    $"{file.Key} {file.Value.Distinct(StringComparer.Ordinal).Count()} distinct of "
                    + $"{file.Value.Count} matched"))
            + $". Shared: {string.Join(", ", shared.Select(g => $"{g.Key} in {g.Count()} files"))}"
            + $", removing {shared.Sum(group => group.Count() - 1)} duplicate occurrences";

        /*
          EXACT, NOT A FLOOR, AND THE FLOOR IS WHAT WAS WRONG WITH IT. This asserted ">= 12" with a
          reason claiming "VerifyCommand alone contributes six", both numbers written from memory,
          and the six was false.

          The TOTAL is written here because it is the thing being asserted: a number a reader must
          be able to disagree with. Everything under it -- what each file contributes, how many
          matches precede the collapse -- is computed above and printed in the failure message,
          because those are measurements and this block has proved it cannot hold one. What does not
          move is the SHAPE: a single Distinct runs after both SelectMany calls, over one flattened
          sequence covering all three files, so the pipeline collapses matches to distinct headlines
          in ONE step and the sum of the per-file distinct counts is never an intermediate total the
          code computes. The difference between that sum and the asserted total is entirely the
          duplicate occurrences contributed by headlines more than one verb prints -- INTERRUPTED,
          CHAIN BROKEN and NO VERDICT as this stands. The list and the arithmetic are in the message
          for a reason beyond habit: a headline can START being shared without the asserted total
          moving at all, so that is a change this paragraph would never have been made to notice.

          (⚠️ It said "FOUR headlines are shared" and then named THREE. Four is the number of
          duplicate OCCURRENCES the Distinct removes -- INTERRUPTED appears in three files and
          contributes two, the other two contribute one each. The paragraph exists so a reader can
          reconcile the per-file counts with the total, and it named the one quantity that makes the
          reconciliation impossible. Raised in review, two sentences after this same paragraph
          declares that measurements belong in the message because this block cannot hold one.)

          ⚠️ THIS PARAGRAPH HELD THOSE NUMBERS UNTIL A REVIEW CAUGHT THEM STALE, IN THE COMMIT THAT
          WIDENED THE PATTERN AND MOVED THE ASSERTION. The file whose entire subject is prose
          drifting from code drifted from its own assertion, thirty lines apart, in one commit. It is
          the strongest argument available for the split above: an assertion has an oracle and a
          paragraph does not, so the paragraph should not be carrying arithmetic.

          A floor of twelve also left a hole the check below cannot close. That check passes a
          headline the runbook merely CONTAINS, and THREE of the sixteen -- INTERRUPTED, ANCHOR and
          EXPORTED -- are single words a page on this subject contains by accident. The check is
          Contains, so what matters is occurrences: "anchor" occurs 75 times in the runbook, none of
          them the verdict; "exported" once, in a sentence about migrations; "interrupted" twice,
          once in prose about freeing a BSTR and once in the exit-code list, where the mention is
          deliberate but lowercase. (65 was written here first: that is the number of LINES carrying
          "anchor", a different quantity from the one the two siblings in the same sentence report.)

          So two of the three are documented only by coincidence and the third is documented for a
          reason this check cannot see either, and a NEW single-word verdict would be waved through
          as documented.

          ⚠️ AND THE SIXTEENTH HEADLINE ARRIVED THE SAME WAY. Widening the regex to see a warning
          prefix surfaced ExportCommand's "⚠️ THE ANCHOR CHAIN DID NOT VERIFY", which no version of
          this guard had checked. The runbook did contain that phrase -- in lower case, describing a
          line VerifyCommand prints, not this one -- so it would have passed on a case-insensitive
          coincidence between two different verbs' output. It is documented deliberately now, in the
          export section, and the coincidence is no longer what carries it.

          The exact count is what notices it: a new verdict cannot arrive without this number
          moving, whatever the runbook happens to say. (It moved 28 -> 29 on 2026-09-04 when the
          verb learned to claim under a lease and gained LEASE LAPSED, ADR-0048 — documented in the
          notify section of the runbook, deliberately, before this number was touched.)
        */
        headlines.Should().HaveCount(
            29,
            "the extraction found {0}, from {1}. FEWER means the regex or the comment stripper "
            + "regressed -- the first version of this guard shipped blind to four verdicts for want "
            + "of a colon, and the second to any headline behind a warning prefix. MORE means a "
            + "verdict was added, and the point of reddening on that is the check below: it accepts "
            + "a headline the runbook CONTAINS, which single words like ANCHOR and EXPORTED satisfy "
            + "by coincidence", headlines.Count, breakdown);

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
          THIS EXACT QUOTATION DRIFTED THREE TIMES. docs/deferred/anchoring-the-audit-trail.md
          reproduces the intact verdict verbatim under a heading that says "What is true today", and
          three commits on this branch rewrote that verdict: the ring narrowed the claim from
          Audit:ChainKey to the ring; a second commit rewrote "narrows what it can WRITE" into
          "narrows what a verification ACCEPTS from it"; and a third narrowed the ring to the one key
          whose epoch contains the row. All three times the quotation was caught by hand, and the
          middle one is the one this comment used to omit -- a rewrite of the sentence's meaning
          rather than of the key it names, which is exactly the kind a reader skims past.

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
      because elsewhere the shape catches long index names and framework types this repo does not
      declare — IX_Transactions_TransactionNumber, SqlServerTransientExceptionDetector,
      OpenApiSecuritySchemeReference — and because widening it means owning what it then finds.

      ⚠️ THE THREE EXAMPLES HERE USED TO BE SELF_TRANSFER_NOT_ALLOWED, IX_Accounts_AccountNumber AND
      OTEL_EXPORTER_OTLP_ENDPOINT, AND LooksLikeASymbol DISCARDS ALL THREE. An underscored name needs
      a segment of 14 characters; their longest are 8, 13 and 8, so not one of them can ever reach
      the resolution step, at this scope or any other. The remark on LooksLikeASymbol 170 lines below
      says so in as many words — "the SCREAMING_SNAKE_CASE identifiers that trip a naive version …
      fail all three" — so this file contradicted itself, and the sentence arguing for the SCOPE was
      the half that was wrong. IX_Accounts_AccountNumber misses by one character; the index name that
      does get through is IX_Transactions_TransactionNumber, at 17.

      Measured repo-wide by porting this guard's logic over every .cs and .md file: a couple of
      dozen unresolved names, NOT the fourteen this comment used to claim, and NOT zero real ones.
      No exact figure is pinned here on purpose — it moves with every document edit, and the version
      of this sentence that did pin one was already stale one commit later. What does not move is
      the counter-example: at least two of them are genuine dead citations of exactly the kind this
      guard exists to catch — AzureBankDbContext.cs names AuditChainRetryingStrategySqlServerTests
      in the present tense and no such class exists, and docs/adr/0041 names an elided
      Transfer_WithNoPinField… that no test answers to. Both are older than this branch, so neither
      is fixed here; the point is that the sentence claiming a clean widening measured nothing.
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
        "backend/tools/AzureBank.AuditVerifier/Commands/EvidenceCommand.cs",
        "backend/tools/AzureBank.AuditVerifier/Commands/NotifyCommand.cs",
        "backend/tests/AzureBank.Tests/Unit/Data/AuditChainTests.cs",
        "backend/tests/AzureBank.Tests/Unit/Tools/AuditVerifierReportTests.cs",
        "backend/tests/AzureBank.Tests/Unit/Tools/UncoveredWindowTests.cs",
        "backend/tests/AzureBank.Tests/Unit/Tools/RealCompositionRootRefusalTests.cs",
        "docs/adr/0044-the-audit-trail-is-append-only-and-chained.md",
        "docs/adr/0045-the-enrolment-notice-rides-the-enrolment-and-stops-at-a-pickup-directory.md",
        "docs/runbooks/audit-chain-unavailable.md",
        "docs/runbooks/pin-enrolment-repudiated.md",
        "docs/deferred/anchoring-the-audit-trail.md",
        "docs/deferred/relaying-the-enrolment-notice.md",
        "docs/audit-trail-against-real-practice.md",
    ];

    /// <summary>
    /// Symbols the corpus cites that are declared outside this solution.
    /// </summary>
    /// <remarks>
    /// Listed rather than pattern-excluded, so that a citation of something that does not exist
    /// anywhere cannot hide behind a rule about interfaces or framework namespaces.
    /// </remarks>
    private static readonly string[] DeclaredElsewhere =
        ["IDbContextOptionsConfiguration", "SqlServerRetryingExecutionStrategy"];

    [Fact]
    public void EverySymbolNamedInTheAuditCorpus_Exists()
    {
        /*
          A DOCUMENT NAMING A TEST THAT DOES NOT EXIST IS A POINTER TO NOTHING, and this corpus has
          done it THREE times: an ADR cited an elided name inside a code span; a summary in
          AuditChainTests cited `ALegacyRowVerifies…`, which is not a prefix of any test in the
          suite; and ExportCommand cited one from inside a block comment, where this guard's own
          citation scan could not see it until the scan was fixed. All three were invisible to every
          other instrument in the build — the compiler does not read prose, and an elided name is
          exactly the shape a reader cannot check by eye. The third is recorded in the remark on
          Classify, by the same commit that left "twice" standing here.

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

            // Markdown cites inside backticks; C# cites inside comments, block interiors included.
            var prose = markdown
                ? File.ReadLines(path).SelectMany(l =>
                    Regex.Matches(l, "`([^`]+)`").Select(m => m.Groups[1].Value))
                : CommentLines(path);

            /*
              A TOKEN THIS FILE USES AS DATA IS NOT A CITATION. AuditChainTests renders a hashed
              payload inside a block comment, event name and all, and the event name is a long
              PascalCase word — indistinguishable by shape from a test name, and not one. Anything
              the same file also writes as a string literal is a value the corpus handles rather
              than a symbol it names, so it is not a claim to check.
            */
            var literals = markdown
                ? new HashSet<string>(StringComparer.Ordinal)
                : Regex.Matches(File.ReadAllText(path), "\"([^\"\\n]{4,})\"")
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal);

            foreach (var name in prose
                         .SelectMany(c => Regex.Matches(c, @"[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)*…?")
                             .Select(m => m.Value))
                         .Where(LooksLikeASymbol))
            {
                var stem = name.TrimEnd('…', '.');

                // Matched EXACTLY, not by containment: a token the file also writes as a whole
                // literal is data. Anything narrower would let a real citation hide inside a
                // longer message string that happens to contain it.
                if (literals.Contains(stem))
                {
                    continue;
                }

                checkedCitations++;
                if (declared.Any(d => d.StartsWith(stem, StringComparison.Ordinal)))
                {
                    continue;
                }

                unresolved.Add($"{relative} names '{name}'");
            }
        }

        /*
          ⚠️ THE OTHER GUARD ON THE GUARD, and the one the first version was missing. It asserted
          only that the DECLARED set was large — the right-hand side of the comparison. If the
          citation regex or the shape filter stopped matching, the left-hand side would be empty and
          the test would pass having checked nothing at all.
        */
        checkedCitations.Should().BeGreaterThanOrEqualTo(
            45,
            "the scan checked {0} citations across {1} files. THE FLOOR SITS JUST UNDER THE MEASURED "
            + "VALUE ON PURPOSE — 45 against 49 observed by raising this number until the assertion "
            + "printed it. It was first written at 20, and 20 is the defect this reason exists to "
            + "name: a floor set far below the truth passes with most of the scan dead, and this one "
            + "did exactly that while the citation scan was skipping every block comment -- measured "
            + "by putting that regression back, the scan then sees 40, and 40 clears 20 twice over. "
            + "Four of slack absorbs a sentence being reworded; it does not absorb the scan going "
            + "quiet",
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

    /*
      ⚠️ WHAT THAT 30 COSTS, MEASURED, BECAUSE A FLOOR THIS HIGH CHECKS FEW CITATIONS. A
      non-underscored name needs thirty characters, which admits long test names and almost nothing
      else: class names and member names in the corpus are shorter and are never checked. So this
      guard catches a dead TEST citation and would not catch a renamed class the runbook points an
      operator at.

      Lowering it is not a one-line change, which is why it is written down rather than done. At 24
      the scan surfaces PreviousAnchorPayloadHash -- a real property of AuditAnchor, unresolved only
      because the declared set collects types and methods and not properties. At 20 it surfaces
      SaveChangesInterceptor, an EF type this repo does not declare. Both are false alarms, and a
      guard that cries wolf is a guard somebody turns off. Widening it means collecting properties
      into the declared set and extending DeclaredElsewhere for framework types, together, in one
      change that can be reviewed as a whole.
    */
}
