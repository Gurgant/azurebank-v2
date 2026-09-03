using System.Reflection;
using System.Text.RegularExpressions;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the server out of the business of rendering currency.
/// </summary>
/// <remarks>
/// <para>
/// Nothing pinned this, which is exactly why it drifted. A literal <c>$</c> sat in
/// <c>DataAnnotationSchemaTransformer</c> and published "Amount must be between $0.01 and
/// $100,000.00." onto four money schemas in the committed OpenAPI document, on a product whose
/// every screen says €. Eighteen more sites formatted amounts with <c>:C</c>, which renders against
/// whatever culture the server process happens to start with — so the same build could tell one
/// user "$100,000.00" and another "100 000,00 €", and no test would notice either.
/// </para>
/// <para>
/// It drifted while a task to fix it was already open: four of the eighteen sites arrived in
/// <c>StepUpAuthorizationRequestValidators</c> AFTER the audit that listed them was written. A list
/// in a document cannot stop that; a failing build can.
/// </para>
/// <para>
/// A source scan rather than a NetArchTest rule, because the defect lives in format specifiers and
/// string literals, which are gone by the time the assembly exists. It FAILS rather than skips when
/// it cannot find the sources: a guard that quietly passes when it cannot do its job is worse than
/// no guard — the same rule the neighbouring scanners state.
/// </para>
/// </remarks>
public class MoneyFormattingTests
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
    /// Both projects that can put a sentence in front of a user or into the published contract. The
    /// BFF is not scanned: it forwards the API's problem bodies and composes none of its own.
    /// </summary>
    private static readonly string[] ScannedFolders =
    [
        Path.Combine("src", "AzureBank.Api"),
        Path.Combine("src", "AzureBank.Shared"),
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

    /*
      EVERY spelling of the currency specifier, not the one that happened to be in the tree.

      The first version of this guard read `:C\d?`, which is what the sites it was written against
      looked like. Measured against the runtime rather than assumed, four of eight legal spellings
      slipped past it and all four still render culture-dependent currency:

        {v:C}     -> $100,000.00          caught
        {v:C2}    -> $100,000.00          caught
        {v:c}     -> $100,000.00          MISSED
        {v:c2}    -> $100,000.00          MISSED
        {v:C10}   -> $100,000.0000000000  MISSED   (precision is 0-99, not one digit)
        {v:C0}    -> $100,000             caught
        {v,12:C}  -> $100,000.00          caught
        {v,12:c4} -> $100,000.0000        MISSED

      Standard numeric specifiers are case-insensitive for currency — unlike X/x, where case changes
      the output — so `c` is not a typo the compiler would reject. Raised by CodeRabbit on #118 and
      confirmed by running the eight above through decimal.ToString before believing it.
    */
    private static readonly Regex CurrencyFormat = new(@"\{[^{}]*:[cC]\d*\}", RegexOptions.Compiled);

    /// <summary>
    /// The same defect spelled as a method call. Neither regex above can see `amount.ToString("C")`,
    /// because there is no brace for them to match — none exists in the tree today, and this is here
    /// so that stays true.
    /// </summary>
    private static readonly Regex CurrencyToString =
        new(@"ToString\(\s*""[cC]\d*""", RegexOptions.Compiled);

    /// <summary>
    /// The ONE predicate. The scan below and the coverage theory further down must ask the same
    /// question, or the theory pins the regexes while the scan quietly stops using one of them —
    /// which is exactly what happened: dropping CurrencyToString from the scan left all sixteen
    /// tests green, because the theory was calling the regexes directly instead of this.
    /// </summary>
    /// <summary>
    /// A member called Currency assigned a string literal. Built by concatenation rather than one
    /// verbatim literal on purpose: the first version of this line reached disk with a raw 0x08
    /// BACKSPACE (U+0008) where a literal backslash-b was meant, so the pattern demanded a control character no source line
    /// contains and the guard silently matched nothing. It compiled, it read every file, and it
    /// reported clean — visible only in `od -c`.
    ///
    /// <para>
    /// The optional brace group is not decoration either: without it the pattern misses
    /// <c>public string Currency { get; set; } = "EUR";</c>, which is the exact shape one of the two
    /// real offenders had. The coverage theory below found that, not the scan.
    /// </para>
    /// </summary>
    private static readonly Regex CurrencyLiteral =
        new("Currency" + @"\s*(\{[^}]*\})?\s*(=>|=)\s*""", RegexOptions.Compiled);

    private static bool RendersCurrency(string line) =>
        CurrencyFormat.IsMatch(line) || CurrencyToString.IsMatch(line);

    [Fact]
    public void NoSourceFormatsMoneyWithTheProcessCulture()
    {
        var root = RepoBackendRoot();
        SourceFiles(root).Should().HaveCountGreaterThan(100,
            "a scan that reads nothing reports clean forever; this is the liveness check that "
            + "separates a working guard from one whose path filter quietly ate every file");
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (RendersCurrency(lines[i]))
                {
                    offenders.Add($"{Path.GetRelativePath(root.FullName, file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "`:C` renders against the SERVER process culture, so the currency a user is shown depends "
            + "on how the container started. State the amount with ValidationRules.DescribeAmount, or "
            + "leave it out and let the client format the number it already has");
    }

    [Fact]
    public void NoSourcePutsACurrencySymbolInAMessage()
    {
        /*
          The dollar sign that started this was NOT a culture artefact — it was typed. A regex over
          every `$` would be useless in C# (string interpolation opens with one), so this looks for a
          currency symbol immediately followed by a digit or an interpolation hole, which is what a
          rendered amount looks like and what ordinary interpolation never produces.
        */
        // The lookbehind is not decoration: PasswordHasher encodes its output as `salt$hash`, which
        // in an interpolated string is `}${`, and that dollar is a DELIMITER between two holes. The
        // defect being hunted never looks like that — it is a symbol opening an amount, as in
        // "between ${Min}" — so requiring that the symbol not follow a closing hole separates them
        // without exempting a file, which would go stale the moment the file moved.
        // `\s*` because "costs $ 100" is the same defect with a space in it, and the first version
        // of this guard missed it — found by sweeping my own regex the way the currency one was swept,
        // not by anyone reporting it.
        var symbolBeforeAmount = new Regex(@"(?<!\})[$€£¥]\s*(\{|\d)", RegexOptions.Compiled);
        var root = RepoBackendRoot();
        SourceFiles(root).Should().HaveCountGreaterThan(100,
            "a scan that reads nothing reports clean forever; this is the liveness check that "
            + "separates a working guard from one whose path filter quietly ate every file");
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            // This file names the symbols in order to forbid them.
            if (Path.GetFileName(file) == "MoneyFormattingTests.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // `$"` and `$@"` open an interpolated string; that dollar is syntax, not money.
                var stripped = line.Replace("$\"", "\"").Replace("$@\"", "@\"");
                if (symbolBeforeAmount.IsMatch(stripped))
                {
                    offenders.Add($"{Path.GetRelativePath(root.FullName, file)}:{i + 1}  {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a currency symbol written into a server message hard-codes the denomination into text "
            + "the client shows verbatim, and into the OpenAPI descriptions the transformer publishes");
    }

    /*
      THE GUARD'S OWN COVERAGE, pinned.

      The scans above answer "is the tree clean today". They cannot answer "would they notice", and
      that is the question that actually failed: the first `:C\d?` looked complete against the
      eighteen sites it was written for, and was blind to four legal spellings of the same thing.
      A prohibition nobody has watched refuse anything is a wish.

      So the detectors are driven directly here, against spellings that are NOT in the tree. This
      goes red if someone narrows a regex to make a new offender pass — the cheapest possible way to
      defeat the two scans above, and one that would otherwise leave them green forever.
    */
    [Theory]
    [InlineData("{amount:C}")]
    [InlineData("{amount:C2}")]
    [InlineData("{amount:c}")]           // case-insensitive specifier, not a typo
    [InlineData("{amount:c2}")]
    [InlineData("{amount:C10}")]         // precision is 0-99, so one digit is not enough
    [InlineData("{amount,12:C}")]        // alignment before the specifier
    [InlineData("{amount,12:c4}")]
    [InlineData("value.ToString(\"C\")")]   // no braces at all — invisible to the format regex
    [InlineData("value.ToString(\"c2\")")]
    public void TheCurrencyDetectorCatchesEverySpelling(string line)
    {
        RendersCurrency(line).Should().BeTrue(
            $"'{line}' renders currency against the process culture and must not slip past the scan");
    }

    [Theory]
    [InlineData("{count:D}")]            // a different specifier entirely
    [InlineData("{amount:F2}")]          // fixed-point is culture-dependent but NOT currency
    [InlineData("var c = 'C';")]
    [InlineData("// the :C specifier is what this file forbids")]  // prose about it, in braces-free text
    public void TheCurrencyDetectorLeavesEverythingElseAlone(string line)
    {
        RendersCurrency(line).Should().BeFalse(
            $"'{line}' is not a currency render, and a guard that fires on it would be turned off");
    }

    [Fact]
    public void OnlyValidationRulesMayNameTheCurrency()
    {
        /*
          "Say once what the currency is" was the claim, and it was not true when it was made.
          AccountMapper stated "EUR" twice and BalanceResponse defaulted to it a third time, so the
          declaration was the fourth statement of a fact three other places already made — and the
          value assertion below could not see any of them, because it only reads the constant.

          Raised by CodeRabbit on #118. The two mapper assignments were deleted outright rather than
          repointed: the DTO's own default already supplies the value, so the mapper had no business
          naming it at all.

          The rule is deliberately about ASSIGNMENT to a member called Currency rather than about the
          literal "EUR". A denylist of currency codes would be the same mistake as the first version
          of the format regex — shaped by what is in the tree today, blind to "USD" tomorrow.

          What this does NOT catch, stated rather than implied: a currency code appended inside some
          free-form message, as in "{amount:0.00} USD". The bound messages go through DescribeAmount
          and the symbol scan covers the symbols; a bare ISO code inside arbitrary prose is not
          distinguishable from ordinary text by a regex, and pretending otherwise would buy a guard
          that fires on comments and gets switched off.
        */
        var root = RepoBackendRoot();
        SourceFiles(root).Should().HaveCountGreaterThan(100,
            "a scan that reads nothing reports clean forever; this is the liveness check that "
            + "separates a working guard from one whose path filter quietly ate every file");
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            // The one file allowed to say it. This is the declaration itself.
            if (Path.GetFileName(file) == "ValidationRules.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (CurrencyLiteral.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetRelativePath(root.FullName, file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "the currency is declared once, in ValidationRules.Currency. A second place that names it "
            + "is a second thing to change when it moves, and the two will disagree");
    }

    /*
      COVERAGE FOR THE DECLARATION DETECTOR, and the reason it exists.

      The format detectors above already had a theory like this; this one did not, and that is
      precisely where an invisible byte survived. The pattern reached disk with a raw 0x08 in it,
      so it could never match, and the scan reported clean while a hard-coded currency sat three
      lines into a mapper. Nothing failed, which is the worst possible outcome for a prohibition.

      Driving the detector against strings that are NOT in the tree is what turns "the tree is
      clean" into "and the rule can still refuse".
    */
    [Theory]
    [InlineData("            Currency = \"EUR\",")]
    [InlineData("            Currency = \"USD\",")]          // not an EUR denylist
    [InlineData("    public string Currency { get; set; } = \"EUR\";")]
    [InlineData("    public string Currency => \"GBP\";")]   // expression-bodied
    [InlineData("Currency=\"CHF\"")]                        // no spaces
    public void TheDeclarationDetectorCatchesAHardCodedCurrency(string line)
    {
        CurrencyLiteral.IsMatch(line).Should().BeTrue(
            $"'{line}' names a currency outside ValidationRules and must not slip past the scan");
    }

    [Theory]
    [InlineData("    public string Currency { get; set; } = ValidationRules.Currency;")]
    [InlineData("        var currency = account.Currency;")]
    [InlineData("    /// The product's denomination, from the single declaration.")]
    public void TheDeclarationDetectorLeavesTheCorrectFormsAlone(string line)
    {
        CurrencyLiteral.IsMatch(line).Should().BeFalse(
            $"'{line}' does not hard-code a currency, and a guard that fires on it would be turned off");
    }

    [Fact]
    public void TheCurrencyIsDeclaredOnceAndDescribeAmountUsesIt()
    {
        // The guards above are prohibitions; this is the affirmative half. Without it, deleting the
        // declaration would leave both scans green while the product forgot what it is denominated in.
        ValidationRules.Currency.Should().Be("EUR");

        ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)
            .Should().Be("100000.00 EUR",
                "invariant digits and the ISO code — the exact text published on the six money schemas "
                + "(PublishedMoneyBoundsTests pins each)");

        ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)
            .Should().Be("0.01 EUR");
    }
}
