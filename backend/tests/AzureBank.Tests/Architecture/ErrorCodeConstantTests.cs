using System.Reflection;
using System.Text.RegularExpressions;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps <see cref="ErrorCodes"/> the single source of truth for the codes the API puts on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Six services used to throw their error code as a bare string literal while an identical constant
/// sat declared and unreferenced — <c>SELF_TRANSFER_NOT_ALLOWED</c> and <c>SAME_ACCOUNT_TRANSFER</c>
/// were declared AND duplicated, and four more codes existed only as literals. Nothing failed, which
/// is the problem: renaming a constant, or fixing a typo in one of the two copies, would have left
/// the other behind and changed the contract silently.
/// </para>
/// <para>
/// This is a source scan rather than a NetArchTest rule because the defect lives in string
/// literals, which are indistinguishable from constant references once compiled. It deliberately
/// FAILS rather than skips when it cannot find the sources: a guard that quietly passes when it
/// cannot do its job is worse than no guard.
/// </para>
/// </remarks>
public class ErrorCodeConstantTests
{
    /// <summary>Walks up from the test assembly until it finds the solution file.</summary>
    private static DirectoryInfo RepoBackendRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AzureBank.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the scan needs the sources; a guard that cannot run must fail loudly");
        return dir!;
    }

    /// <summary>
    /// All-caps string literals of six characters or more — the shape every error code has.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT require an underscore, and a review suggestion to add one was measured
    /// and declined: <c>ErrorCodes.Conflict</c> is <c>"CONFLICT"</c>, a real code that
    /// <see cref="AzureBank.Shared.Exceptions.ConflictException"/> applies by default and that
    /// therefore reaches the wire. An underscore-requiring pattern misses it, which would reopen
    /// exactly the hole this rule was widened to close.
    ///
    /// The cost of being broad is false positives on all-caps non-codes ("DELETE", "HOSTNAME").
    /// Measured in the two scanned folders: there are none, and those names live in
    /// Program/Observability/Middleware, which are out of scope on purpose.
    /// </remarks>
    private static readonly Regex ScreamingSnakeLiteral = new(
        "\"[A-Z][A-Z0-9_]{5,}\"", RegexOptions.Compiled);

    /// <summary>
    /// The folders where every error code the API emits is chosen. Both are now literal-free, which
    /// is what lets the rule below be "none at all" rather than "none that happen to be declared".
    /// </summary>
    private static readonly string[] ScannedFolders =
    [
        Path.Combine("src", "AzureBank.Api", "Services"),
        Path.Combine("src", "AzureBank.Shared", "Exceptions"),
    ];

    [Fact]
    public void NoErrorCodeIsWrittenAsABareStringLiteral()
    {
        // The first version of this rule only flagged literals whose value was ALREADY declared in
        // ErrorCodes, and only scanned the services. That left two holes, and a review found both:
        // BusinessRuleException and ConflictException chose their default codes inline, invisibly;
        // and a brand-new code invented at a throw site — which is exactly how RECIPIENT_NO_ACCOUNT
        // came to exist without a constant — matched nothing and passed.
        //
        // Since both folders now contain zero such literals, the rule can be absolute: ANY
        // SCREAMING_SNAKE literal here is either an error code that belongs in ErrorCodes, or
        // something that does not belong in these folders at all. Environment variables and header
        // names live in Program/Observability/Middleware, which are deliberately not scanned.
        var root = RepoBackendRoot();
        var offenders = new List<string>();

        foreach (var relative in ScannedFolders)
        {
            var folder = Path.Combine(root.FullName, relative);
            Directory.Exists(folder).Should().BeTrue(because: $"expected to scan {folder}");

            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                offenders.AddRange(
                    ScreamingSnakeLiteral.Matches(File.ReadAllText(file))
                        .Select(m => $"{Path.GetFileName(file)}: {m.Value} — declare it in ErrorCodes and reference it"));
            }
        }

        offenders.Should().BeEmpty(
            because: "a code written as a literal drifts from its constant, or never gets one, without failing anything");
    }

    [Fact]
    public void EveryDeclaredCodeIsUnique()
    {
        // Two constants sharing a value would make the wire ambiguous and the rule above blind:
        // a literal matching either one would resolve to whichever the set happened to hold.
        var values = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        values.Should().OnlyHaveUniqueItems();
        values.Should().NotBeEmpty(because: "reflection finding nothing would make this vacuous");

        // Every declared value must match the SAME pattern the scanner looks for, and this is not
        // cosmetic: a constant whose value the regex cannot match is a constant whose duplicated
        // literal the scanner cannot see. Declaring `Foo = "lower_case"` or `Foo = ""` would leave
        // the rule above passing while that code drifted freely — the guard would be blind to
        // precisely the constant it was meant to protect.
        //
        // A review asked for a non-empty check here. This is that, generalised: an empty or
        // whitespace value fails the pattern too, and so does every other shape the scanner cannot
        // detect.
        values.Should().OnlyContain(
            value => ScreamingSnakeLiteral.IsMatch($"\"{value}\""),
            because: "a code the scanner's own pattern cannot match is a code the scanner cannot guard");
    }
}
