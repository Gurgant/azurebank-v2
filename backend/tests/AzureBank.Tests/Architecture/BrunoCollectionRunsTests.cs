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
    // BOTH quote styles, because `bru.setVar("authToken", v)` is the same publisher as the
    // single-quoted one. Matching only `'` meant the order check never recorded it AND
    // UnguardedPublishes never inspected it, so an unconditional double-quoted publish passed —
    // measured, and review found it.
    private static readonly Regex SetVar = new(
        @"bru\.setVar\(\s*(?<q>['""])(?<name>[A-Za-z_][A-Za-z0-9_]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex VarsBlockEntry = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:", RegexOptions.Compiled);
    private static readonly Regex Seq = new(@"^\s*seq:\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    // `require ('crypto')` is the same call as `require('crypto')`, and the sandbox refuses both.
    private static readonly Regex ExecutableRequire = new(@"\brequire\s*\(", RegexOptions.Compiled);
    // The brace is PART of the match, so the span belongs to THIS `if` and not to whatever
    // block happens to come next. Searching for the next `{` let a braceless success guard
    // adopt an unrelated block's brace, and a publisher that runs on ANY status looked
    // guarded — measured, and review found it.
    private static readonly Regex SuccessGuard = new(
        @"if\s*\(\s*res\.getStatus\(\)\s*===\s*(\d{3})\s*\)\s*\{", RegexOptions.Compiled);

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

            var pre = BlockOf(code, "script:pre-request");
            var post = BlockOf(code, "script:post-response");

            void Check(string fragment)
            {
                foreach (Match read in Interpolation.Matches(fragment))
                {
                    var name = read.Groups[1].Value;
                    if (name.StartsWith('$') || known.Contains(name))
                    {
                        continue;
                    }

                    unwritten.Add($"{path} reads {{{{{name}}}}}, which nothing before it writes");
                }
            }

            // A pre-request script runs STATEMENT BY STATEMENT, so a line may only read what an
            // EARLIER line set. Adding all of its names up front was coarse in the permissive
            // direction -- a script reading a variable one line before setting it passed, which
            // review caught and a probe confirmed. So it is walked in order.
            foreach (var statement in pre.Split('\n'))
            {
                Check(statement);
                foreach (Match early in SetVar.Matches(statement))
                {
                    known.Add(early.Groups["name"].Value);
                }
            }

            // Then the request itself, which goes out once that whole script has run.
            Check(Without(Without(code, pre), post));

            // A post-response script runs after the answer, so its variables belong to the
            // requests that FOLLOW, not to this one.
            foreach (Match late in SetVar.Matches(post))
            {
                known.Add(late.Groups["name"].Value);
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
            // After the prose, and allowing the whitespace JavaScript allows: `require ('crypto')`
            // is the same refused call, and Contains("require(") missed it while also reading docs
            // blocks that only MENTION the trap. Review caught both halves.
            .Where(file => ExecutableRequire.IsMatch(WithoutProse(File.ReadAllText(file.FullName))))
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

    [Fact]
    public void TheRequestsThatPublishAVariable_DoSoOnlyWhenTheySucceeded()
    {
        // The other half of the first cause, and the damaging half. login answered 401 and its
        // post-response STILL ran, overwriting the valid token register had just published with
        // nothing -- so fifteen later requests failed as "unauthorised" when the real fault was
        // one request earlier. A publisher that does not look at its own status turns one broken
        // request into a broken suite, and hides which one broke.
        var unguarded = new List<string>();
        foreach (var (path, text) in InRunOrder())
        {
            // Both strings appearing SOMEWHERE proves nothing: an unconditional bru.setVar beside
            // an unrelated res.getStatus() read satisfied it, and a publish inside a FAILURE branch
            // would have too. Each publish must sit inside a brace-matched success guard.
            unguarded.AddRange(
                UnguardedPublishes(BlockOf(WithoutProse(text), "script:post-response"), path));

            // vars:post-response is the declarative form and CANNOT be conditional, so a request
            // using it publishes on failure by construction.
            if (WithoutProse(text).Contains("vars:post-response", StringComparison.Ordinal))
            {
                unguarded.Add($"{path} uses vars:post-response, which publishes even on a failure");
            }
        }

        unguarded.Should().BeEquivalentTo(
            Array.Empty<string>(),
            "a failed request must publish nothing, or it replaces a good value with an empty one. "
            + $"Offenders:{Environment.NewLine}" + string.Join(Environment.NewLine, unguarded));
    }

    [Fact]
    public void TheLogin_UsesTheIdentityThatRegisterCreated()
    {
        // The first cause itself, which the chain test above CANNOT see: testEmail is declared in
        // the environments, so "every variable has a writer" was satisfied while the value was a
        // stale default. Measured 2026-09-21: register created test.<timestamp>@example.com and
        // never wrote it back, so login posted test@example.com -- a user nobody had made -- and
        // answered 401 INVALID_CREDENTIALS. A mutant that removed the publish left the general
        // test green, which is why this one is specific.
        // The EXECUTABLE blocks, not whole files: a mention in a comment kept this green after
        // the chain was broken. Review said so and a probe confirmed it.
        var root = Collection().FullName;
        var register = BlockOf(
            WithoutProse(File.ReadAllText(Path.Combine(root, "endpoints", "auth", "register.bru"))),
            "script:post-response");
        var login = BlockOf(
            WithoutProse(File.ReadAllText(Path.Combine(root, "endpoints", "auth", "login.bru"))),
            "body:json");

        // The WHOLE call, not its parts. Asserting the NAME and the EXPRESSION separately let a
        // request publish testEmail from something else entirely while an unrelated line kept the
        // second assertion satisfied — measured, and it passed. The same for login: {{testEmail}}
        // appearing SOMEWHERE in the body is not the same as it being the address signed in with.
        Regex.IsMatch(
                register,
                @"bru\.setVar\(\s*(['""])testEmail\1\s*,\s*res\.body\.data\.user\.email\s*\)")
            .Should().BeTrue(
                "the address register CREATED is the only one login can sign in with, and it is "
                + "read back from the answer rather than recomputed. Its post-response block "
                + $"reads:{Environment.NewLine}{register}");

        Regex.IsMatch(login, @"""email""\s*:\s*""\{\{testEmail\}\}""")
            .Should().BeTrue(
                "login must sign in as whoever register made, and it is the EMAIL field that has "
                + $"to carry it. Its body reads:{Environment.NewLine}{login}");
    }
    /// <summary>
    /// The file without its docs block and without ANY comment — leading or trailing — while
    /// leaving comment-like text inside quoted strings and URL schemes alone.
    /// </summary>
    /// <remarks>
    /// Stripping only lines that BEGIN with <c>//</c> left a trailing comment as executable input
    /// to every later check. Measured 2026-09-21, with all six guards green:
    /// <c>const note = 0; // bru.setVar('testEmail', res.body.data.user.email)</c> satisfied the
    /// registration-to-login guard while register published nothing at all.
    /// </remarks>
    private static string WithoutProse(string text)
    {
        var withoutDocs = RemoveBlock(text, "docs");
        var kept = new System.Text.StringBuilder(withoutDocs.Length);
        var quote = '\0';

        for (var i = 0; i < withoutDocs.Length; i++)
        {
            var c = withoutDocs[i];

            if (quote != '\0')
            {
                kept.Append(c);
                if (c == '\\' && i + 1 < withoutDocs.Length)
                {
                    kept.Append(withoutDocs[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                kept.Append(c);
                continue;
            }

            // `https://localhost:7215` is an address, not a comment. But ANY colon was too
            // generous: a ternary written `1 ? 0 :// …` kept its comment as executable text, and
            // a fake publish hidden there satisfied the identity guard while the real one was
            // gone — measured, with all six guards green. So a real SCHEME is required.
            if (c == '/' && i + 1 < withoutDocs.Length && withoutDocs[i + 1] == '/'
                && !PrecededByUriScheme(withoutDocs, i))
            {
                while (i < withoutDocs.Length && withoutDocs[i] != '\n')
                {
                    i++;
                }

                if (i < withoutDocs.Length)
                {
                    kept.Append('\n');
                }

                continue;
            }

            kept.Append(c);
        }

        return kept.ToString();
    }

    /// <summary>
    /// True when the <c>//</c> at <paramref name="slash"/> closes a URI scheme — <c>http:</c>,
    /// <c>https:</c>, anything matching RFC 3986's <c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c>
    /// — rather than opening a comment after some other colon.
    /// </summary>
    private static bool PrecededByUriScheme(string text, int slash)
    {
        if (slash == 0 || text[slash - 1] != ':')
        {
            return false;
        }

        var i = slash - 1;
        while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '+' or '-' or '.'))
        {
            i--;
        }

        // A scheme is at least one character and must START with a letter, so `1 ? 0 ://` and
        // `0://` are comments while `https://` is an address.
        return i < slash - 1 && char.IsLetter(text[i]);
    }

    /// <summary>The text with one named block, braces and all, taken out of it.</summary>
    private static string RemoveBlock(string text, string name)
    {
        var header = Regex.Match(text, $@"(?m)^{Regex.Escape(name)}\s*\{{");
        if (!header.Success)
        {
            return text;
        }

        var close = MatchingBrace(text, header.Index + header.Length - 1);
        return close < 0 ? text : text[..header.Index] + text[(close + 1)..];
    }

    /// <summary>
    /// Every <c>bru.setVar</c> in a post-response block that is NOT inside a brace-matched
    /// <c>if (res.getStatus() === 2xx)</c>, plus any guard written on a non-success status.
    /// </summary>
    private static IEnumerable<string> UnguardedPublishes(string published, string path)
    {
        var offenders = new List<string>();
        var guarded = new List<(int Open, int Close)>();

        foreach (Match guard in SuccessGuard.Matches(published))
        {
            var status = int.Parse(guard.Groups[1].Value);
            if (status is < 200 or > 299)
            {
                offenders.Add($"{path} publishes under a guard on {status}, which is not a success");
                continue;
            }

            var open = guard.Index + guard.Length - 1;   // the brace the match consumed
            var close = MatchingBrace(published, open);
            if (close > open)
            {
                guarded.Add((open, close));
            }
        }

        foreach (Match publish in SetVar.Matches(published))
        {
            if (!guarded.Any(span => publish.Index > span.Open && publish.Index < span.Close))
            {
                var name = publish.Groups["name"].Value;
                offenders.Add($"{path} publishes {name} outside a success guard");
            }
        }

        return offenders;
    }

    /// <summary>The index of the brace closing the one at <paramref name="open"/>, or -1.</summary>
    private static int MatchingBrace(string text, int open)
    {
        if (open < 0 || open >= text.Length || text[open] != '{')
        {
            return -1;
        }

        // Braces INSIDE a string are text, not structure: `{{testEmail}}` in a JSON body would
        // otherwise unbalance every block that carries one.
        var depth = 0;
        var quote = '\0';
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                continue;
            }

            depth += c == '{' ? 1 : c == '}' ? -1 : 0;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Removes one fragment, tolerating the empty one a missing block returns.</summary>
    private static string Without(string text, string fragment) =>
        fragment.Length == 0 ? text : text.Replace(fragment, string.Empty, StringComparison.Ordinal);

    /// <summary>The body of one named block, or empty when the file has none.</summary>
    /// <remarks>
    /// Read to the BRACE THAT MATCHES the block's own, never to the first <c>}</c> at the start of
    /// a line. A nested branch closing in column zero is legal in these scripts, and it used to cut
    /// the block short: measured 2026-09-21, an unguarded <c>bru.setVar</c> sitting after such a
    /// branch was invisible to <see cref="UnguardedPublishes"/> and all six guards stayed green.
    /// </remarks>
    private static string BlockOf(string text, string name)
    {
        var header = Regex.Match(text, $@"(?m)^{Regex.Escape(name)}\s*\{{");
        if (!header.Success)
        {
            return string.Empty;
        }

        var open = header.Index + header.Length - 1;
        var close = MatchingBrace(text, open);
        return close > open ? text[(open + 1)..close] : string.Empty;
    }
}
