using System.Text.RegularExpressions;
using AzureBank.Tests.Integration;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// The Bruno collection under <c>tests/api-collection</c> must be RUNNABLE: every variable a
/// request interpolates is written before it is read, and no request asks the sandbox for a Node
/// module it does not have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a test and not a note.</b> Measured on 2026-09-21 against the running API, the collection
/// answered <c>1 Passed, 23 Failed</c> out of 24 requests, on four independent causes: the register
/// request never published the address it had just created, so the login that followed authenticated
/// as a user nobody had made and — worse — its own post-response overwrote the valid token with
/// nothing; five requests asked for <c>require('crypto')</c>, which Bruno's default QuickJS sandbox
/// does not have, and were never sent at all; a failed list-accounts still published an empty
/// <c>accountId</c>, so three more requests built <c>/api/accounts/</c>; and no folder carried a
/// <c>seq</c>, so Bruno ordered the folders alphabetically and ran <c>accounts</c> before
/// <c>auth</c>.
/// </para>
/// <para>
/// <b>And nothing said so.</b> The only job that runs the collection is manual-only, its step ended
/// in <c>|| true</c>, and its command line omitted <c>-r</c> — measured with that exact line:
/// <c>Requests 0, Tests 0/0, Status PASS</c>. A green run that sent nothing. These two tests run in
/// the ordinary backend gate, need no server, and make the first and the second causes mechanical.
/// </para>
/// </remarks>
public class BrunoCollectionRunsTests
{
    private static DirectoryInfo Collection() => new(Path.Combine(
        RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName, "tests", "api-collection"));

    // {{baseUrl}}, {{$randomUUID}} — the $-prefixed ones are Bruno's own, supplied by the runtime.
    private static readonly Regex Interpolation = new(@"\{\{(\$?[A-Za-z_][A-Za-z0-9_]*)\}\}", RegexOptions.Compiled);
    private static readonly Regex SetVar = new(@"bru\.setVar\(\s*'([A-Za-z_][A-Za-z0-9_]*)'", RegexOptions.Compiled);
    private static readonly Regex VarsBlockEntry = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:", RegexOptions.Compiled);
    private static readonly Regex Seq = new(@"^\s*seq:\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Folder seq, then request seq — the order Bruno actually sends them in.</summary>
    private static List<(string Path, string Text)> InRunOrder()
    {
        var root = Collection();
        var requests = new List<(int Folder, int Request, string Path, string Text)>();
        foreach (var file in root.GetDirectories("endpoints").Single()
                     .GetFiles("*.bru", SearchOption.AllDirectories))
        {
            if (file.Name == "folder.bru")
            {
                continue;
            }

            var folderFile = Path.Combine(file.DirectoryName!, "folder.bru");
            File.Exists(folderFile).Should().BeTrue(
                $"{file.Directory!.Name} has no folder.bru, so Bruno would order it ALPHABETICALLY — "
                + "which ran accounts before auth and left every request in it unauthenticated");

            var text = File.ReadAllText(file.FullName);
            requests.Add((
                int.Parse(Seq.Match(File.ReadAllText(folderFile)).Groups[1].Value),
                int.Parse(Seq.Match(text).Groups[1].Value),
                Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'),
                text));
        }

        return requests
            .OrderBy(r => r.Folder).ThenBy(r => r.Request)
            .Select(r => (r.Path, r.Text))
            .ToList();
    }

    [Fact]
    public void EveryVariableTheCollectionReads_IsWrittenBeforeItIsRead()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);

        // Whatever the environments declare is available from the first request onwards.
        foreach (var environment in Collection().GetDirectories("environments").Single().GetFiles("*.bru"))
        {
            var inside = false;
            foreach (var line in File.ReadAllLines(environment.FullName))
            {
                if (line.TrimStart().StartsWith("vars", StringComparison.Ordinal))
                {
                    inside = true;
                    continue;
                }

                if (inside && line.Trim() == "}")
                {
                    inside = false;
                    continue;
                }

                var entry = VarsBlockEntry.Match(line);
                if (inside && entry.Success)
                {
                    known.Add(entry.Groups[1].Value);
                }
            }
        }

        var unwritten = new List<string>();
        foreach (var (path, text) in InRunOrder())
        {
            // A docs block and a // comment are prose. They name variables in order to EXPLAIN
            // them, and reading those as dependencies is how this test first accused two
            // requests that were correct.
            var code = WithoutProse(text);

            // A pre-request script runs BEFORE this request is sent, so what it sets is
            // available to this request's own url, headers and body.
            foreach (Match early in SetVar.Matches(BlockOf(code, "script:pre-request")))
            {
                known.Add(early.Groups[1].Value);
            }

            foreach (Match read in Interpolation.Matches(code))
            {
                var name = read.Groups[1].Value;
                if (name.StartsWith('$') || known.Contains(name))
                {
                    continue;
                }

                unwritten.Add($"{path} reads {{{{{name}}}}}, which nothing before it writes");
            }

            // A post-response script runs after the answer, so its variables belong to the
            // requests that FOLLOW, not to this one.
            foreach (Match late in SetVar.Matches(BlockOf(code, "script:post-response")))
            {
                known.Add(late.Groups[1].Value);
            }
        }

        // Every offender, not just the first: BeEmpty() on a collection names only one, and the
        // point of this test is to hand back the whole list.
        unwritten.Should().BeEquivalentTo(
            Array.Empty<string>(),
            "a variable read before anything writes it interpolates to nothing, and the request that "
            + $"follows fails for a reason that hides the real one. Offenders:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unwritten));
    }

    [Fact]
    public void NoRequestAsksTheSandboxForANodeModule()
    {
        var offenders = Collection()
            .GetFiles("*.bru", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file.FullName).Contains("require(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Collection().FullName, file.FullName).Replace('\\', '/'))
            .ToList();

        offenders.Should().BeEquivalentTo(
            Array.Empty<string>(),
            "Bruno runs scripts in a QuickJS sandbox with no Node module resolution, and the CLI reads "
            + "its sandbox ONLY from --sandbox (default \"safe\"), never from the collection — so a "
            + "require() cannot be fixed by configuring the collection. Measured 2026-09-21: "
            + "\"Error: Cannot find module crypto\", and the request was never sent. Use Bruno's own "
            + "{{$randomUUID}} in the header, or bru.interpolate('{{$randomUUID}}') when the value has "
            + $"to be stored and reused. Offenders:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The file without its docs block and its line comments — prose names variables
    /// in order to explain them, which is not a dependency.</summary>
    private static string WithoutProse(string text)
    {
        var withoutDocs = Regex.Replace(text, @"(?ms)^docs \{.*?^\}", string.Empty);
        return Regex.Replace(withoutDocs, @"(?m)^\s*//.*$", string.Empty);
    }

    /// <summary>The body of one named block, or empty when the file has none.</summary>
    private static string BlockOf(string text, string name)
    {
        var match = Regex.Match(text, $@"(?ms)^{Regex.Escape(name)} \{{(.*?)^\}}");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
