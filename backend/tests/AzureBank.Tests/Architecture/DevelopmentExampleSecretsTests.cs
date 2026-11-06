using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Each development settings example names, in its header comment, every secret that the Local
/// setup recipe in <c>docs/engineering-practices.md</c> sets on its host.
/// </summary>
/// <remarks>
/// <para>
/// Both hosts refuse to start without their secrets (<c>ValidateOnStart</c>), and the suite cannot
/// see a secret that an example leaves out: the test hosts supply their own values. The recipe is
/// the list, and <c>docs/engineering-traps.md</c> asks every new required option for its section
/// and its command in the API's example. <c>ServiceCredential:BffKey</c> (ADR-0055) was missing
/// from both examples while the recipe set it on both hosts, and nothing read either file.
/// </para>
/// <para>
/// The API's example also holds a placeholder for each secret. The BFF's holds none on purpose:
/// the recipe copies that file as it is, so a placeholder there would become the key whenever the
/// user-secret is missing, and the BFF would start with a key the API does not hold instead of
/// refusing to start and saying why.
/// </para>
/// </remarks>
public class DevelopmentExampleSecretsTests
{
    /// <summary>
    /// A command of the recipe, such as <c>dotnet user-secrets --project $API    set "Jwt:Secret"</c>.
    /// The project names are padded to one width, so any run of spaces follows them.
    /// </summary>
    private static readonly Regex RecipeCommand = new(
        @"^dotnet user-secrets --project \$(?<project>[A-Z]+)\s+set ""(?<name>[^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// A command in an example's header comment, such as
    /// <c>//   dotnet user-secrets set "Jwt:Secret"</c>.
    /// </summary>
    private static readonly Regex ExampleCommand = new(
        @"^\s*//\s*dotnet user-secrets set ""(?<name>[^""]+)""",
        RegexOptions.Compiled);

    /// <summary>The options the .NET JSON configuration provider reads a settings file with.</summary>
    private static readonly JsonDocumentOptions SettingsFile = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void TheApiExample_NamesEverySecretTheRecipeSetsOnTheApi_AndHoldsAPlaceholderForEach()
    {
        var recipe = RecipeNames("API");
        var example = Example("AzureBank.Api");

        CommandNames(example).Should().BeEquivalentTo(
            recipe,
            "the header of the API's example lists the same user-secrets commands as the recipe, so a "
            + "developer who starts from either one sets every secret the API reads");

        using var settings = JsonDocument.Parse(File.ReadAllText(example), SettingsFile);
        foreach (var name in recipe)
        {
            HasMember(settings.RootElement, name).Should().BeTrue(
                "the API's example holds a placeholder for {0}, as it does for every other secret", name);
        }
    }

    [Fact]
    public void TheBffExample_NamesTheSecretTheRecipeSetsOnTheBff_AndHoldsNoValueForIt()
    {
        var recipe = RecipeNames("BFF");
        var example = Example("AzureBank.Bff");

        CommandNames(example).Should().BeEquivalentTo(
            recipe,
            "the recipe copies the BFF's example as it is, and its header comment is where a "
            + "developer who starts from it reads which secret to set with user-secrets");

        using var settings = JsonDocument.Parse(File.ReadAllText(example), SettingsFile);
        foreach (var name in recipe)
        {
            HasMember(settings.RootElement, name).Should().BeFalse(
                "a placeholder for {0} in the BFF's example would become the key whenever the "
                + "user-secret is missing, and the BFF would start instead of refusing", name);
        }
    }

    /// <summary>
    /// The names the recipe sets with <c>--project $</c><paramref name="project"/>. The test fails
    /// when it finds none: a guard that reads no name would pass for ever.
    /// </summary>
    private static List<string> RecipeNames(string project)
    {
        var names = File.ReadAllLines(RepositoryFiles.ExistingFile("docs", "engineering-practices.md"))
            .Select(line => RecipeCommand.Match(line))
            .Where(match => match.Success && match.Groups["project"].Value == project)
            .Select(match => match.Groups["name"].Value)
            .ToList();

        names.Should().NotBeEmpty(
            "the Local setup recipe in docs/engineering-practices.md sets the secrets of ${0} with "
            + "user-secrets, one command a line",
            project);

        return names;
    }

    private static string Example(string project) =>
        RepositoryFiles.ExistingFile("backend", "src", project, "appsettings.Development.json.example");

    /// <summary>The names in the user-secrets commands of an example's comment lines.</summary>
    private static List<string> CommandNames(string example) =>
        File.ReadAllLines(example)
            .Select(line => ExampleCommand.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value)
            .ToList();

    /// <summary>
    /// Whether the settings hold the configuration key <paramref name="name"/>, whose sections are
    /// separated by colons, as nested members.
    /// </summary>
    private static bool HasMember(JsonElement root, string name)
    {
        var element = root;
        foreach (var section in name.Split(':'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(section, out element))
            {
                return false;
            }
        }

        return true;
    }
}
