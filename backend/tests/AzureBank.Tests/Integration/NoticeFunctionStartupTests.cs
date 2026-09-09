using AzureBank.Functions.NoticeRelay;
using AzureBank.Api.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

// A BARE `Program` IS THE API'S. This project references both hosts, the API's entry point is a
// top-level `Program` in the global namespace, and that is the one an unqualified name binds to —
// the compiler said so, CS0117 'Program' does not contain a definition for 'Register'. The alias
// names which host's root is under test, and reads better than the qualified form repeated.
using FunctionHost = AzureBank.Functions.NoticeRelay.Program;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What the Function's real composition root does with the <c>Notices</c> section (ADR-0051 D3):
/// the three shared rules the API also applies, asked about the Function instead — and refused
/// here, where nothing refused them before. The fourth rule, the lease against the period, is the
/// API's alone and the test below says why.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE SUITE FOR THE GAP THE PR CLOSES. Before ADR-0051 every rule in the API's root was
/// written <c>o.Runner != NoticeRunner.Api || …</c>, so with <c>Notices:Runner=Function</c> the
/// whole section went unchecked: no contact, no directory, a directory INSIDE A GIT REPOSITORY, and
/// a lease shorter than two periods were all accepted in silence. Nothing was wrong with that while
/// the API was the only host that could deliver. Every test below would have passed on <c>main</c>
/// by accepting what it now refuses, which is why each one asserts the MESSAGE and not merely that
/// something threw.
/// </para>
/// <para>
/// Through <see cref="FunctionHost.Register"/> on a bare collection — the <c>NoticeRelayStartupTests</c>
/// idiom — reading the <see cref="OptionsValidationException"/> the first resolution throws. Not
/// through a started Functions host: starting one needs the Core Tools and a storage account, and
/// the message is the thing under test because it is the only thing an operator sees.
/// </para>
/// </remarks>
public sealed class NoticeFunctionStartupTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "azurebank-function-startup-" + Guid.NewGuid().ToString("N"));

    public NoticeFunctionStartupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ACompleteSection_IsAccepted_AndCarriesTheValuesThroughUnchanged()
    {
        var options = Resolve(
            ("Notices:Runner", "Function"),
            ("Notices:PickupDirectory", _directory),
            ("Notices:Contact", "security@your-bank.example, +00 000 0000"),
            ("Notices:Schedule", "*/15 * * * * *"),
            ("Notices:LeaseSeconds", "120"));

        options.Runner.Should().Be(NoticeRunner.Function);
        options.Schedule.Should().Be("*/15 * * * * *", "the cadence binds like every other key");
        options.PickupDirectory.Should().Be(_directory);
        options.BatchSize.Should().Be(100, "the shipped default survives a section that does not set it");
    }

    [Fact]
    public void RunnerFunction_WithoutAContact_IsRefused_AndTheMessageNamesTheRunner()
    {
        RefusalOf(("Notices:Runner", "Function"), ("Notices:PickupDirectory", _directory))
            .Should().Contain("Notices:Contact").And.Contain("800-63B-4").And.Contain("Function",
                "the shared rule interpolates the runner it was asked about, so an operator running two "
                + "hosts learns WHICH one refused");
    }

    [Fact]
    public void RunnerFunction_WithoutASchedule_IsRefused_SoTheHostCannotComeUpDeliveringNothing()
    {
        /*
          THE GUARD ADR-0051 FIRST DECLINED, AND THE REASONING THAT DECLINED IT WAS WRONG.
          The draft said a worker-side check could not run before the Functions runtime resolves the
          trigger's %Notices:Schedule% during indexing. Reasoned, not measured. Measured now, with
          the key removed and `func start`:

            before  '%Notices:Schedule%' does not resolve to a value.
                    Function 'Functions.DeliverOwedNotices' failed indexing and will be disabled.
                    Job host started                      <- exit 0, delivering nothing
            after   OptionsValidationException: Notices:Schedule must be set when ...
                    Failed to start language worker process for runtime: dotnet-isolated.
                    'failed indexing': 0 occurrences       <- never indexed; func exits 1

          ValidateOnStart runs as the worker process starts, which is BEFORE the worker reports its
          metadata for indexing — the same order already observed for a missing Notices:Contact.
        */
        RefusalOf(
                ("Notices:Runner", "Function"),
                ("Notices:PickupDirectory", _directory),
                ("Notices:Contact", "security@your-bank.example"))
            .Should().Contain("Notices:Schedule")
            .And.Contain("WHATEVER Notices:Runner says",
                "the binding is resolved during indexing, before the flag is read")
            .And.Contain("delivers nothing",
                "the message has to say what the operator would otherwise have seen: a healthy host");
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Api")]
    public void TheScheduleIsAskedOfTHISHOST_WhateverTheFlagSays(string runner)
    {
        /*
          THE ONE RULE HERE THAT IS NOT RUNNER-CONDITIONAL, and an earlier version of this test
          asserted the opposite. The trigger binds %Notices:Schedule% and the Functions host resolves
          it during INDEXING, which happens before the flag is read — the flag is checked inside the
          invocation, and there are no invocations of a function that failed to index.

          Measured with Notices:Runner=Api and the key removed: three "'%Notices:Schedule%' does not
          resolve to a value" lines and one "failed indexing", with every runner-conditional
          validator silent. So a host set to step aside still cannot come up without a schedule; what
          it gets without this rule is an indexing error instead of a named refusal.
        */
        var resolve = () => Resolve(("Notices:Runner", runner), ("Notices:PickupDirectory", _directory));

        resolve.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("Notices:Schedule"));
    }

    [Fact]
    public void RunnerFunction_WithoutADirectory_IsRefused()
    {
        RefusalOf(("Notices:Runner", "Function"), ("Notices:Contact", "security@your-bank.example"))
            .Should().Contain("Notices:PickupDirectory").And.Contain("EXISTING").And.Contain("Function");
    }

    [Fact]
    public void RunnerFunction_WithADirectoryInsideAGitRepository_IsRefused()
    {
        /*
          The rule ADR-0045 D4 wrote and ADR-0048 D6 kept: a pickup directory is a spool of addresses
          at rest, and one under a repository is one commit away from being published. On main this
          exact configuration started an API and a Function host without a word.
        */
        var inside = Path.Combine(RepoRoot(), "backend", "src", "AzureBank.Functions.NoticeRelay");

        RefusalOf(
                ("Notices:Runner", "Function"),
                ("Notices:PickupDirectory", inside),
                ("Notices:Contact", "security@your-bank.example"))
            .Should().Contain("inside a git repository").And.Contain("Function",
                "D3 says the shared messages name the runner they were asked about, and this was the "
                + "one of the three that could not: its message was a constant with no interpolation");
    }

    [Fact]
    public void RunnerFunction_WithALeaseUnderTwiceThePeriod_IsACCEPTED_BecauseThisHostNeverReadsThePeriod()
    {
        /*
          THE FOURTH RULE IS NOT THIS HOST'S, and the first draft of ADR-0051 had it wrong. The API
          validates `LeaseSeconds >= 2 * PeriodSeconds` and refuses to start, because
          Notices:PeriodSeconds IS what it sleeps for. This host sleeps for whatever
          Notices:Schedule says — resolved by the Functions runtime before any of this code runs —
          and never reads PeriodSeconds at all. Refusing to start over it would be a rule guarding a
          number nothing uses, and, worse, an assurance about a cadence this host does not have.

          What replaces it is a RUNTIME check against the interval between this host's own ticks
          (ADR-0051 D5), which is the only cadence it can know, and which caught a 19-second gap on
          a 20-second schedule when it was measured.
        */
        var options = Resolve(
            ("Notices:Runner", "Function"),
            ("Notices:PickupDirectory", _directory),
            ("Notices:Contact", "security@your-bank.example"),
            // Supplied because THIS host's cadence is the schedule and it is now required of it;
            // the point of the row is the period rule, which must not fire.
            ("Notices:Schedule", "*/15 * * * * *"),
            ("Notices:PeriodSeconds", "60"),
            ("Notices:LeaseSeconds", "100"));

        options.LeaseSeconds.Should().Be(100, "the value binds; it is simply not a rule here");
        options.PeriodSeconds.Should().Be(60);
    }

    [Fact]
    public void TheSameSection_IsRefusedForTheApi_WhichIsTheHostThatSleepsForThatPeriod()
    {
        /*
          The other half, so the asymmetry is asserted rather than described. Identical numbers,
          identical keys; the only difference is which host the section names.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(Configuration(
            ("Notices:Runner", "Api"),
            ("Notices:PickupDirectory", _directory),
            ("Notices:Contact", "security@your-bank.example"),
            ("Notices:PeriodSeconds", "60"),
            ("Notices:LeaseSeconds", "100"),
            ("Audit:ChainKey", CustomWebApplicationFactory.AuditChainKey),
            ("Audit:AnchorKey", CustomWebApplicationFactory.AuditAnchorKey)));
        using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IOptions<NoticeRelayOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("Notices:LeaseSeconds"));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Api")]
    public void WhenTheFlagNamesAnotherRunner_ThisHostStartsRegardless(string runner)
    {
        /*
          THE ASYMMETRY IS DELIBERATE AND IS THE OTHER HALF OF D3. A host validates the configuration
          it would ACT on. This one is about to step aside, so refusing to start over a directory it
          will never write to would take it down for another runner's misconfiguration — and, run the
          other way, would take the API down for this one's.
        */
        var resolve = () => Resolve(
            ("Notices:Runner", runner),
            ("Notices:PickupDirectory", "Z:\\nowhere"),
            // Supplied because the schedule is NOT a runner rule — it is what this host needs to
            // exist, and without it the refusal below would be about the wrong key.
            ("Notices:Schedule", "*/15 * * * * *"));

        resolve.Should().NotThrow("the flag does not name this host, so the runner rules are not its to enforce");
    }

    [Fact]
    public void AnOutOfRangeLease_IsRefusedWhateverTheRunner()
    {
        // The [Range] annotations are not part of the runner rules on purpose: a lease out of range
        // is a misconfiguration even in a host that delivers nothing.
        RefusalOf(("Notices:Runner", "None"), ("Notices:LeaseSeconds", "5"))
            .Should().Contain("Notices:LeaseSeconds");
    }

    [Fact]
    public void TheRootRegistersTheHostStateAsASINGLETON_WhichIsWhatMakesD8AndD5True()
    {
        /*
          ONE KEYWORD DISABLES TWO DECISIONS WITH A GREEN SUITE, which is why this asserts the
          registration and not a behaviour. Every other test here builds NoticeRelayHostState by
          hand, so `AddSingleton` -> `AddTransient` was measured to leave the entire Notice filter
          green — 76 of 76 — while breaking both:

            D8: a fresh `func/...` name every tick, so a row left HELD by a failed delivery is
                unrecognisable to the next sweep. Stranded until its lease lapses, then claimed as a
                stranger — the duplicate the lease exists to prevent, reached from the other side.
            D5: `_lastTick` always null, so ObserveTick returns null forever and the lease warning
                can never fire. The guard becomes the wish six live ticks were spent arguing against.

          The class-level mutant (a name generated per read) is a DIFFERENT mutant and is caught by
          the tests above; the two are independent, and this is the layer that had nothing.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        FunctionHost.Register(Configuration(("Notices:Runner", "None")), new StubEnvironment(), services);

        services.Should().Contain(
            d => d.ServiceType == typeof(NoticeRelayHostState) && d.Lifetime == ServiceLifetime.Singleton,
            "the runner name and the previous tick must outlive the invocation, and a Function class "
            + "is constructed per invocation");
    }

    [Fact]
    public void TheRootRegistersSTARTUPValidation_SoABadSectionStopsTheHostRatherThanTheFirstTick()
    {
        /*
          WHY THIS EXISTS, and it is not a hypothetical. `.ValidateOnStart()` was removed from
          Program.Register twice by accident while this PR was being reviewed, and the whole suite
          stayed green both times — because every other test here resolves `IOptions<T>.Value`, and
          resolving validates whether or not validation was asked for at startup. So the suite
          pinned the RULES and not the MOMENT.

          The moment matters. With startup validation the worker process refuses to start and the
          operator gets the message once (measured 2026-09-08: removing Notices:Contact and running
          `func start` gives "Failed to start language worker process" and an
          OptionsValidationException naming the key and the runner). Without it the host starts,
          every tick throws inside the Function's constructor, and a misconfiguration reads as a
          recurring runtime fault instead of a configuration error.

          Asserted on the REGISTRATION rather than by starting a host: starting one needs the Core
          Tools and a storage account, which is not this suite's job. `ValidateOnStart()` is what
          puts IStartupValidator in the container; nothing else here does.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        FunctionHost.Register(Configuration(("Notices:Runner", "None")), new StubEnvironment(), services);

        services.Should().Contain(
            d => d.ServiceType == typeof(IStartupValidator),
            "ValidateOnStart() registers IStartupValidator, and it is the difference between a host "
            + "that refuses to start on a bad Notices section and one that throws on every tick");
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] notices)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=Unreached;Trusted_Connection=True",
        };
        foreach (var (key, value) in notices)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static NoticeRelayOptions Resolve(params (string Key, string Value)[] notices)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        FunctionHost.Register(Configuration(notices), new StubEnvironment(), services);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<NoticeRelayOptions>>().Value;
    }

    private static string RefusalOf(params (string Key, string Value)[] notices)
    {
        var resolve = () => Resolve(notices);
        var refusal = resolve.Should().Throw<OptionsValidationException>(
            "a partial Notices section must be refused by the validator the Function's own root registers").Which;
        return string.Join("\n", refusal.Failures);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "AzureBank.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("this guard reads the sources; one that cannot find them must say so rather than pass");
        return dir!.FullName;
    }

    /// <summary>
    /// The one thing <c>AddInfrastructure</c> asks of a host beyond configuration: whether it is
    /// Development, which decides only EF's sensitive-data logging. Production here, so a test can
    /// never turn that on by accident.
    /// </summary>
    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "AzureBank.Functions.NoticeRelay";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
