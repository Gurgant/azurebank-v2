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
    /// SCREAMING_SNAKE string literals, which is the shape every error code has. Six characters
    /// minimum keeps ordinary constants like "DELETE" and header names out of the net.
    /// </summary>
    private static readonly Regex ScreamingSnakeLiteral = new(
        "\"[A-Z][A-Z0-9_]{5,}\"", RegexOptions.Compiled);

    [Fact]
    public void NoServiceThrowsAnErrorCodeAsABareStringLiteral()
    {
        var services = Path.Combine(RepoBackendRoot().FullName, "src", "AzureBank.Api", "Services");
        Directory.Exists(services).Should().BeTrue(because: $"expected to scan {services}");

        var declaredValues = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ScreamingSnakeLiteral.Matches(text))
            {
                var value = match.Value.Trim('"');

                // Only codes are in scope. A SCREAMING_SNAKE literal that is NOT a known error code
                // is something else entirely (an env var, a header) and is none of this rule's
                // business — flagging those would make the guard noisy and therefore ignored.
                if (declaredValues.Contains(value))
                {
                    offenders.Add($"{Path.GetFileName(file)}: \"{value}\" — use ErrorCodes instead");
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "an error code duplicated as a literal drifts from its constant without failing anything");
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
    }
}
