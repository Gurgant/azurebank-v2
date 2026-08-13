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
    /// six of the twenty-one sites live in the BFF's middleware and its Program, and scoping is what
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

    [Fact]
    public void NoSecurityEventNameIsWrittenAsABareStringLiteral()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles(RepoBackendRoot()))
        {
            var text = File.ReadAllText(file);
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

                // Everything up to the first argument separator that follows the closing quote of the
                // message: the name lives there and nowhere else.
                var closingQuote = window.IndexOf("\",", StringComparison.Ordinal);
                if (closingQuote < 0)
                {
                    continue;
                }

                var argument = FirstArgument(window[(closingQuote + 2)..]);

                var match = PascalCaseLiteral.Match(argument);
                if (match.Success)
                {
                    var line = text[..(searchFrom + closingQuote)].Count(c => c == '\n') + 1;
                    offenders.Add(
                        $"{Path.GetFileName(file)}:{line}: {match.Value} — declare it in SecurityEvents and reference it");
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "an event name written inline drifts from the constant, and the alert that watched it "
                     + "stops matching without anything failing");
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
