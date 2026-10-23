using System.Text.RegularExpressions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// No finding of the audit chain and no line of the verifier groups the digits of a number with the
/// culture of the host.
/// </summary>
/// <remarks>
/// A source scan, because a number below one thousand is written the same under every culture: the
/// lines that carry an anchor's own sequence, or the sequence of one audit row in an evidence pack,
/// cannot be told apart by running them on the few rows a test writes.
/// <c>VerifierReportsUnderAHostCultureTests</c> and
/// <c>AuditChainFindingsUnderAHostCultureTests</c> run the lines a test can reach with a number
/// above one thousand. The scan fails when it cannot find the sources.
/// </remarks>
public class AuditReportNumberFormattingTests
{
    private static readonly string[] ScannedFolders =
    [
        Path.Combine("src", "AzureBank.Infrastructure"),
        Path.Combine("tools", "AzureBank.AuditVerifier"),
    ];

    // The specifier with its digits: "N" alone is also how an identifier is written without
    // hyphens, and that form has no culture.
    private static readonly Regex GroupedInAHole = new(@"\{[^{}]*:[nN]\d+\}", RegexOptions.Compiled);

    // The same specifier as the only argument of the call: with no culture named, the call takes
    // the thread's.
    private static readonly Regex GroupedInACall = new(@"ToString\(\s*""[nN]\d+""\s*\)", RegexOptions.Compiled);

    private static bool GroupsWithTheHostCulture(string line) =>
        GroupedInAHole.IsMatch(line) || GroupedInACall.IsMatch(line);

    [Fact]
    public void NoFindingOrReportGroupsDigitsWithTheHostCulture()
    {
        var root = RepositoryFiles.Backend();
        var offenders = new List<string>();
        var read = 0;

        foreach (var file in SourceFiles(root))
        {
            read++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (GroupsWithTheHostCulture(lines[i]))
                {
                    offenders.Add($"{Path.GetRelativePath(root.FullName, file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        read.Should().BeGreaterThan(20, "a scan that reads nothing reports clean for ever");
        offenders.Should().BeEmpty(
            "a number in a finding or a report is written with ReportText.Grouped, which names the "
            + "invariant culture: the same sequence must not read 12,345 on one host and 12.345 on "
            + "another");
    }

    [Theory]
    [InlineData("$\"Row {row.Id} is sequence {row.Sequence:N0} and names key id \"")]
    [InlineData("$\"  Sequences read: {lowest:n0} to {highest:N2}\"")]
    [InlineData("$\"{count,12:N0} rows\"")]
    [InlineData("state.FirstBrokenSequence?.ToString(\"N0\") ?? \"(unknown)\"")]
    [InlineData("records.Count.ToString(\"n0\")")]
    public void TheDetectorCatchesEverySpelling(string line)
    {
        GroupsWithTheHostCulture(line).Should().BeTrue($"'{line}' groups digits with the host's culture");
    }

    [Theory]
    [InlineData("$\"notice {reference} for user {notice.UserId:N} has no email\"")]
    [InlineData("row.Id.ToString(\"N\"),")]
    [InlineData("row.ActorUserId?.ToString(\"N\") ?? string.Empty,")]
    [InlineData("Guid.NewGuid().ToString(\"N\")")]
    [InlineData("value.ToString(\"N0\", CultureInfo.InvariantCulture)")]
    [InlineData("$\"  {movement.Type} on account {movement.AccountId:D},\"")]
    public void TheDetectorLeavesIdentifiersAndNamedCulturesAlone(string line)
    {
        GroupsWithTheHostCulture(line).Should().BeFalse($"'{line}' is the same text under every culture");
    }

    private static IEnumerable<string> SourceFiles(DirectoryInfo root)
    {
        foreach (var relative in ScannedFolders)
        {
            var folder = Path.Combine(root.FullName, relative);
            Directory.Exists(folder).Should().BeTrue(because: $"expected to scan {folder}");

            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                // bin and obj hold generated copies of the sources, some from an earlier build.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }
}
