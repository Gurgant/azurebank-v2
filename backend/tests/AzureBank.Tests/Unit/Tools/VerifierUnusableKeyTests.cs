using AzureBank.AuditVerifier.Commands;
using AzureBank.AuditVerifier.Extensions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// <c>verify</c>, <c>evidence</c> and <c>notify</c> on the verifier's own composition root, with a
/// key the tool cannot use: exit 3, the sentence of the command, the key that is wrong, and no
/// connection opened.
/// </summary>
/// <remarks>
/// <para>
/// <c>RealCompositionRootRefusalTests</c> asks <c>anchor</c> and <c>export</c> this question. The
/// other three verbs had the same guard and no test of it: a verifier holding the wrong chain key
/// does not fail, it recomputes every hash and calls an intact chain broken at sequence 1, so the
/// refusal before the first row is the property.
/// </para>
/// <para>
/// ZERO OPENS IS ONLY EVIDENCE BESIDE A COUNT THAT IS NOT ZERO (<c>OpenCountingInterceptor</c>).
/// The three tests "on a server that is not there" are that count, one for each verb: the same
/// instrument on the same registration, with keys the tool accepts.
/// </para>
/// </remarks>
public sealed class VerifierUnusableKeyTests : IDisposable
{
    // Port 1 on loopback: nothing listens there, and nothing leaves the machine.
    private const string AbsentServer =
        "Server=127.0.0.1,1;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1;TrustServerCertificate=True";

    private const string ChainKeyName = "Audit:ChainKey";
    private const string AnchorKeyName = "Audit:AnchorKey";

    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "azurebank-unusable-key-" + Guid.NewGuid().ToString("N")))
            .FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory is not worth failing a green run over (ExportCommandTests).
        }
    }

    private static ServiceProvider RealProvider(OpenCountingInterceptor opens, string? chainKey, string? anchorKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = AbsentServer,
            [ChainKeyName] = chainKey,
            [AnchorKeyName] = anchorKey,
        }).Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInterceptor>(opens);
        services.AddVerifierServices(configuration, environment.Object);
        return services.BuildServiceProvider();
    }

    private static (string? ChainKey, string? AnchorKey) Keys(string unusable) => unusable switch
    {
        "a short chain key" => ("tooshort10", new string('a', 40)),
        "no chain key" => (null, new string('a', 40)),
        "a short anchor key" => (new string('k', 40), "tooshort10"),
        _ => throw new ArgumentOutOfRangeException(nameof(unusable), unusable, "not a case of this test"),
    };

    [Theory]
    [InlineData("a short chain key", ChainKeyName, AnchorKeyName)]
    [InlineData("no chain key", ChainKeyName, AnchorKeyName)]
    [InlineData("a short anchor key", AnchorKeyName, ChainKeyName)]
    public async Task Verify_WithAnUnusableKey_ExitsThree_NamesThatKey_AndOpensNothing(
        string unusable, string wrongKey, string otherKey)
    {
        var opens = new OpenCountingInterceptor();
        var (chainKey, anchorKey) = Keys(unusable);
        await using var provider = RealProvider(opens, chainKey, anchorKey);

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider, CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Misconfigured, "a key the tool cannot use is no verdict, never 1");
        lines[0].Should().Be("CANNOT VERIFY: this tool is not configured to read the chain.");
        text.Should().Contain($"{wrongKey} must be configured with at least 32 characters");
        text.Should().NotContain($"{otherKey} must be configured", "the other key is fine");
        text.Should().NotContain("could not be read", "that sentence is about the store, and the store was not asked");
        opens.Opens.Should().Be(0, "no row is read with a key that was never checked");
    }

    [Theory]
    [InlineData("a short chain key", ChainKeyName)]
    [InlineData("a short anchor key", AnchorKeyName)]
    public async Task Evidence_WithAnUnusableKey_ExitsThree_NamesThatKey_AndOpensNothing(string unusable, string wrongKey)
    {
        var opens = new OpenCountingInterceptor();
        var (chainKey, anchorKey) = Keys(unusable);
        await using var provider = RealProvider(opens, chainKey, anchorKey);

        var (exitCode, lines) = await EvidenceCommand.RunAsync(
            provider, "TXN-20260101-0000000000Z", CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Misconfigured);
        lines[0].Should().Be("CANNOT ASSEMBLE: this tool is not configured to read the chain.");
        text.Should().Contain($"{wrongKey} must be configured with at least 32 characters");
        text.Should().NotContain("could not be read");
        opens.Opens.Should().Be(0, "the evidence of a movement is not assembled under an unchecked key");
    }

    [Theory]
    [InlineData("a short chain key", ChainKeyName)]
    [InlineData("a short anchor key", AnchorKeyName)]
    public async Task Notify_WithAnUnusableKey_ExitsThree_NamesThatKey_AndWritesNoFile(string unusable, string wrongKey)
    {
        var opens = new OpenCountingInterceptor();
        var (chainKey, anchorKey) = Keys(unusable);
        await using var provider = RealProvider(opens, chainKey, anchorKey);

        var (exitCode, lines) = await NotifyCommand.RunAsync(
            provider, _directory, "security@your-bank.example", CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Misconfigured);
        lines[0].Should().Be("CANNOT NOTIFY: this tool is not configured to read the store.");
        text.Should().Contain($"{wrongKey} must be configured with at least 32 characters");
        opens.Opens.Should().Be(0);
        Directory.EnumerateFileSystemEntries(_directory).Should().BeEmpty("nothing is rendered from a store that was not read");
    }

    [Fact]
    public async Task Verify_WithUsableKeys_OnAServerThatIsNotThere_IsNoVerdict_AndTheOpenIsCounted()
    {
        // The count that makes the zeros above mean something, and the outage on the tool's own
        // root: the database's own exception is the one printed, by its type.
        // Measured 2026-10-12: exit 3, and the second line begins "  SqlException: A
        // network-related or instance-specific error occurred while establishing a connection".
        var opens = new OpenCountingInterceptor();
        await using var provider = RealProvider(opens, new string('k', 40), new string('a', 40));

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider, CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Misconfigured, "a server that is not there is no verdict, never 1");
        lines[0].Should().Be("CANNOT VERIFY: the audit store could not be read, so there is no verdict.");
        lines[1].Should().StartWith("  SqlException: ", "the operator is shown the database's own failure");
        string.Join("\n", lines).Should().Contain("NOT a statement about the chain");
        opens.Opens.Should().BeGreaterThan(0, "the instrument is attached and this command does reach for the store");
    }

    [Fact]
    public async Task Evidence_WithUsableKeys_OnAServerThatIsNotThere_IsNoVerdict_AndTheOpenIsCounted()
    {
        // Measured 2026-10-12: exit 3, the third line "  SqlException: A network-related ...".
        var opens = new OpenCountingInterceptor();
        await using var provider = RealProvider(opens, new string('k', 40), new string('a', 40));

        var (exitCode, lines) = await EvidenceCommand.RunAsync(
            provider, "TXN-20260101-0000000000Z", CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Misconfigured, "no evidence and no verdict, and never 4: the number was not looked up");
        lines[0].Should().Be("CANNOT ASSEMBLE: the store could not be read, so there is no evidence and no");
        lines[2].Should().StartWith("  SqlException: ", "the first database failure in the chain is the one printed");
        opens.Opens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Notify_WithUsableKeys_OnAServerThatIsNotThere_IsNoVerdict_AndTheOpenIsCounted()
    {
        // Measured 2026-10-12: exit 3, "(SqlException)" in the first line, no file.
        var opens = new OpenCountingInterceptor();
        await using var provider = RealProvider(opens, new string('k', 40), new string('a', 40));

        var (exitCode, lines) = await NotifyCommand.RunAsync(
            provider, _directory, "security@your-bank.example", CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Misconfigured);
        lines[0].Should().Be("CANNOT NOTIFY: the store could not be read or written (SqlException).");
        opens.Opens.Should().BeGreaterThan(0);
        Directory.EnumerateFileSystemEntries(_directory).Should().BeEmpty();
    }
}
