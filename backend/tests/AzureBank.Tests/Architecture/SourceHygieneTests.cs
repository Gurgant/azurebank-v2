using System.Text.RegularExpressions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps invisible characters out of the source.
/// </summary>
/// <remarks>
/// <para>
/// A control character in a source file is legal and invisible. On PR #118 a line in
/// <c>MoneyFormattingTests</c> carried a literal <c>U+0008</c> BACKSPACE where a backslash-b was
/// meant. C# verbatim strings do not process escapes, so the byte went into a regex as a character
/// to match, and the guard built on that regex could never match anything.
/// </para>
/// <para>
/// The compiler accepts it: a backspace in a regex is legal and means "match a backspace".
/// <c>grep</c> renders it as nothing, and the file reads as the intended text. <c>od -c</c> shows
/// it, because there the byte prints as ONE token where real backslashes print as two.
/// </para>
/// <para>
/// So this test reads every hand-written source file and fails on any control character other
/// than tab, carriage return and line feed.
/// </para>
/// </remarks>
public class SourceHygieneTests
{
    /// <summary>
    /// Hand-written source only. Generated artefacts are excluded not to be lenient but because they
    /// are not edited: a control character there would be the generator's doing, and this rule cannot
    /// act on it.
    /// </summary>
    private static readonly string[] ScannedFolders =
    [
        Path.Combine("backend", "src"),
        Path.Combine("backend", "tests"),
        Path.Combine("frontend", "src"),
        "scripts",
    ];

    private static readonly string[] ScannedExtensions = [".cs", ".ts", ".tsx", ".mjs", ".js"];

    /// <summary>
    /// Tab, carriage return and line feed are the three that legitimately appear in source. Every
    /// other Unicode control character is one no editor puts there on purpose.
    /// </summary>
    /// <remarks>
    /// <c>char.IsControl</c> rather than a comparison against U+0020: that comparison stops at
    /// U+001F and accepts U+007F DELETE and the whole U+0080-U+009F C1 block. Raised by CodeRabbit
    /// on PR #119. Measured before widening: the repository holds no character in U+007F-U+009F,
    /// so nothing legitimate is caught.
    /// </remarks>
    private static bool IsForbiddenControlCharacter(char c) =>
        char.IsControl(c) && c != (char)0x09 && c != (char)0x0D && c != (char)0x0A;

    [Fact]
    public void NoSourceFileCarriesAnInvisibleControlCharacter()
    {
        var root = RepositoryFiles.Root();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var relative in ScannedFolders)
        {
            var folder = Path.Combine(root.FullName, relative);
            Directory.Exists(folder).Should().BeTrue(because: $"expected to scan {folder}");

            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (!ScannedExtensions.Contains(Path.GetExtension(file))
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                scanned++;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var found = lines[i].Where(IsForbiddenControlCharacter).ToArray();
                    if (found.Length > 0)
                    {
                        var codes = string.Join(", ", found.Select(c => $"U+{(int)c:X4}"));
                        offenders.Add($"{Path.GetRelativePath(root.FullName, file)}:{i + 1}  {codes}");
                    }
                }
            }
        }

        scanned.Should().BeGreaterThan(100,
            "a scan that reads nothing reports clean forever, and nothing distinguishes that from a "
            + "scan that read everything — the same liveness the sibling guards now assert");

        offenders.Should().BeEmpty(
            "a control character in source is invisible to the compiler, to grep and to a code review, "
            + "and it silently changed the meaning of a regex once already");
    }

    /// <summary>
    /// One <c>&lt;summary&gt;</c> per documentation comment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twice on PR #198, a member ended up carrying two consecutive <c>&lt;summary&gt;</c> blocks:
    /// once on <c>TransferService.PrepareTransferAttemptAsync</c>, when a body became a delegation
    /// and picked up a second block, and once here in <c>SecurityEventConstantTests</c>, when a
    /// helper was inserted BETWEEN a doc comment and the method it described — which left the
    /// helper holding both and the test holding none. Both blocks reach the generated XML under
    /// that one member: a reader finds two explanations on it, and none where the second belonged.
    /// </para>
    /// <para>
    /// The compiler does not say so. Documentation generation is on for every project here
    /// (<c>backend/Directory.Build.props</c>), and a member with two <c>&lt;summary&gt;</c> blocks
    /// still builds in Release with zero warnings (measured on 2026-10-12 on a member of
    /// <c>AzureBank.Shared</c>). Both instances on PR #198 built clean and were caught by a
    /// reviewer reading the diff. That is the same shape as the control-character rule above — a
    /// defect every normal instrument is blind to — which is why it lives in this file.
    /// </para>
    /// <para>
    /// A documentation comment is a contiguous run of <c>///</c> lines; two <c>&lt;summary&gt;</c>
    /// openings inside one run is the defect. <c>&lt;/summary&gt;</c> is not counted, so a
    /// single-line <c>/// &lt;summary&gt;text&lt;/summary&gt;</c> is fine.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoDocumentationCommentCarriesTwoSummaries()
    {
        var root = RepositoryFiles.Root();
        var offenders = new List<string>();
        var runs = 0;

        foreach (var relative in ScannedFolders)
        {
            var folder = Path.Combine(root.FullName, relative);
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                var runStart = -1;
                var summaries = 0;

                for (var i = 0; i <= lines.Length; i++)
                {
                    var isDoc = i < lines.Length && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal);
                    if (isDoc)
                    {
                        if (runStart < 0) { runStart = i; summaries = 0; runs++; }
                        if (lines[i].Contains("<summary>", StringComparison.Ordinal)) { summaries++; }
                        continue;
                    }

                    if (runStart >= 0 && summaries > 1)
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(root.FullName, file)}:{runStart + 1} carries {summaries}");
                    }

                    runStart = -1;
                }
            }
        }

        runs.Should().BeGreaterThan(200,
            "a scan that reads nothing reports clean forever; this repository documents far more "
            + "than two hundred members");

        offenders.Should().BeEmpty(
            "two <summary> blocks on one member both reach the generated XML under it, and the "
            + "compiler is silent about it with documentation generation on");
    }

    /// <summary>
    /// The comment lines of production source that carry a date, the words "review round" or a
    /// warning sign, counted per file: the count of a file may fall and may not rise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A date or a review round says where a comment came from, and AGENTS.md rules out both. The
    /// warning sign is counted beside them because it shouts as capitals do, and the rule rules
    /// those out too. The agents that write part of the code start from that page, so a rule that
    /// nothing checks would be broken again at the next change.
    /// </para>
    /// <para>
    /// Production source is every C# file of <c>backend/src</c> and the <c>.ts</c> and <c>.tsx</c>
    /// files of <c>frontend/src</c> other than tests, declaration files and the folders
    /// <c>test</c>, <c>mocks</c>, <c>contract</c>, <c>integration</c> and <c>api/generated</c>. A
    /// line counts once, however many of the three it holds. Capitals for emphasis are not
    /// counted, because SQL keywords and error codes in comments would drown them: the rule covers
    /// them and a review reads them.
    /// </para>
    /// <para>
    /// <see cref="MarkedCommentLinesAllowed"/> holds the count of each file that has any, and a file
    /// that is not in it has none allowed. A failing run names each file that grew, with its count
    /// and the count allowed. A change that removes such lines lowers the entry of each file it
    /// touched to the count that file now holds, and takes a file at zero out. The lines that are
    /// left wait for the next change to their file.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, int> MarkedCommentLinesAllowed = new(StringComparer.Ordinal)
    {
        ["backend/src/AzureBank.Api/Controllers/AccountController.cs"] = 1,
        ["backend/src/AzureBank.Api/Controllers/AuthController.cs"] = 1,
        ["backend/src/AzureBank.Api/Controllers/TransferController.cs"] = 1,
        ["backend/src/AzureBank.Api/Extensions/ServiceCollectionExtensions.cs"] = 4,
        ["backend/src/AzureBank.Api/Handlers/AppExceptionHandler.cs"] = 1,
        ["backend/src/AzureBank.Api/Middleware/OversizedBodyDrain.cs"] = 2,
        ["backend/src/AzureBank.Api/Middleware/ServiceCredentialMiddleware.cs"] = 1,
        ["backend/src/AzureBank.Api/ModelBinding/EmptyQueryValueRejectingBinder.cs"] = 2,
        ["backend/src/AzureBank.Api/Observability/RequestLogRoute.cs"] = 2,
        ["backend/src/AzureBank.Api/Program.cs"] = 6,
        ["backend/src/AzureBank.Api/Services/AuditKeyRingStartupCheck.cs"] = 2,
        ["backend/src/AzureBank.Api/Services/Implementations/AccountService.cs"] = 10,
        ["backend/src/AzureBank.Api/Services/Implementations/DailyOutflowLimitService.cs"] = 2,
        ["backend/src/AzureBank.Api/Services/Implementations/RefreshTokenService.cs"] = 1,
        ["backend/src/AzureBank.Api/Services/Implementations/StepUpAuthorizationService.cs"] = 1,
        ["backend/src/AzureBank.Api/Services/Implementations/TransactionService.cs"] = 3,
        ["backend/src/AzureBank.Api/Services/Implementations/UserService.cs"] = 1,
        ["backend/src/AzureBank.Api/Services/RefreshTokenCleanupService.cs"] = 1,
        ["backend/src/AzureBank.Api/Transformers/BusinessRulesDocumentTransformer.cs"] = 8,
        ["backend/src/AzureBank.Api/Transformers/NotFoundResponseTransformer.cs"] = 1,
        ["backend/src/AzureBank.Api/Transformers/UnsupportedMediaTypeResponseTransformer.cs"] = 1,
        ["backend/src/AzureBank.Bff/Middleware/AuthLevelMiddleware.cs"] = 1,
        ["backend/src/AzureBank.Bff/Middleware/FetchMetadataMiddleware.cs"] = 1,
        ["backend/src/AzureBank.Bff/Middleware/SecurityHeadersMiddleware.cs"] = 1,
        ["backend/src/AzureBank.Bff/Observability/RequestLogRoute.cs"] = 2,
        ["backend/src/AzureBank.Bff/Options/ProxyOptionsValidator.cs"] = 1,
        ["backend/src/AzureBank.Bff/Program.cs"] = 5,
        ["backend/src/AzureBank.Bff/Services/SessionStampWatcher.cs"] = 2,
        ["backend/src/AzureBank.Bff/StrictForwardedFor.cs"] = 1,
        ["backend/src/AzureBank.Bff/Transforms/BearerTokenTransformProvider.cs"] = 1,
        ["backend/src/AzureBank.Functions.NoticeRelay/NoticeRelayHostState.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Data/AuditAnchorChain.cs"] = 3,
        ["backend/src/AzureBank.Infrastructure/Data/AuditChain.cs"] = 19,
        ["backend/src/AzureBank.Infrastructure/Data/AuditKeyRingException.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Data/AzureBankDbContext.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Data/Configurations/AuditAnchorConfiguration.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Data/Configurations/DemoCopyConfiguration.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Data/Configurations/SubscriberNoticeConfiguration.cs"] = 2,
        ["backend/src/AzureBank.Infrastructure/Data/DesignTimeDbContextFactory.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Migrations/20260903000526_AddSubscriberNotices.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Migrations/20260909173646_AddSubscriberNoticeAuditEventId.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Migrations/20260928134643_AddRefreshTokenRevokedReason.cs"] = 1,
        ["backend/src/AzureBank.Infrastructure/Notices/NoticeDeliveryRun.cs"] = 2,
        ["backend/src/AzureBank.Infrastructure/Notices/NoticeRelayOptionsValidation.cs"] = 1,
        ["backend/src/AzureBank.Shared/Constants/AuditDetails.cs"] = 1,
        ["backend/src/AzureBank.Shared/Constants/SecurityEvents.cs"] = 7,
        ["backend/src/AzureBank.Shared/DTOs/Auth/LoginResponse.cs"] = 1,
        ["backend/src/AzureBank.Shared/DTOs/Auth/SessionStampDtos.cs"] = 1,
        ["backend/src/AzureBank.Shared/Entities/AuditAnchor.cs"] = 8,
        ["backend/src/AzureBank.Shared/Entities/AuditEvent.cs"] = 2,
        ["backend/src/AzureBank.Shared/Enums/AuditAnchorKind.cs"] = 1,
        ["backend/src/AzureBank.Shared/Observability/ConsoleLogFormat.cs"] = 2,
        ["backend/src/AzureBank.Shared/Options/AuditOptions.cs"] = 6,
        ["backend/src/AzureBank.Shared/Options/DatabaseOptions.cs"] = 1,
        ["backend/src/AzureBank.Shared/Options/RetiredChainKey.cs"] = 3,
        ["backend/src/AzureBank.Shared/Utilities/SecretPrefix.cs"] = 1,
        ["backend/src/AzureBank.Shared/Validation/NoNullItemsAttribute.cs"] = 1,
        ["frontend/src/api/problemBaseQuery.ts"] = 1,
        ["frontend/src/api/validationErrors.ts"] = 4,
        ["frontend/src/components/AccountNumberField.tsx"] = 2,
        ["frontend/src/components/PinInput.tsx"] = 1,
        ["frontend/src/components/dialogs/ChangePinDialog.tsx"] = 2,
        ["frontend/src/components/dialogs/DeleteAccountDialog.tsx"] = 1,
        ["frontend/src/components/dialogs/WithdrawDialog.tsx"] = 2,
        ["frontend/src/components/dialogs/deleteRules.ts"] = 1,
        ["frontend/src/components/feedback/WaitHint.tsx"] = 1,
        ["frontend/src/components/shared/TransactionRow.tsx"] = 2,
        ["frontend/src/components/shared/transactionRowStyles.ts"] = 8,
        ["frontend/src/features/api/apiSlice.ts"] = 1,
        ["frontend/src/features/auth/sessionMiddleware.ts"] = 3,
        ["frontend/src/forms/moneySchemas.ts"] = 2,
        ["frontend/src/hooks/useIdempotentMutation.ts"] = 1,
        ["frontend/src/hooks/useMoneyWizard.ts"] = 2,
        ["frontend/src/hooks/useReturnFocus.ts"] = 1,
        ["frontend/src/hooks/useWaitPhase.ts"] = 1,
        ["frontend/src/pages/DashboardPage.tsx"] = 1,
        ["frontend/src/pages/LoginPage.tsx"] = 2,
        ["frontend/src/pages/SettingsPage.tsx"] = 1,
        ["frontend/src/pages/TransferPage.tsx"] = 2,
        ["frontend/src/theme/disabledButtonEdge.ts"] = 1,
        ["frontend/src/utils/format.ts"] = 1,
    };

    private static readonly string[] FrontendFoldersNotRead =
        ["test/", "mocks/", "contract/", "integration/", "api/generated/"];

    /// <summary>
    /// Each match is a comment or a literal, in the order they stand in the file, so that a
    /// <c>//</c> inside a string is never read as a comment and a quote inside a comment never
    /// opens a string.
    /// </summary>
    private static readonly Regex CommentOrLiteral = new(
        @"//[^\r\n]*"
        + @"|/\*.*?\*/"
        + @"|(?<fence>""{3,}).*?\k<fence>"
        + @"|@""(?:[^""]|"""")*"""
        + @"|""(?:\\.|[^""\\\r\n])*"""
        + @"|'(?:\\.|[^'\\\r\n])*'"
        + @"|`(?:\\.|[^`\\])*`",
        RegexOptions.Singleline);

    private static readonly Regex MarkedComment =
        new(@"\b20\d\d-\d\d-\d\d\b|review round|\u26A0", RegexOptions.IgnoreCase);

    private static int MarkedCommentLines(string source) =>
        CommentOrLiteral.Matches(source)
            .Select(match => match.Value)
            .Where(text => text.StartsWith("//", StringComparison.Ordinal)
                || text.StartsWith("/*", StringComparison.Ordinal))
            .SelectMany(text => text.Split('\n'))
            .Count(MarkedComment.IsMatch);

    private static IEnumerable<string> ProductionSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "backend", "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                yield return file;
            }
        }

        var frontend = Path.Combine(root, "frontend", "src");
        foreach (var file in Directory.EnumerateFiles(frontend, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(frontend, file).Replace('\\', '/');
            var isScript = relative.EndsWith(".ts", StringComparison.Ordinal)
                || relative.EndsWith(".tsx", StringComparison.Ordinal);
            var isNotProduction = relative.EndsWith(".d.ts", StringComparison.Ordinal)
                || relative.Contains(".test.", StringComparison.Ordinal)
                || relative.Contains(".spec.", StringComparison.Ordinal)
                || FrontendFoldersNotRead.Any(folder => relative.StartsWith(folder, StringComparison.Ordinal));

            if (isScript && !isNotProduction)
            {
                yield return file;
            }
        }
    }

    [Fact]
    public void ProductionCommentsCarryNoMoreDatesReviewRoundsOrWarningSignsThanBefore()
    {
        var root = RepositoryFiles.Root().FullName;
        var perFile = new Dictionary<string, int>();
        var scanned = 0;

        foreach (var file in ProductionSourceFiles(root))
        {
            scanned++;
            var marked = MarkedCommentLines(File.ReadAllText(file));
            if (marked > 0)
            {
                perFile[Path.GetRelativePath(root, file).Replace('\\', '/')] = marked;
            }
        }

        scanned.Should().BeGreaterThan(300,
            "a scan that reads nothing counts zero, and zero never rises");

        var grew = perFile
            .Select(pair => (File: pair.Key, Now: pair.Value,
                Allowed: MarkedCommentLinesAllowed.GetValueOrDefault(pair.Key)))
            .Where(row => row.Now > row.Allowed)
            .OrderBy(row => row.File, StringComparer.Ordinal)
            .Select(row => $"{row.File} {row.Now} (was {row.Allowed})")
            .ToList();

        grew.Should().BeEmpty(
            "AGENTS.md says a comment carries no date, no review round and no capitals for emphasis, "
            + "and the count of such lines in a file only falls. Remove the line, or write the reason "
            + "it holds without the date or the round. A change that removes some lowers the entry "
            + "of each file it touched in MarkedCommentLinesAllowed");
    }

    [Theory]
    [InlineData("// 2026-09-07", 1)]
    [InlineData("/// <summary>Review round 2</summary>", 1)]
    [InlineData("// \u26A0 careful", 1)]
    [InlineData("// 2026-09-07 and a review round and \u26A0", 1)]
    [InlineData("// 2026-09-07\n// 2026-09-08", 2)]
    [InlineData("/* first\n 2026-09-07\n last */", 1)]
    [InlineData("var u = \"https://example.test\"; // 2026-09-07", 1)]
    [InlineData("var s = \"2026-09-07\";", 0)]
    [InlineData("var s = @\"say \"\"//\"\" 2026-09-07\";", 0)]
    [InlineData("var s = \"\"\"\n// 2026-09-07\n\"\"\";", 0)]
    [InlineData("const s = `// 2026-09-07`;", 0)]
    [InlineData("Date.parse('2026-09-07'); // parsed", 0)]
    [InlineData("// a comment with nothing marked", 0)]
    [InlineData("var year = 2026; var month = 09;", 0)]
    public void TheCountReadsCommentsAndNotStrings(string source, int expected)
    {
        // A count that read a string as a comment would charge every literal that holds a date, and
        // one that skipped a comment after a string would let a dated line through.
        MarkedCommentLines(source).Should().Be(expected);
    }

    [Theory]
    [InlineData((char)0x08, true)]   // BACKSPACE - the one that actually happened
    [InlineData((char)0x00, true)]   // NUL
    [InlineData((char)0x1B, true)]   // ESC, i.e. a pasted terminal colour sequence
    [InlineData((char)0x0C, true)]   // FORM FEED
    [InlineData((char)0x7F, true)]   // DELETE - outside C0, missed by the first version
    [InlineData((char)0x85, true)]   // NEXT LINE, a C1 control that ReadLine does NOT split on
    [InlineData((char)0x9F, true)]   // the far end of the C1 block
    [InlineData((char)0x09, false)]  // TAB
    [InlineData((char)0x0D, false)]  // CR
    [InlineData((char)0x0A, false)]  // LF
    [InlineData((char)0x41, false)]  // 'A'
    [InlineData((char)0xE9, false)]  // an accented letter: non-ASCII is fine, this is about CONTROL characters
    public void TheDetectorAgreesOnWhatCountsAsInvisible(char c, bool forbidden)
    {
        // Without this the rule's boundary is whatever the tree happens to contain, which is exactly
        // how the corrupted pattern on #118 survived: nobody had watched the detector refuse.
        IsForbiddenControlCharacter(c).Should().Be(forbidden);
    }
}
