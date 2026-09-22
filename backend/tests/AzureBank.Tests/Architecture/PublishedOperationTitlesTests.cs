using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Every operation in the committed OpenAPI document has a title for a summary: one line, at most
/// fifty characters. The prose belongs in the description.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-14 seven of the twenty-seven summaries were whole paragraphs, the longest 372
/// characters over five lines, because the XML <c>&lt;summary&gt;</c> comments overrode the short
/// <c>[EndpointSummary]</c> titles the project believed it was publishing (ADR-0053). Scalar and the
/// generated frontend types showed the paragraphs. This guard was red on that document — measured
/// before the summaries moved: 11 of 27, seven spanning lines and four of 52 to 76 characters — and
/// green once each action's title became its summary and its prose its remarks.
/// </para>
/// <para>
/// Reads the COMMITTED file, like <see cref="PublishedRefusalCodesTests"/>: the claim is about what
/// the document says, and the generator agrees with a long summary as readily as with a short one.
/// </para>
/// </remarks>
public class PublishedOperationTitlesTests
{
    private const int TitleLimit = 50;

    private static JsonElement Document()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the guard needs the committed document; one that cannot run must fail loudly");

        var path = Path.Combine(dir!.FullName, "docs", "api", "openapiv1.json");
        File.Exists(path).Should().BeTrue(because: $"the published contract is expected at {path}");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    [Fact]
    public void EveryOperationSummary_IsOneLineOfAtMostFiftyCharacters()
    {
        var offenders = new List<string>();
        var operations = 0;

        foreach (var path in Document().GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                operations++;
                var summary = operation.Value.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                if (summary.Length == 0)
                {
                    offenders.Add($"{operation.Name.ToUpperInvariant()} {path.Name}: no summary");
                }
                else if (summary.Contains('\n') || summary.Contains('\r'))
                {
                    offenders.Add($"{operation.Name.ToUpperInvariant()} {path.Name}: the summary spans lines");
                }
                else if (summary.Length > TitleLimit)
                {
                    offenders.Add($"{operation.Name.ToUpperInvariant()} {path.Name}: {summary.Length} characters — \"{summary}\"");
                }
            }
        }

        // The floor guards the scan itself: a document with no operations would pass every check.
        operations.Should().BeGreaterThanOrEqualTo(28, "the scan must have seen the operations the API publishes (28 on 2026-09-21, after the withdrawal mint)");
        offenders.Should().BeEmpty(
            "a summary is the title Scalar and the generated client show; the prose belongs in the description. "
            + "Offenders ({0} of {1}): {2}", offenders.Count, operations, string.Join("; ", offenders));
    }
}
