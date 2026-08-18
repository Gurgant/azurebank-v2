using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps invisible characters out of the source.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a real, measured incident on PR #118, not as hygiene theatre. A line in
/// <c>MoneyFormattingTests</c> reached disk carrying a literal <c>U+0008</c> BACKSPACE where a
/// backslash-b was meant — written by a tool that expanded the escape on its way to the file. C#
/// verbatim strings do not process escapes, so the byte went into a regex as a character to match.
/// The result was a guard that could never match anything, and it reported clean for a whole session
/// while the defect it was written to catch sat three lines inside a mapper.
/// </para>
/// <para>
/// Every normal instrument was blind to it. The compiler accepted it — a backspace in a regex is
/// legal and simply means "match a backspace". <c>grep</c> rendered it as nothing. Reading the file
/// showed the intended text. Only <c>od -c</c> revealed it, because there the byte prints as ONE
/// token where real backslashes print as two.
/// </para>
/// <para>
/// So the rule is about the editing tool rather than about the product, which is unusual for this
/// suite and is the reason it is one test rather than a family. It is cheap, it is absolute, and it
/// would have caught that byte on the first run instead of the third day.
/// </para>
/// </remarks>
public class SourceHygieneTests
{
    /// <summary>Walks up from the test assembly until it finds the repository root.</summary>
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the scan needs the sources; a guard that cannot run must fail loudly");
        return dir!;
    }

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
    /// <c>char.IsControl</c> rather than a comparison against U+0020, which is what this said
    /// first and which silently stopped at U+001F: it accepted U+007F DELETE and the whole
    /// U+0080-U+009F C1 block, while the trap doc claimed the rule covered any control
    /// character. Raised by CodeRabbit on PR #119, and it is the same defect this PR exists to
    /// make impossible - a stated rule wider than the one enforced. Measured before widening:
    /// the repo contains no character in U+007F-U+009F today, so nothing legitimate is caught.
    /// </remarks>
    private static bool IsForbiddenControlCharacter(char c) =>
        char.IsControl(c) && c != (char)0x09 && c != (char)0x0D && c != (char)0x0A;

    [Fact]
    public void NoSourceFileCarriesAnInvisibleControlCharacter()
    {
        var root = RepoRoot();
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
