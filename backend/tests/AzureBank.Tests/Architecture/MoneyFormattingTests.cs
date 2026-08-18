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

    /// <summary>`{something:C}` — the currency format specifier, in any interpolation or template.</summary>
    private static readonly Regex CurrencyFormat = new(@"\{[^{}]*:C\d?\}", RegexOptions.Compiled);

    [Fact]
    public void NoSourceFormatsMoneyWithTheProcessCulture()
    {
        var root = RepoBackendRoot();
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (CurrencyFormat.IsMatch(lines[i]))
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
        var symbolBeforeAmount = new Regex(@"(?<!\})[$€£¥](\{|\d)", RegexOptions.Compiled);
        var root = RepoBackendRoot();
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

    [Fact]
    public void TheCurrencyIsDeclaredOnceAndDescribeAmountUsesIt()
    {
        // The guards above are prohibitions; this is the affirmative half. Without it, deleting the
        // declaration would leave both scans green while the product forgot what it is denominated in.
        ValidationRules.Currency.Should().Be("EUR");

        ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)
            .Should().Be("100000.00 EUR",
                "invariant digits and the ISO code — the exact text published on the four money schemas");

        ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)
            .Should().Be("0.01 EUR");
    }
}
