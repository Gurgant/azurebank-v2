using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using static AzureBank.Tests.Architecture.LogPlaceholderClassTests.PlaceholderClass;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// ADR-0017's log-identifier rule, enforced on every log template in the four projects that log:
/// each placeholder has exactly one class, and two classes may never appear at all.
/// </summary>
/// <remarks>
/// <para>
/// THE RULE, as ADR-0017 records it since 2026-09-11. Surrogate keys are logged in clear — they are
/// the correlation key, not personal data. A secret appears only as its first eight characters,
/// through <c>SecretPrefix.Of</c>. An email appears only masked, through the PII redactor. A direct
/// identifier — a handle, a name, anything a person chose to be known by — never appears. Neither
/// does money. Everything else is operational: counts, codes, durations, event names.
/// </para>
/// <para>
/// MEASURED BEFORE IT WAS WRITTEN, not assumed. The first run of this scan over the four projects
/// found amounts and balances on the deposit and withdrawal lines (ADR-0017 D5 had banned them from
/// the start, and its Consequences said "no amounts"), handles on three lines, and an account's
/// chosen name on another. All six were fixed in the same change.
/// </para>
/// <para>
/// WHAT IT DOES NOT SEE: values that reach a log through an operational placeholder. The request log
/// prints the request path, and <c>GET /api/users/{azureTag}</c> puts a handle in it — measured,
/// <c>HTTP GET /api/users/janesmith responded 200</c>. That is ADR-0017's named gap, not a green
/// light: this guard reads templates, and a path is a template's value.
/// </para>
/// </remarks>
public class LogPlaceholderClassTests
{
    public enum PlaceholderClass
    {
        /// <summary>An opaque key: not a credential, not personal data, useless without the database.</summary>
        SurrogateKey,

        /// <summary>A secret, allowed only as its prefix through <c>SecretPrefix.Of</c>.</summary>
        SecretPrefix,

        /// <summary>Personal data allowed only masked, through the PII redactor (ADR-0017 D1-D3).</summary>
        MaskedPii,

        /// <summary>Counts, codes, durations, names of events and endpoints: nothing about a person.</summary>
        Operational,

        /// <summary>Never logged: a handle, a name, anything a person is known by.</summary>
        DirectIdentifier,

        /// <summary>Never logged: an amount or a balance (ADR-0017 D5).</summary>
        Money,
    }

    /// <summary>
    /// Every placeholder the four projects use, with its one class. A new placeholder fails the scan
    /// until it is added here, so the class is a decision somebody makes, not a default.
    /// </summary>
    private static readonly Dictionary<string, PlaceholderClass> ClassOf = new(StringComparer.Ordinal)
    {
        ["AccountId"] = SurrogateKey,
        ["ActorUserId"] = SurrogateKey,
        ["AuthorizationId"] = SurrogateKey,
        ["FromId"] = SurrogateKey,
        ["Key"] = SurrogateKey, // the client's Idempotency-Key: an opaque request id it generated
        ["NewId"] = SurrogateKey,
        ["OldId"] = SurrogateKey,
        ["RecipientAccountId"] = SurrogateKey,
        ["Reference"] = SurrogateKey, // a notice's own id, printed on the notice for the holder to quote
        ["ToAccountId"] = SurrogateKey,
        ["ToId"] = SurrogateKey,
        ["TokenId"] = SurrogateKey, // the refresh token's ROW id, never the token
        ["TransactionNumber"] = SurrogateKey,
        ["UserId"] = SurrogateKey,

        ["SessionId"] = SecretPrefix,

        ["Email"] = MaskedPii,

        ["Age"] = Operational,
        ["Attempt"] = Operational,
        ["Attempted"] = Operational,
        ["Attempts"] = Operational,
        ["AuditedEvent"] = Operational,
        ["BatchSize"] = Operational,
        ["Bytes"] = Operational,
        ["Claimed"] = Operational,
        ["Codes"] = Operational,
        ["Count"] = Operational,
        ["CurrentLevel"] = Operational,
        ["Delivered"] = Operational,
        ["Directory"] = Operational,
        ["Endpoint"] = Operational,
        ["ErrorCode"] = Operational,
        ["ErrorCount"] = Operational,
        ["Event"] = Operational,
        ["ExceptionType"] = Operational,
        ["ExpiresAt"] = Operational,
        ["FailureType"] = Operational,
        ["Held"] = Operational,
        ["Interval"] = Operational,
        ["IntervalSeconds"] = Operational,
        ["LeaseSeconds"] = Operational,
        ["Max"] = Operational,
        ["MaxPasses"] = Operational,
        ["Message"] = Operational, // an exception's message: figure-free by rule (3769dc9)
        ["Method"] = Operational,
        ["Operation"] = Operational,
        ["Outcome"] = Operational,
        ["Owed"] = Operational,
        ["Partition"] = Operational,
        ["Path"] = Operational, // see the remarks: a path's VALUE can still carry a handle
        ["PendingRows"] = Operational,
        ["PeriodSeconds"] = Operational,
        ["Receipt"] = Operational,
        ["Runner"] = Operational,
        ["RunnerName"] = Operational,
        ["SecurityEvent"] = Operational,
        ["Site"] = Operational,
        ["Status"] = Operational,
        ["StatusCode"] = Operational,
        ["TimeoutSeconds"] = Operational,
        ["Until"] = Operational,

        // Listed so that the message names the rule when one comes back, rather than "unknown".
        ["AzureTag"] = DirectIdentifier,
        ["FirstName"] = DirectIdentifier,
        ["LastName"] = DirectIdentifier,
        ["Name"] = DirectIdentifier,
        ["PreviousAzureTag"] = DirectIdentifier,
        ["RecipientTag"] = DirectIdentifier,
        ["SenderTag"] = DirectIdentifier,

        ["Amount"] = Money,
        ["Balance"] = Money,
    };

    private static readonly string[] Projects =
    [
        Path.Combine("src", "AzureBank.Api"),
        Path.Combine("src", "AzureBank.Bff"),
        Path.Combine("src", "AzureBank.Infrastructure"),
        Path.Combine("src", "AzureBank.Functions.NoticeRelay"),
    ];

    private static readonly Regex LogCall = new(
        @"\b(?:_?[Ll]ogger|Log|_log)\s*\.\s*"
        + @"(?:Log(?:Trace|Debug|Information|Warning|Error|Critical)|Log|Information|Warning|Error|Debug|Fatal|Verbose)"
        + @"\s*\(");

    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)(?:[,:][^}]*)?\}");

    private sealed record Site(string File, int Line, string Template, string Arguments)
    {
        public IEnumerable<string> Placeholders =>
            Placeholder.Matches(Template.Replace("{{", string.Empty).Replace("}}", string.Empty))
                .Select(m => m.Groups[1].Value);

        public override string ToString() => $"{File}:{Line}";
    }

    [Fact]
    public void TheScanFindsTheLogCalls()
    {
        // The guard on the guard: a scan that silently matched nothing would pass every rule below.
        // Measured on 2026-09-11, offenders fixed: 146 log calls and 239 placeholder uses, the same
        // 239 an independent scan found. Each floor sits just under its measured value, so the scan
        // going half blind fails here instead of passing everything below.
        var sites = Sites();
        sites.Should().HaveCountGreaterThanOrEqualTo(140, "146 log calls were measured");
        sites.Sum(s => s.Placeholders.Count()).Should().BeGreaterThanOrEqualTo(230, "239 uses were measured");
    }

    [Fact]
    public void EveryPlaceholderHasItsClass()
    {
        var unclassified = Sites()
            .SelectMany(site => site.Placeholders.Where(p => !ClassOf.ContainsKey(p)).Select(p => $"{{{p}}} at {site}"))
            .ToList();

        unclassified.Should().BeEmpty(
            "every placeholder is a decision about what may reach an exported log: add it to ClassOf "
            + "with the one class it belongs to (ADR-0017's log-identifier rule)");
    }

    [Fact]
    public void NoLogLineNamesADirectIdentifierOrMoney()
    {
        var offenders = Sites()
            .SelectMany(site => site.Placeholders
                .Where(p => ClassOf.TryGetValue(p, out var c) && c is DirectIdentifier or Money)
                .Select(p => $"{{{p}}} ({ClassOf[p]}) at {site}"))
            .ToList();

        offenders.Should().BeEmpty(
            "a handle, a name or an amount in an exported log is personal or financial data that the "
            + "surrogate key beside it already makes unnecessary (ADR-0017)");
    }

    [Fact]
    public void ASecretReachesALogOnlyAsItsPrefix()
    {
        var bare = Sites()
            .Where(site => site.Placeholders.Any(p => ClassOf.GetValueOrDefault(p) == SecretPrefix)
                && !site.Arguments.Contains("SecretPrefix.Of(", StringComparison.Ordinal))
            .Select(site => site.ToString())
            .ToList();

        bare.Should().BeEmpty("a session id is a credential; the log may carry its first eight characters, through the one helper");
    }

    [Fact]
    public void AnEmailReachesALogOnlyMasked()
    {
        var bare = Sites()
            .Where(site => site.Placeholders.Any(p => ClassOf.GetValueOrDefault(p) == MaskedPii)
                && !site.Arguments.Contains("Redact(", StringComparison.Ordinal))
            .Select(site => site.ToString())
            .ToList();

        bare.Should().BeEmpty("an address reaches a log only through the PII redactor (ADR-0017 D1-D3)");
    }

    private static List<Site> Sites()
    {
        var root = BackendRoot();
        var sites = new List<Site>();
        foreach (var project in Projects)
        {
            var folder = Path.Combine(root.FullName, project);
            Directory.Exists(folder).Should().BeTrue($"expected to scan {folder}");
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                // Comments are blanked first: this codebase quotes old log lines in its comments, and a
                // quoted line is history, not a call.
                var code = BlankComments(File.ReadAllText(file));
                var relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
                foreach (Match call in LogCall.Matches(code))
                {
                    var arguments = ArgumentsFrom(code, call.Index + call.Length);
                    var template = TemplateOf(arguments);
                    var line = code.AsSpan(0, call.Index).Count('\n') + 1;
                    sites.Add(new Site(relative, line, template, arguments));
                }
            }
        }

        return sites;
    }

    /// <summary>The call's argument text, from after its '(' to its matching ')'.</summary>
    private static string ArgumentsFrom(string code, int start)
    {
        var depth = 1;
        var i = start;
        while (i < code.Length && depth > 0)
        {
            var c = code[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }

            i++;
        }

        return code[start..Math.Max(start, i - 1)];
    }

    /// <summary>
    /// The message template: the string literals of the first top-level argument that is one —
    /// concatenated, since long templates are split with <c>+</c>. An exception or a level may come
    /// first, and is skipped.
    /// </summary>
    private static string TemplateOf(string arguments)
    {
        foreach (var argument in TopLevelArguments(arguments))
        {
            var literals = new StringBuilder();
            var i = 0;
            while (i < argument.Length)
            {
                if (argument[i] == '"')
                {
                    var end = SkipLiteral(argument, i);
                    literals.Append(argument[(i + 1)..Math.Max(i + 1, end - 1)]);
                    i = end;
                    continue;
                }

                i++;
            }

            if (literals.Length > 0)
            {
                return literals.ToString();
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> TopLevelArguments(string arguments)
    {
        var depth = 0;
        var from = 0;
        var i = 0;
        while (i < arguments.Length)
        {
            var c = arguments[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(arguments, i);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return arguments[from..i];
                from = i + 1;
            }

            i++;
        }

        yield return arguments[from..];
    }

    /// <summary>The index just past the string or char literal that starts at <paramref name="start"/>.</summary>
    private static int SkipLiteral(string text, int start)
    {
        var quote = text[start];
        var verbatim = quote == '"' && start > 0 && (text[start - 1] == '@' || (start > 1 && text[start - 2] == '@'));
        var i = start + 1;
        while (i < text.Length)
        {
            if (verbatim && text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (!verbatim && text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                return i + 1;
            }

            if (!verbatim && text[i] == '\n')
            {
                return i;
            }

            i++;
        }

        return i;
    }

    /// <summary>The source with every comment's characters replaced by spaces, newlines kept.</summary>
    private static string BlankComments(string source)
    {
        var text = source.ToCharArray();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] is '"' or '\'')
            {
                i = SkipLiteral(source, i);
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    text[i++] = ' ';
                }

                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] != '\n')
                    {
                        text[i] = ' ';
                    }

                    i++;
                }

                if (i + 1 < text.Length)
                {
                    text[i] = ' ';
                    text[i + 1] = ' ';
                }

                i += 2;
                continue;
            }

            i++;
        }

        return new string(text);
    }

    private static DirectoryInfo BackendRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AzureBank.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the scan needs the sources; a guard that cannot run must fail loudly");
        return dir!;
    }
}
