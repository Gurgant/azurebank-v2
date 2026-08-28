using AzureBank.AuditVerifier.Commands;
using AzureBank.AuditVerifier.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// A misconfigured key must produce exit 3 and a sentence on the provider the TOOL builds, not just
/// on the one a test builds.
/// </summary>
/// <remarks>
/// <para>
/// All three commands were written with the same point-of-use key guard, returning
/// <c>Misconfigured</c> with prose. Then <c>Audit:AnchorKey</c> was added to the options
/// <c>Validate()</c> in <c>ServiceCollectionExtensions</c>, checking the identical predicate.
/// Reaching a point-of-use guard means reading <c>options.Value</c>, and reading <c>options.Value</c>
/// is what triggers that validation -- so on two of the three the exception was thrown one line
/// before the guard and nothing caught it. Only <c>VerifyCommand</c> survived, because only
/// <c>VerifyCommand</c> invoked the validator itself inside a <c>try</c>.
/// </para>
/// <para>
/// MEASURED 2026-08-28 on the shipped build, <c>Audit__AnchorKey</c> unset and again at 10
/// characters: <c>verify</c> exited <b>3</b> with "CANNOT VERIFY: this tool is not configured to read
/// the chain"; <c>export</c> and <c>anchor</c> printed an unhandled
/// <c>OptionsValidationException</c> and exited <b>4</b>, which this tool defines as "the command
/// line was wrong" -- on a command line that was right. The guards' own sentences were printed zero
/// times.
/// </para>
/// <para>
/// The whole suite was green while that was true, and this is the reason: the existing tests build
/// their provider by hand with <c>Options.Create(...)</c>, which registers no validation and no
/// <c>IStartupValidator</c>, so <c>options.Value</c> returns the bad value quietly and the guard is
/// reached. They assert a branch production cannot enter. The command is not what differed -- the
/// composition root is. So these tests use the real one, <c>AddVerifierServices</c>, exactly as
/// <c>Program.cs</c> does.
/// </para>
/// <para>
/// It was not a test that found it. It was running the recovery procedure in
/// <c>docs/runbooks/audit-chain-unavailable.md</c> as written, which is the practice that also found
/// the missing key in that procedure. A nearby fixture had already met the symptom and routed around
/// it -- <c>VerifierUsesAStreamingContextTests</c> carries the note that "a fixture supplying only
/// one builds a host that refuses to start" -- without asking what an operator would see.
/// </para>
/// <para>
/// FALSIFIED by reverting either <c>try</c>/<c>catch</c>: the assertion fails on the exception, and
/// with the exception swallowed it fails on 4 against 3.
/// </para>
/// </remarks>
public class RealCompositionRootRefusalTests
{
    /// <summary>A well-formed chain key and an <b>unusable</b> anchor key: 10 characters, minimum 32.</summary>
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=Unreached;Trusted_Connection=True",
            ["Audit:ChainKey"] = new string('k', 40),
            ["Audit:AnchorKey"] = "tooshort10",
        }).Build();

    private static IHostEnvironment Environment()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");
        return environment.Object;
    }

    /*
      NO DATABASE IS NEEDED AND THAT IS THE POINT. The refusal has to happen before anything is read,
      so a connection string naming a database that does not exist is the honest fixture: if these
      tests ever start needing one, the guard has moved after the first query and the guarantee is
      gone. Registration does not connect -- AddDbContext is lazy.
    */
    private static ServiceProvider RealProvider(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerifierServices(configuration ?? Configuration(), Environment());
        return services.BuildServiceProvider();
    }

    /// <summary>The <b>other</b> key is the unusable one, and nothing else changes.</summary>
    private static IConfiguration BadChainKeyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=Unreached;Trusted_Connection=True",
            ["Audit:ChainKey"] = "tooshort10",
            ["Audit:AnchorKey"] = new string('a', 40),
        }).Build();

    /*
      A COMMAND MUST NAME THE KEY THAT ACTUALLY FAILED, not the key it is named after. The first
      version of anchor's catch discarded the exception and reported Audit:AnchorKey unconditionally,
      because that is the secret the command is about -- so a machine with a bad Audit:ChainKey was
      sent to look at the wrong one. Both keys are validated together and `anchor` needs both: the
      chain key to verify what it is anchoring, the anchor key to authenticate the record.

      This is the same defect as reporting a database outage over a typo, which the path guard in
      ExportCommand was written to prevent. It was reintroduced one commit after that fix, in the
      catch that fixed something else -- and caught in review rather than by a test, which is why
      this one exists.
    */
    [Theory]
    [InlineData(true, "Audit:ChainKey", "Audit:AnchorKey")]
    [InlineData(false, "Audit:AnchorKey", "Audit:ChainKey")]
    public async Task AnchorNamesTheKeyThatFailed_NotTheOneItIsNamedAfter(
        bool chainKeyIsBad, string expected, string notExpected)
    {
        await using var provider = RealProvider(chainKeyIsBad ? BadChainKeyConfiguration() : Configuration());

        var (exitCode, lines) = await AnchorCommand.RunAsync(provider, CancellationToken.None);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Misconfigured);
        text.Should().Contain(
            expected,
            "the operator is sent to the secret that is actually wrong");
        text.Should().NotContain(
            $"{notExpected} must be configured",
            "naming the other key's failure would send them to a setting that is fine");
    }

    [Fact]
    public async Task ExportRefusesAnUnusableAnchorKeyWithAVerdictCode_NotAUsageError()
    {
        await using var provider = RealProvider();
        var path = Path.Combine(Path.GetTempPath(), $"unreached-{Guid.NewGuid():N}.jsonl");

        var (exitCode, lines) = await ExportCommand.RunAsync(provider, path, CancellationToken.None);

        exitCode.Should().Be(
            VerifyCommand.Misconfigured,
            "a key this machine cannot use is a fact about the configuration, and 4 would send the "
            + "operator to re-read a command line that was correct");
        exitCode.Should().NotBe(VerifyCommand.UsageError);
        string.Join(" ", lines).Should().Contain("Audit:AnchorKey");
        File.Exists(path).Should().BeFalse("nothing was read, so nothing may be written");
    }

    [Fact]
    public async Task AnchorRefusesAnUnusableAnchorKeyWithAVerdictCode_NotAUsageError()
    {
        await using var provider = RealProvider();

        var (exitCode, lines) = await AnchorCommand.RunAsync(provider, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Misconfigured);
        exitCode.Should().NotBe(VerifyCommand.UsageError);
        string.Join(" ", lines).Should().Contain("Audit:AnchorKey");
    }

    /*
      THE THREE VERBS MUST AGREE, which is the property the runbook's exit-code list depends on. That
      list is written per-CODE, not per-verb, so a reader is entitled to assume 3 means the same thing
      whichever verb produced it. For one release it did not, and the list said nothing because the
      list cannot see the code.
    */
    [Fact]
    public async Task AllThreeVerbsAnswerTheSameWayToTheSameMisconfiguration()
    {
        await using var provider = RealProvider();
        var path = Path.Combine(Path.GetTempPath(), $"agree-{Guid.NewGuid():N}.jsonl");

        var export = await ExportCommand.RunAsync(provider, path, CancellationToken.None);
        var anchor = await AnchorCommand.RunAsync(provider, CancellationToken.None);

        new[] { export.ExitCode, anchor.ExitCode }.Should().AllBeEquivalentTo(
            VerifyCommand.Misconfigured,
            "the runbook documents exit codes per code rather than per verb, so a verb that answers "
            + "differently makes that page wrong without changing a word of it");
    }
}
