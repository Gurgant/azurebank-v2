using System.Reflection;
using System.Text.RegularExpressions;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps <see cref="SecurityEvents"/> the single source of truth for the names an operator alerts on.
/// </summary>
/// <remarks>
/// <para>
/// Twenty-one log sites across the API and the BFF used to spell their event name inline. Nothing
/// checked that two sites meant to emit the same event agreed, and nothing would have failed if one
/// of them were renamed: the alert would simply stop matching. For a detective control that is the
/// worst failure mode available, because the system keeps reporting green while the signal is gone.
/// </para>
/// <para>
/// Modelled on <see cref="ErrorCodeConstantTests"/>, with one deliberate difference. That rule can
/// key off the SHAPE of the value (SCREAMING_SNAKE is distinctive enough to ban outright); event
/// names are PascalCase, which is the shape of half the string literals in any C# file, so a
/// shape-based ban would be unusable. This rule is anchored on CONTEXT instead: it finds the log
/// template and inspects the argument that fills its placeholder. That makes it precise — it cannot
/// fire on an unrelated literal — and it also makes it narrow, which is why
/// <see cref="EveryMentionOfASecurityEventUsesTheCanonicalPlaceholder"/> exists below.
/// </para>
/// </remarks>
public class SecurityEventConstantTests
{
    /// <summary>
    /// The exact template every security-event log line opens with. It is the anchor the scan finds,
    /// so it is also a contract: see the placeholder rule below.
    /// </summary>
    private const string Template = "SecurityEvent {SecurityEvent}";

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
    /// Both projects, whole. Unlike the error-code rule this is not scoped to a Services folder:
    /// eight of the twenty-five sites live in the BFF's middleware and its Program, and scoping is what
    /// let a third of the vocabulary sit outside anybody's rule in the first place.
    /// </summary>
    private static readonly string[] ScannedFolders =
    [
        Path.Combine("src", "AzureBank.Api"),
        Path.Combine("src", "AzureBank.Bff"),
    ];

    private static IEnumerable<string> SourceFiles(DirectoryInfo root)
    {
        foreach (var relative in ScannedFolders)
        {
            var folder = Path.Combine(root.FullName, relative);
            Directory.Exists(folder).Should().BeTrue(because: $"expected to scan {folder}");

            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                // bin/obj hold generated copies of the same sources; scanning them would double every
                // offender and, worse, report a stale one from a previous build.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>A PascalCase string literal — the shape an inline event name has.</summary>
    private static readonly Regex PascalCaseLiteral = new("\"[A-Z][A-Za-z0-9]{3,}\"", RegexOptions.Compiled);

    /// <summary>
    /// The end of the message literal and the separator after it: a closing quote that is not
    /// escaped, then any whitespace (including a line break), then the comma.
    /// </summary>
    private static readonly Regex MessageEnd = new("(?<!\\\\)\"\\s*,", RegexOptions.Compiled);

    /// <summary>
    /// One whole ordinary string literal that mentions SecurityEvent, wherever the statement wraps.
    /// </summary>
    private static readonly Regex SecurityEventLiteral = new(
        "\"[^\"\\n]*SecurityEvent[^\"\\n]*\"", RegexOptions.Compiled);

    /// <summary>
    /// A verbatim or raw string that mentions SecurityEvent — the shape the rule above cannot read.
    /// </summary>
    private static readonly Regex MultilineStringMentioningSecurityEvent = new(
        "(@\"|\"\"\")[^\"]*SecurityEvent", RegexOptions.Compiled);

    /// <summary>
    /// The text of the first argument after the message, stopping at the comma that ENDS it.
    /// </summary>
    /// <remarks>
    /// Splitting on the first comma outright was the obvious version and it is wrong: an argument
    /// like <c>string.Join(",", codes)</c> or any nested call would be cut in half, and a literal
    /// after the cut would go unseen — a guard with a silent hole in exactly the place a complicated
    /// argument lives. Tracking parenthesis depth costs ten lines and removes that. A ternary needs
    /// no special handling: it contains no top-level comma, so both of its branches are inside this
    /// one argument, which is what caught <see cref="SecurityEvents.RegistrationRejected"/>.
    /// </remarks>
    private static string FirstArgument(string afterMessage)
    {
        var depth = 0;
        for (var i = 0; i < afterMessage.Length; i++)
        {
            var c = afterMessage[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']')
            {
                // A closing bracket at depth 0 ends the whole call: the first argument was the last.
                if (depth == 0) return afterMessage[..i];
                depth--;
            }
            else if (c == ',' && depth == 0) return afterMessage[..i];
        }

        return afterMessage;
    }

    /// <summary>What the scan found at one log site: an inline name, or an unreadable site.</summary>
    /// <param name="Line">1-based line of the offending literal, or of the template when unreadable.</param>
    /// <param name="Literal">The inline literal including its quotes; null when the site could not be read.</param>
    internal readonly record struct ScanFinding(int Line, string? Literal);

    /// <summary>
    /// Every site in <paramref name="text"/> that writes its event name inline, or that the scan
    /// could not read. Separated from the file walk so the SCANNER ITSELF is testable — see
    /// <see cref="TheScannerSeesWhatItClaimsTo"/>.
    /// </summary>
    internal static IEnumerable<ScanFinding> Scan(string text)
    {
        for (var i = text.IndexOf(Template, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(Template, i + Template.Length, StringComparison.Ordinal))
        {
            /*
              Look only at what FILLS the placeholder, not at the whole statement.

              The event name is the first argument after the message, so the first PascalCase
              literal following the template is either that name written inline, or — once the
              name is a constant — a literal belonging to a LATER argument. The window is bounded
              so a long statement cannot reach into the next one and report a neighbour's string.
            */
            var searchFrom = i + Template.Length;
            var window = text[searchFrom..Math.Min(text.Length, searchFrom + 600)];
            var templateLine = text[..searchFrom].Count(c => c == '\n') + 1;

            /*
              Find where the MESSAGE ends, tolerating whitespace before the comma.

              This used to be `IndexOf("\",")`, requiring the quote and comma to be adjacent, and a
              review was right to flag it — though the danger is worse than the one described. A
              skipped site is the benign half: it only happens when the window holds no separator at
              all. The likely half is a silent MIS-PARSE — put a space or a line break before the
              comma and the adjacent-pair search sails past the real separator and locks onto a later
              `",` belonging to some other literal in the window, after which the scan inspects a
              region that has nothing to do with this call and reports nothing.

              The lookbehind rejects an escaped quote inside the message, which would otherwise end
              the literal early. No template contains one today; it costs nothing to be right anyway.
            */
            var separator = MessageEnd.Match(window);
            if (!separator.Success)
            {
                // NOT skipped. A site whose argument list the scan cannot find is a site where an
                // inline name would pass unseen, so it is reported — the same principle as the
                // placeholder rule. A log call carrying the template with NO arguments lands here
                // too, and deserves to: its {SecurityEvent} placeholder would render literally.
                yield return new ScanFinding(templateLine, null);
                continue;
            }

            var argumentStart = separator.Index + separator.Length;
            var argument = FirstArgument(window[argumentStart..]);

            var match = PascalCaseLiteral.Match(argument);
            if (match.Success)
            {
                // The line of the LITERAL, not of the template: it is the line that has to be edited.
                var line = text[..(searchFrom + argumentStart + match.Index)].Count(c => c == '\n') + 1;
                yield return new ScanFinding(line, match.Value);
            }
        }
    }

    [Fact]
    public void NoSecurityEventNameIsWrittenAsABareStringLiteral()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles(RepoBackendRoot()))
        {
            foreach (var finding in Scan(File.ReadAllText(file)))
            {
                offenders.Add(finding.Literal is null
                    ? $"{Path.GetFileName(file)}:{finding.Line}: the argument list of this site could not be read — "
                      + "keep the event name as the first argument after the message literal"
                    : $"{Path.GetFileName(file)}:{finding.Line}: {finding.Literal} — declare it in SecurityEvents and reference it");
            }
        }

        offenders.Should().BeEmpty(
            because: "an event name written inline drifts from the constant, and the alert that watched it "
                     + "stops matching without anything failing");
    }

    /*
      THE SCANNER'S OWN BEHAVIOUR, pinned.

      A source-scanning guard is only worth its comment if it actually fires, and nothing above
      proves that: the repository rule passes today because the sources are clean, which is exactly
      what it would do if the scan were broken. These cases feed synthetic snippets straight to
      Scan(), so a change that quietly narrows it fails here instead of years later.

      Two of them exist because of a review finding on this file. The scan used to require the
      message's closing quote and the comma to be ADJACENT; with a space or a line break between
      them it locked onto a later `",` and inspected the wrong region, reporting nothing. Both
      shapes are below, and both failed against the old implementation.
    */
    public static TheoryData<string, string> SitesWithAnInlineName() => new()
    {
        { "adjacent comma", """_logger.LogWarning("SecurityEvent {SecurityEvent}: x", "Inline", y);""" },
        { "space before the comma", """_logger.LogWarning("SecurityEvent {SecurityEvent}: x" , "Inline", y);""" },
        {
            "line break before the comma",
            "_logger.LogWarning(\"SecurityEvent {SecurityEvent}: x\"\n            , \"Inline\", y);"
        },
        {
            "inline name in the second branch of a ternary",
            """_logger.LogWarning("SecurityEvent {SecurityEvent}: x", flag ? SecurityEvents.A : "Inline", y);"""
        },
        {
            "inline name inside a call in the first argument",
            """_logger.LogWarning("SecurityEvent {SecurityEvent}: x", Pick(flag, "Inline"), y);"""
        },
        {
            // An escaped quote FOLLOWED BY A COMMA inside the message. The obvious version of this
            // case — `said \"hi\"` — was measured and discarded: the old scan handled it by luck,
            // so pinning it would have proved nothing. This shape is the one that discriminates.
            "escaped quote and comma inside the message",
            """_logger.LogWarning("SecurityEvent {SecurityEvent}: said \", not the end", "Inline", y);"""
        },
    };

    [Theory]
    [MemberData(nameof(SitesWithAnInlineName))]
    public void TheScannerSeesWhatItClaimsTo(string shape, string snippet)
    {
        var findings = Scan(snippet).ToList();

        findings.Should().ContainSingle(because: $"an inline name written with {shape} must be caught");
        findings[0].Literal.Should().Be("\"Inline\"");
    }

    public static TheoryData<string, string> CleanSites() => new()
    {
        { "a constant", """_logger.LogWarning("SecurityEvent {SecurityEvent}: x", SecurityEvents.A, y);""" },
        {
            "a constant in both branches of a ternary",
            """_logger.LogWarning("SecurityEvent {SecurityEvent}: x", flag ? SecurityEvents.A : SecurityEvents.B, y);"""
        },
        {
            "a PascalCase literal in a LATER argument",
            """_logger.LogWarning("SecurityEvent {SecurityEvent}: {Method}", SecurityEvents.A, "Options");"""
        },
        {
            "an exception overload, template second",
            """_logger.LogWarning(ex, "SecurityEvent {SecurityEvent}: x", SecurityEvents.A);"""
        },
    };

    [Theory]
    [MemberData(nameof(CleanSites))]
    public void TheScannerDoesNotCryWolf(string shape, string snippet)
    {
        Scan(snippet).Should().BeEmpty(because: $"a site using {shape} is not an offender");
    }

    [Fact]
    public void TheScannerReportsASiteItCannotRead()
    {
        // No argument list at all: the placeholder would render literally, and an inline name added
        // later would be invisible. Silence here was the other half of the review finding.
        var findings = Scan("""_logger.LogWarning("SecurityEvent {SecurityEvent}: no arguments at all");""").ToList();

        findings.Should().ContainSingle();
        findings[0].Literal.Should().BeNull(because: "an unreadable site is reported, not skipped");
    }

    [Fact]
    public void EveryMentionOfASecurityEventUsesTheCanonicalPlaceholder()
    {
        /*
          The blind spot of the rule above, closed.

          That scan finds sites by looking for one exact template. A log line written as
          "SecurityEvent {Event}: ..." — or "SecurityEvent: ..." with the name interpolated — emits
          onto the same channel, reads identically to a human, and is invisible to the scanner. This
          is the same reasoning as the ErrorCodes rule requiring every declared value to match the
          scanner's own pattern: a guard has to be able to SEE the thing it guards.
        */
        var offenders = new List<string>();

        foreach (var file in SourceFiles(RepoBackendRoot()))
        {
            var text = File.ReadAllText(file);

            /*
              Matched as a LITERAL over the whole file, not line by line.

              The first version read one line at a time, and a review was right that it was fragile:
              a message wrapped across two source lines would put `"SecurityEvent` on one and
              `{SecurityEvent}` on the next, so the rule would report an offender that is not one —
              and a genuinely miswritten template could hide in the same seam.

              A regular C# string literal cannot contain a raw newline, so `[^"\n]*` between the
              quotes matches exactly one whole literal and nothing else, wherever the statement is
              broken. That is precise without a parser. The review suggested Roslyn; it would work,
              but it buys a compiler dependency for a rule whose entire subject is a single-line
              literal, and the multi-line forms it would handle are refused outright below.
            */
            foreach (Match match in SecurityEventLiteral.Matches(text))
            {
                if (match.Value.Contains(Template, StringComparison.Ordinal))
                {
                    continue;
                }

                var line = text[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add(
                    $"{Path.GetFileName(file)}:{line}: {match.Value} — open with \"{Template}: …\" "
                    + "so the constant rule can see this site");
            }

            /*
              The one shape the regex above cannot read, refused rather than ignored.

              A verbatim (@"…") or raw ("""…""") string CAN span lines, so a template written that
              way would slip past both rules. There are none today and no reason to introduce one, so
              the honest handling is to fail and say so — rather than let the guard quietly narrow.
            */
            foreach (Match multiline in MultilineStringMentioningSecurityEvent.Matches(text))
            {
                var line = text[..multiline.Index].Count(c => c == '\n') + 1;
                offenders.Add(
                    $"{Path.GetFileName(file)}:{line}: a verbatim or raw string mentions SecurityEvent — "
                    + "write the template as an ordinary single-line literal, which is what both rules can read");
            }
        }

        offenders.Should().BeEmpty(
            because: "a site the scanner cannot find is a site where a bare literal passes unnoticed");
    }

    [Fact]
    public void TheEventInventoryThisAdrStatesIsStillTheOneInTheSource()
    {
        /*
          THE NUMBERS IN THE PROSE ARE NOW LOAD-BEARING, because they went stale in FOUR places at
          once and nothing said a word.

          ADR-0044 decides which security events are audited and which stay log-only, and it decides
          it by NAMING them and counting them. Adding one event to the BFF left behind: D4's "the six
          BFF events", AuditOutcome's "23 existing sites" and "13 of the 23", and this very file's
          own summary comment. A reviewer caught one of the four. That is the failure this test
          exists to make impossible — not a wrong number, but a wrong number nobody can see.

          Counting the CANONICAL TEMPLATE rather than the constants is deliberate: one constant can
          be raised at several sites, and it is the sites the ADR counts. The same template is what
          every other scan in this file anchors on, so the two cannot drift apart either.

          WHEN THIS GOES RED, do not just move the number here. The whole point is that it names the
          documents that must move with it.
        */
        /*
          THE NAMES, NOT ONLY THE COUNT — and this is a correction to the first version of this test,
          which counted sites and stopped there. Swapping one event for another keeps the count at
          seven and would have sailed through, which is the same weakness as asserting a substring
          instead of an identity. The set is what ADR-0044 D4 actually writes down, so the set is
          what gets compared; the totals stay below as a cheaper second signal.
        */
        string[] bffInventory =
        [
            "CrossSiteRequestBlocked",
            "RateLimitExceeded",
            "RawAuthEntryBlocked",
            "RawRefreshBlocked",
            "RefreshRejected",
            "SessionRequired",
            "StepUpRequired",
            "StepUpWithoutSession",
        ];

        const int apiSites = 17;
        const int bffSites = 8;

        var perProject = SourceFiles(RepoBackendRoot())
            .GroupBy(f => f.Contains(Path.Combine("src", "AzureBank.Bff"), StringComparison.Ordinal)
                ? "Bff"
                : "Api")
            .ToDictionary(
                g => g.Key,
                g => g.Sum(f => CountOccurrences(File.ReadAllText(f), Template)));

        // ContainKeys takes params, so a because-string here would be read as a THIRD key — caught by
        // running it. Two explicit assertions instead, each able to say why it matters.
        perProject.Should().ContainKey(
            "Api", "a project scanning to nothing would make every count below vacuously wrong");
        perProject.Should().ContainKey(
            "Bff", "a project scanning to nothing would make every count below vacuously wrong");

        EventsNamedOnTemplateLines(SourceFiles(RepoBackendRoot())
                .Where(f => f.Contains(Path.Combine("src", "AzureBank.Bff"), StringComparison.Ordinal)))
            .Should().BeEquivalentTo(
                bffInventory,
                "ADR-0044 D4 lists the BFF's log-only events by name. A set comparison catches a "
                + "SUBSTITUTION that the totals below cannot see — one event removed and another "
                + "added leaves every count untouched");

        perProject["Api"].Should().Be(
            apiSites,
            "ADR-0044 says \"Seventeen security events are logged\" and \"Seven of the seventeen API "
            + "events write a row today\". If the API's count moved, update that ADR's Context and its "
            + "\"What is wired\" section, and AuditOutcome's per-outcome tallies, in the same commit");

        perProject["Bff"].Should().Be(
            bffSites,
            "ADR-0044 D4 names the BFF events one by one and calls them \"the eight BFF events\". If "
            + "this moved, add or remove the name there too — the list is the count");

        (perProject["Api"] + perProject["Bff"]).Should().Be(
            25,
            "AuditOutcome's remarks derive the four outcomes from \"the 25 existing security-event log "
            + "sites\" and tally \"15 of the 25\" as Refused; both have to move with this");
    }

    /// <summary>
    /// The distinct <c>SecurityEvents.X</c> names mentioned on lines that carry the canonical
    /// template — i.e. the events a project actually raises, as opposed to every constant it can see.
    /// </summary>
    private static IEnumerable<string> EventsNamedOnTemplateLines(IEnumerable<string> files) =>
        files
            .SelectMany(f => NamesNearTemplate(File.ReadAllLines(f)))
            .Distinct()
            .Order()
            .ToArray();

    /*
      The name is often on the NEXT line, because the template sits in the message string and the
      constant is passed as an argument below it. Two lines of lookahead covers every site in this
      repository; a site that drifts further apart shows up as a missing name rather than silently
      passing, which is the failure direction to prefer.
    */
    private static IEnumerable<string> NamesNearTemplate(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(Template, StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = i; j < Math.Min(i + 3, lines.Length); j++)
            {
                var match = Regex.Match(lines[j], @"SecurityEvents\.([A-Za-z]+)");
                if (match.Success)
                {
                    yield return match.Groups[1].Value;
                    break;
                }
            }
        }
    }

    /// <summary>Non-overlapping occurrences of <paramref name="needle"/>, counted plainly.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void EveryDeclaredEventIsUniqueAndScannable()
    {
        var values = typeof(SecurityEvents)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        values.Should().OnlyHaveUniqueItems(
            because: "two constants sharing a value make the log ambiguous about which event fired");
        values.Should().NotBeEmpty(because: "reflection finding nothing would make this vacuous");

        // The same closing argument ErrorCodeConstantTests makes: a value the scanner's own pattern
        // cannot match is a value the scanner cannot police. An empty or lower-case name would leave
        // the literal rule above passing while that event drifted freely.
        values.Should().OnlyContain(
            value => PascalCaseLiteral.IsMatch($"\"{value}\""),
            because: "an event name the scanner cannot match is an event name the scanner cannot guard");
    }
}
