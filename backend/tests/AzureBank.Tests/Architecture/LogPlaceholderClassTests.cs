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
/// WHAT IT COULD NOT SEE UNTIL 2026-09-14, and how that was closed in review. A value reaching a
/// log through an operational placeholder: the request log printed the request path, and
/// <c>GET /api/users/{azureTag}</c> put a handle in it — measured,
/// <c>HTTP GET /api/users/janesmith responded 200</c>. This guard reads templates, and a path is a
/// template's value. So the request logs now name the ROUTE PATTERN (<c>RequestLogRoute</c> in the
/// API and the BFF), <c>{RequestPath}</c> is classified a direct identifier so its return is
/// refused, and the scan reads the request-log templates too (<c>MessageTemplate = "..."</c>).
/// Two channels stay outside any template guard and are closed elsewhere: ASP.NET Core's hosting
/// scope, which attached <c>RequestPath</c> to every event of a request (<c>RequestPathEnricher</c>
/// strips it), and YARP's forwarder, which logged the destination URL at Information (the BFF's
/// <c>appsettings.json</c> overrides it to Warning). <c>RequestLogRouteTests</c> in both test
/// projects reads every event of a request through the real host and finds no handle in any.
/// </para>
/// <para>
/// THE PARSER IS PART OF THE GUARD, and the same review found three shapes it dropped: a template
/// that is not a literal fell through as an empty template, so the site passed every rule unread;
/// <c>{@x}</c> and <c>{$x}</c> were not placeholders to it; and the two "through the helper" rules
/// accepted the helper ANYWHERE in the call, so an unrelated guarded argument covered a bare one.
/// Each is pinned in <see cref="TheParserSeesTheShapesTheRulesDependOn"/> against a snippet, and
/// the helper rules now bind each placeholder to the argument at ITS position.
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
        ["Elapsed"] = Operational,
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
        // An exception's message: figure-free by rule (3769dc9), but NOT handle-free for a domain
        // refusal -- "Recipient with identifier 'janesmith' was not found." -- which is why
        // AppExceptionHandler stopped logging it on 2026-09-14. The three sites left log framework
        // text (a bad request line, a token validation failure) or an UNHANDLED fault, whose text
        // is not classifiable and is named as open in ADR-0017.
        ["Message"] = Operational,
        ["Method"] = Operational,
        ["Operation"] = Operational,
        ["Outcome"] = Operational,
        ["Owed"] = Operational,
        ["Partition"] = Operational,
        ["PendingRows"] = Operational,
        ["PeriodSeconds"] = Operational,
        ["Receipt"] = Operational,
        ["RequestMethod"] = Operational,
        ["Resource"] = Operational, // one of five constants the BFF derives from the first path segment, never the segment itself
        ["RoutePattern"] = Operational, // "/api/users/{azureTag}": a constant of the code, never a value
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
        // The VALUE of a request path carries its route parameters, a handle among them (measured
        // 2026-09-11: "HTTP GET /api/users/janesmith responded 200"; and 2026-09-14 through the BFF's
        // test host, the AuthLevelMiddleware event of an unauthenticated GET /api/users/janesmith:
        // SecurityEvent="SessionRequired" | Method="GET" | Path="/api/users/janesmith"). Every line
        // names {RoutePattern} since 2026-09-14; these two entries are what refuse the path's return.
        ["Path"] = DirectIdentifier,
        ["RequestPath"] = DirectIdentifier,

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

    /// <summary>
    /// A request-log template (<c>UseSerilogRequestLogging</c>): Serilog's middleware binds its
    /// values, so there is no argument list to read, only the right-hand side up to the ';'.
    /// </summary>
    private static readonly Regex TemplateAssignment = new(@"\bMessageTemplate\s*=");

    /// <summary>
    /// A Serilog hole: an optional operator (<c>@</c> destructures, <c>$</c> stringifies), the
    /// name, an optional alignment or format. Group 1 is the operator, group 2 the name.
    /// </summary>
    private static readonly Regex Placeholder = new(@"\{([@$]?)([A-Za-z_][A-Za-z0-9_]*)(?:[,:][^}]*)?\}");

    /// <param name="File">The file, relative to the backend root, forward slashes.</param>
    /// <param name="Line">The line the call or the assignment starts on.</param>
    /// <param name="Templates">
    /// One message template per branch of the template argument: a literal (or literals joined
    /// with <c>+</c>) gives one, a conditional of two literals gives two. Empty when the argument
    /// is not something this parser can read, which <see cref="EveryLogCallHasALiteralTemplate"/>
    /// refuses.
    /// </param>
    /// <param name="Values">
    /// The arguments after the template, in order — the ones Serilog binds to the placeholders by
    /// position — or null for a request-log template, whose values the middleware supplies.
    /// </param>
    private sealed record Site(string File, int Line, IReadOnlyList<string> Templates, IReadOnlyList<string>? Values)
    {
        public bool HasLiteralTemplate => Templates.Count > 0;

        /// <summary>The project folder the file sits in, "src/AzureBank.Api" and the like.</summary>
        public string Project => string.Join('/', File.Split('/').Take(2));

        /// <summary>Every placeholder use, across every branch, in order of appearance.</summary>
        public IEnumerable<string> Placeholders => Templates.SelectMany(PlaceholdersOf).Select(p => p.Name);

        /// <summary>The placeholders written <c>{@x}</c>: the whole object, every property it has.</summary>
        public IEnumerable<string> Destructured =>
            Templates.SelectMany(PlaceholdersOf).Where(p => p.Operator == "@").Select(p => p.Name);

        public override string ToString() => $"{File}:{Line}";
    }

    private static IEnumerable<(string Operator, string Name)> PlaceholdersOf(string template) =>
        Placeholder.Matches(template.Replace("{{", string.Empty).Replace("}}", string.Empty))
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value));

    [Fact]
    public void TheScanFindsTheLogCalls()
    {
        /*
          The guard on the guard: a scan that silently matched nothing would pass every rule below.
          Measured on 2026-09-11, offenders fixed: 146 log calls and 239 placeholder uses, the same
          239 an independent scan found. Re-measured 2026-09-14 with the request-log templates in
          the scan and the conditional at NoticeSweep.cs:167 read per branch: 148 sites and 247
          uses — API 89, BFF 46, Infrastructure 10, NoticeRelay 3. Each floor sits just under its
          measurement, PER PROJECT as well as in total, because the total floors alone let a whole
          project drop out of the scan unnoticed: 145 sites still clear 140.
        */
        var sites = Sites();
        sites.Should().HaveCountGreaterThanOrEqualTo(146, "148 sites were measured on 2026-09-14");
        sites.Sum(s => s.Placeholders.Count()).Should().BeGreaterThanOrEqualTo(243, "247 uses were measured on 2026-09-14");
        var perProject = sites.GroupBy(s => s.Project).ToDictionary(g => g.Key, g => g.Count());
        perProject.Should().ContainKey("src/AzureBank.Api").WhoseValue.Should().BeGreaterThanOrEqualTo(85, "89 measured");
        perProject.Should().ContainKey("src/AzureBank.Bff").WhoseValue.Should().BeGreaterThanOrEqualTo(43, "46 measured");
        perProject.Should().ContainKey("src/AzureBank.Infrastructure").WhoseValue.Should().BeGreaterThanOrEqualTo(9, "10 measured");
        perProject.Should().ContainKey("src/AzureBank.Functions.NoticeRelay").WhoseValue.Should().BeGreaterThanOrEqualTo(2, "3 measured");
    }

    [Fact]
    public void EveryLogCallHasALiteralTemplate()
    {
        /*
          FAIL CLOSED. A template this parser cannot read as NOTHING BUT string literals — held in a
          variable, interpolated, built with '+' from an expression, defaulted with '??' — is one it
          cannot police, and until 2026-09-14 it read such a call as an empty template, so the site
          passed every rule below unexamined. Measured that day: none of the 146 calls needed
          anything but a literal, or a conditional of two, so the rule costs nothing.
        */
        var unreadable = Sites().Where(site => !site.HasLiteralTemplate).Select(site => site.ToString()).ToList();

        unreadable.Should().BeEmpty(
            "a message template this guard cannot read is one it cannot police: the template argument "
            + "must be string literals (joined with +), or a conditional of two such");
    }

    [Fact]
    public void NoLogLineDestructuresAnObject()
    {
        var destructured = Sites()
            .SelectMany(site => site.Destructured.Select(name => $"{{@{name}}} at {site}"))
            .ToList();

        destructured.Should().BeEmpty(
            "{@x} logs every property x has, and a class is decided per placeholder NAME; an object "
            + "has no single class, so its members reach the log unclassified (measured 2026-09-14: none)");
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
    public void EveryPlaceholderIsBoundToAnArgumentOfItsOwn()
    {
        /*
          Serilog binds holes to arguments BY POSITION, so a count that differs leaves a hole
          unrendered or a value unnamed — and the two helper rules below rely on the position to
          find the argument a placeholder owns. A conditional template is checked per branch.
        */
        var unbound = Sites()
            .Where(site => site.Values is not null)
            .SelectMany(site => site.Templates
                .Select(template => PlaceholdersOf(template).Count())
                .Where(count => count != site.Values!.Count)
                .Select(count => $"{site}: {count} placeholders, {site.Values!.Count} arguments"))
            .ToList();

        unbound.Should().BeEmpty("every placeholder takes the argument at its own position, and only that one");
    }

    /// <summary>The argument is one call to <c>SecretPrefix.Of</c> and nothing else.</summary>
    private static readonly Func<string, bool> SecretPrefixCall = value => IsExactlyACallTo(value, "SecretPrefix.Of");

    /// <summary>The argument is one call to <c>Redact</c>, on whatever holds the redactor, and nothing else.</summary>
    private static readonly Func<string, bool> RedactCall = value => IsExactlyACallTo(value, "Redact", receiverAllowed: true);

    [Fact]
    public void ASecretReachesALogOnlyAsItsPrefix()
    {
        // The helper's own behaviour -- eight characters, never more -- is SecretPrefixTests' to
        // hold; this rule holds that every session-id site goes through it and nothing else.
        AzureBank.Shared.Utilities.SecretPrefix.Length.Should().Be(8, "ADR-0017: the first eight characters");

        var bare = Offenders(Sites(), SecretPrefix, SecretPrefixCall);

        bare.Should().BeEmpty("a session id is a credential; the log may carry its first eight characters, through the one helper, "
            + "on the argument that placeholder owns");
    }

    [Fact]
    public void AnEmailReachesALogOnlyMasked()
    {
        var bare = Offenders(Sites(), MaskedPii, RedactCall);

        bare.Should().BeEmpty("an address reaches a log only through the PII redactor (ADR-0017 D1-D3), "
            + "on the argument that placeholder owns");
    }

    [Fact]
    public void TheParserSeesTheShapesTheRulesDependOn()
    {
        /*
          The rules read what the parser hands them, so a shape the parser drops is a rule that never
          fires. Measured against the parser instead of assumed: the four shapes a review named on
          2026-09-14 (the two Serilog operators, a template that is not a literal, a helper on the
          WRONG argument, a request-log template), the conditional at NoticeSweep.cs:167 this rewrite
          found when its first count-check read the two branches as one, and the six an adversarial
          pass over the rewrite found the same day — an interpolated template, a '??' default and a
          '+ expression' tail (all three read as readable literals), an EventId's text taken for the
          template, a generic type argument splitting a value at its comma, a raw-string literal
          mis-tokenised, and a request-log template joined with '+' read to its first piece. And the
          shape a third review round named: a guarded argument that is the helper PLUS something
          else -- "SecretPrefix.Of(other) + sessionId" -- which a prefix or substring test accepted;
          the argument must now be one call to the helper and nothing more.
        */
        const string code = """""
            _logger.LogInformation("Plain {Count} and {$Forced} and {@Whole}", count, tag, user);
            _logger.LogWarning(flag ? "Yes {A}" : "No {B} {C}", a, b);
            _logger.LogError(ex, message);
            _logger.LogInformation("Session {SessionId} on {TokenId}", sessionId, SecretPrefix.Of(token));
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath}";
            _logger.LogInformation($"User {user.AzureTag} paid {account.Balance}");
            _logger.LogInformation(template ?? "Fallback {UserId}", userId);
            _logger.LogError(ex, "Failed: " + ex.Message);
            _logger.LogWarning(new EventId(7, "Login"), "User {UserId} in {Count}", id, new Dictionary<string, int>().Count);
            _logger.LogInformation((flag ? "a {SessionId}" : "b {SessionId}"), SecretPrefix.Of(sessionId));
            _logger.LogInformation(""""
                Raw {AzureTag} on {Count}
                """", tag, count);
            options.MessageTemplate = "HTTP {RequestMethod} " + "{RequestPath} responded {StatusCode}";
            options.MessageTemplate = SharedTemplate;
            _logger.LogInformation("Session {SessionId}", SecretPrefix.Of(other) + sessionId);
            _logger.LogWarning("Failed login attempt for email {Email}", _piiRedactor.Redact(other) + email);
            _logger.LogWarning("Failed login attempt for email {Email}", _piiRedactor.Redact(request.Email));
            """"";

        var sites = SitesIn(code, "snippet.cs");

        sites.Should().HaveCount(16);
        sites[0].Placeholders.Should().Equal("Count", "Forced", "Whole");
        sites[0].Destructured.Should().Equal("Whole");
        sites[1].Templates.Should().Equal("Yes {A}", "No {B} {C}");
        sites[1].Values!.Select(v => v.Trim()).Should().Equal("a", "b");
        sites[2].HasLiteralTemplate.Should().BeFalse("a variable is a template this parser cannot read");
        sites[3].Values!.Select(v => v.Trim()).Should().Equal("sessionId", "SecretPrefix.Of(token)");
        Offenders([sites[3]], SecretPrefix, SecretPrefixCall)
            .Should().ContainSingle("the helper guards the token, not the session id: the old Contains passed this")
            .Which.Should().Be("{SessionId} at snippet.cs:4");
        sites[4].Templates.Should().Equal("HTTP {RequestMethod} {RequestPath}");
        sites[4].Values.Should().BeNull("the middleware supplies the values, not an argument list");
        sites[5].HasLiteralTemplate.Should().BeFalse("an interpolated string is built at the call; its holes are not Serilog's");
        sites[6].HasLiteralTemplate.Should().BeFalse("the template rendered is the variable, not the fallback literal");
        sites[7].HasLiteralTemplate.Should().BeFalse("a template built from an expression is not readable, whatever literal it starts with");
        sites[8].Templates.Should().Equal(["User {UserId} in {Count}"], "the EventId's text is at depth 1, so it is not the template");
        sites[8].Values!.Select(v => v.Trim()).Should().Equal("id", "new Dictionary<string, int>().Count");
        sites[9].Templates.Should().Equal("a {SessionId}", "b {SessionId}");
        Offenders([sites[9]], SecretPrefix, SecretPrefixCall)
            .Should().BeEmpty("both branches bind the one guarded argument");
        sites[10].Placeholders.Should().Equal("AzureTag", "Count");
        sites[10].Values!.Select(v => v.Trim()).Should().Equal("tag", "count");
        sites[11].Templates.Should().Equal("HTTP {RequestMethod} {RequestPath} responded {StatusCode}");
        sites[12].HasLiteralTemplate.Should().BeFalse("a request-log template held in a constant is not readable here");
        Offenders([sites[13]], SecretPrefix, SecretPrefixCall)
            .Should().ContainSingle("the helper plus a bare value is a bare value: a prefix test passed this")
            .Which.Should().Be("{SessionId} at snippet.cs:16");
        Offenders([sites[14]], MaskedPii, RedactCall)
            .Should().ContainSingle("the redactor plus a bare value is a bare value: a substring test passed this")
            .Which.Should().Be("{Email} at snippet.cs:17");
        Offenders([sites[15]], MaskedPii, RedactCall).Should().BeEmpty("one call to the redactor, nothing else");
    }

    /// <summary>
    /// Whether <paramref name="value"/> is ONE call to <paramref name="method"/> and nothing else:
    /// the callee (behind a receiver such as <c>_piiRedactor.</c> when <paramref name="receiverAllowed"/>),
    /// an opening parenthesis, a balanced argument list, and the closing parenthesis as the last
    /// character. A prefix or substring test accepted <c>SecretPrefix.Of(other) + sessionId</c> and
    /// <c>Redact(other) + email</c>, which a review round named on 2026-09-14.
    /// </summary>
    private static bool IsExactlyACallTo(string value, string method, bool receiverAllowed = false)
    {
        var text = value.Trim();
        var open = text.IndexOf('(');
        if (open < 0 || text[^1] != ')')
        {
            return false;
        }

        var callee = text[..open].Trim();
        var named = receiverAllowed
            ? (callee == method || callee.EndsWith("." + method, StringComparison.Ordinal))
                && callee.All(c => char.IsLetterOrDigit(c) || c is '_' or '.')
            : callee == method;
        if (!named)
        {
            return false;
        }

        // The '(' after the callee must be the one that closes at the very end.
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i) - 1;
                continue;
            }

            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth == 0)
            {
                return i == text.Length - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// Placeholders of <paramref name="class"/> whose OWN argument — the one at the placeholder's
    /// position — does not satisfy <paramref name="guarded"/>. A site with no argument list cannot
    /// pass, since no helper can stand there.
    /// </summary>
    private static List<string> Offenders(IEnumerable<Site> sites, PlaceholderClass @class, Func<string, bool> guarded) =>
        sites
            .SelectMany(site => site.Templates.SelectMany(template => PlaceholdersOf(template)
                .Select((p, index) => (p.Name, Index: index))
                .Where(p => ClassOf.GetValueOrDefault(p.Name) == @class)
                .Where(p => site.Values is null || p.Index >= site.Values.Count || !guarded(site.Values[p.Index]))
                .Select(p => $"{{{p.Name}}} at {site}")))
            .Distinct(StringComparer.Ordinal)
            .ToList();

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

                var relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
                sites.AddRange(SitesIn(File.ReadAllText(file), relative));
            }
        }

        return sites;
    }

    /// <summary>The log sites of one file's source: every log call, and every request-log template.</summary>
    private static List<Site> SitesIn(string source, string relative)
    {
        // Comments are blanked first: this codebase quotes old log lines in its comments, and a
        // quoted line is history, not a call.
        var code = BlankComments(source);
        var sites = new List<Site>();
        foreach (Match call in LogCall.Matches(code))
        {
            var (templates, values) = TemplateAndValuesOf(ArgumentsFrom(code, call.Index + call.Length));
            sites.Add(new Site(relative, LineOf(code, call.Index), templates, values));
        }

        foreach (Match assignment in TemplateAssignment.Matches(code))
        {
            // The right-hand side up to the statement's ';', read like a template argument: joined
            // literals are one template, anything else is unreadable and fails closed.
            var start = assignment.Index + assignment.Length;
            var end = IndexAtTopLevel(code, ';', start);
            var rhs = code[start..(end < 0 ? code.Length : end)];
            sites.Add(new Site(relative, LineOf(code, assignment.Index), BranchesOf(rhs), Values: null));
        }

        return sites.OrderBy(site => site.Line).ToList();
    }

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

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
    /// The message template(s) and the arguments after them. The template is the first top-level
    /// argument holding a string literal AT DEPTH ZERO — an exception, a level or an EventId may
    /// come first and is skipped, and an <c>EventId(7, "text")</c>'s text is not a template — read
    /// per branch when it is a conditional; the values are every argument after it. No such
    /// argument: no templates, and the values are not worth reading.
    /// </summary>
    private static (IReadOnlyList<string> Templates, IReadOnlyList<string>? Values) TemplateAndValuesOf(string arguments)
    {
        var parts = TopLevelArguments(arguments).ToList();
        for (var k = 0; k < parts.Count; k++)
        {
            if (!HasLiteralAtDepthZero(Unwrapped(parts[k])))
            {
                continue;
            }

            var values = parts.Skip(k + 1).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            return (BranchesOf(parts[k]), values);
        }

        return ([], null);
    }

    /// <summary>
    /// A conditional template — <c>flag ? "a {X}" : "b {Y}"</c> — is two templates, one per branch;
    /// anything else is one. Either way a branch must be NOTHING BUT string literals joined with
    /// <c>+</c>, or the whole argument is unreadable: an interpolated string, a <c>??</c> default or
    /// a <c>+ expression</c> tail all build the template at the call, out of this parser's sight.
    /// </summary>
    private static IReadOnlyList<string> BranchesOf(string argument)
    {
        var text = Unwrapped(argument);
        var question = IndexAtTopLevel(text, '?', 0);
        var colon = question < 0 ? -1 : IndexAtTopLevel(text, ':', question + 1);
        if (question >= 0 && colon >= 0)
        {
            var yes = LiteralTemplate(text[(question + 1)..colon]);
            var no = LiteralTemplate(text[(colon + 1)..]);
            return yes is null || no is null ? [] : [yes, no];
        }

        var one = LiteralTemplate(text);
        return one is null ? [] : [one];
    }

    /// <summary>The text without one pair of enclosing parentheses, when it is wrapped in exactly that.</summary>
    private static string Unwrapped(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[^1] != ')')
        {
            return text;
        }

        // The '(' must close at the very end, not earlier: "(a) + (b)" is not wrapped.
        var depth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(trimmed, i) - 1;
                continue;
            }

            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth == 0 && i < trimmed.Length - 1)
            {
                return text;
            }
        }

        return trimmed[1..^1];
    }

    /// <summary>Whether a string literal sits outside every bracket of <paramref name="text"/>.</summary>
    private static bool HasLiteralAtDepthZero(string text)
    {
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                if (depth == 0)
                {
                    return true;
                }

                i = SkipLiteral(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            depth += c is '(' or '[' or '{' ? 1 : c is ')' or ']' or '}' ? -1 : 0;
            i++;
        }

        return false;
    }

    /// <summary>
    /// The literals of <paramref name="text"/> joined, or null unless the text is nothing but string
    /// literals and '+' (an <c>@</c> verbatim prefix allowed): the only shape whose template this
    /// parser can know at rest.
    /// </summary>
    private static string? LiteralTemplate(string text)
    {
        var literals = new StringBuilder();
        var residue = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (IsInterpolated(text, i))
                {
                    return null;
                }

                var end = SkipLiteral(text, i);
                literals.Append(ContentOf(text, i, end));
                i = end;
                continue;
            }

            residue.Append(text[i]);
            i++;
        }

        if (literals.Length == 0 || residue.ToString().Any(c => !char.IsWhiteSpace(c) && c != '+' && c != '@'))
        {
            return null;
        }

        return literals.ToString();
    }

    /// <summary>Whether the literal opening at <paramref name="quote"/> is <c>$"</c>, <c>$@"</c> or <c>@$"</c>.</summary>
    private static bool IsInterpolated(string text, int quote) =>
        (quote > 0 && text[quote - 1] == '$') || (quote > 1 && text[quote - 2] == '$' && text[quote - 1] == '@');

    /// <summary>The text inside the literal that spans <paramref name="start"/>..<paramref name="end"/>: quotes stripped, raw-string fences too.</summary>
    private static string ContentOf(string text, int start, int end)
    {
        var fence = QuoteRunAt(text, start);
        var inner = text[(start + fence)..Math.Max(start + fence, end - fence)];
        return fence >= 3 ? inner.Trim('\r', '\n', ' ') : inner;
    }

    private static int QuoteRunAt(string text, int start)
    {
        var n = 0;
        while (start + n < text.Length && text[start + n] == '"')
        {
            n++;
        }

        return n;
    }

    /// <summary>The index of <paramref name="wanted"/> outside literals and brackets, or -1.</summary>
    private static int IndexAtTopLevel(string text, char wanted, int from)
    {
        var depth = 0;
        var i = from;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i);
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
            else if (c == wanted && depth == 0)
            {
                // "??" is not a conditional; skip the pair.
                if (wanted == '?' && i + 1 < text.Length && text[i + 1] == '?')
                {
                    i += 2;
                    continue;
                }

                return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>
    /// The call's arguments split at the commas outside literals, brackets and generic type argument
    /// lists — <c>new Dictionary&lt;string, int&gt;()</c> is one value, not two.
    /// </summary>
    private static IEnumerable<string> TopLevelArguments(string arguments)
    {
        var depth = 0;
        var angle = 0;
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
            else if (c == '<' && i > 0 && (char.IsLetterOrDigit(arguments[i - 1]) || arguments[i - 1] is '_' or '>' or '?'))
            {
                angle++;
            }
            else if (c == '>' && angle > 0 && arguments[i - 1] != '=')
            {
                angle--;
            }
            else if (c == ',' && depth == 0 && angle == 0)
            {
                yield return arguments[from..i];
                from = i + 1;
            }

            i++;
        }

        yield return arguments[from..];
    }

    /// <summary>
    /// The index just past the string or char literal that starts at <paramref name="start"/>: a
    /// plain literal with its escapes, a verbatim one with its doubled quotes, or a raw one closed
    /// by a run of quotes at least as long as the one that opened it.
    /// </summary>
    private static int SkipLiteral(string text, int start)
    {
        var quote = text[start];
        var fence = quote == '"' ? QuoteRunAt(text, start) : 1;
        if (fence >= 3)
        {
            var i = start + fence;
            while (i < text.Length)
            {
                if (text[i] == '"' && QuoteRunAt(text, i) >= fence)
                {
                    return i + QuoteRunAt(text, i);
                }

                i++;
            }

            return i;
        }

        var verbatim = quote == '"' && start > 0 && (text[start - 1] == '@' || (start > 1 && text[start - 2] == '@'));
        var j = start + 1;
        while (j < text.Length)
        {
            if (verbatim && text[j] == '"')
            {
                if (j + 1 < text.Length && text[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            if (!verbatim && text[j] == '\\')
            {
                j += 2;
                continue;
            }

            if (text[j] == quote)
            {
                return j + 1;
            }

            if (!verbatim && text[j] == '\n')
            {
                return j;
            }

            j++;
        }

        return j;
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
