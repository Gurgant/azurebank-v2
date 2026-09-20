using AzureBank.Tests.Integration;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// The Bruno environment a developer runs, <c>tests/api-collection/environments/local.bru</c>, is
/// TRACKED — so it carries no service credential, and the collection's README does not ask for one.
/// </summary>
/// <remarks>
/// <para>
/// Review found the documentation defect this guard makes mechanical (2026-09-19). That file ships
/// <c>serviceKey</c> empty, and its README said "put your own <c>ServiceCredential:BffKey</c> in
/// it" — an instruction whose obedient reader commits their own credential, since nothing in
/// <c>.gitignore</c> covers the path. The key is supplied per run instead:
/// <c>bru run . --env local --env-var serviceKey=...</c>.
/// </para>
/// <para>
/// Measured with Bruno CLI 4.1.0 against the running API on 2026-09-19: with the key passed that
/// way <c>register</c> answered 201, and with <c>local.bru</c>'s empty value every request answered
/// 401. So the empty default is not a broken file — it is the file refusing to hold a secret, and
/// the flag is what makes it usable.
/// </para>
/// <para>
/// <c>ci.bru</c> is deliberately NOT checked: it carries a throwaway value for a workflow that
/// stands up its own database and API, and nothing in it is anyone's secret.
/// </para>
/// </remarks>
public class BrunoEnvironmentSecretTests
{
    private static string LocalEnvironment => Path.Combine(
        RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName,
        "tests", "api-collection", "environments", "local.bru");

    [Fact]
    public void TheLocalBrunoEnvironment_CarriesNoServiceCredential()
    {
        File.Exists(LocalEnvironment).Should().BeTrue("a guard that cannot read its file must fail loudly");

        var serviceKey = File.ReadAllLines(LocalEnvironment)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("serviceKey:", StringComparison.Ordinal))
            .Select(line => line["serviceKey:".Length..].Trim())
            .ToList();

        serviceKey.Should().ContainSingle("the variable is declared, so a run without the flag fails loudly");
        serviceKey[0].Should().BeEmpty(
            "this file is tracked and not ignored, so a key written here is a key committed; pass it "
            + "per run instead: bru run . --env local --env-var serviceKey=...");
    }

    [Fact]
    public void TheCollectionsReadme_DoesNotAskForTheKeyToBeWrittenIntoThatFile()
    {
        var readme = File.ReadAllText(Path.Combine(
            RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName,
            "tests", "api-collection", "README.md"));

        readme.Should().Contain("--env-var serviceKey=", "the per-run flag is the documented method");
        readme.Should().NotContain("put your own `ServiceCredential:BffKey` in it",
            "that sentence is the defect: its obedient reader commits their credential");
    }
}
