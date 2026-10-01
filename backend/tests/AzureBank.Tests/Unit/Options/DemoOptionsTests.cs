extern alias seeder;

using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using seeder::AzureBank.Seeder.Extensions;

// The folder's existing namespace: a namespace literally named "Options" under Unit would shadow
// `Options.Create(...)` in every sibling test file.
namespace AzureBank.Tests.Unit.Configuration;

/// <summary>
/// The demo's settings: off unless a deployment turns them on, with the numbers the pool is sized
/// by as defaults in code, and a host that reads them refusing to run on a value out of range.
/// </summary>
/// <remarks>
/// No committed <c>appsettings.json</c> carries a "Demo" section, so the defaults asserted here ARE
/// what a deployment gets when it sets only <c>Demo__Enabled=true</c>.
/// </remarks>
public class DemoOptionsTests
{
    private static DemoOptions Bound(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        return configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new DemoOptions();
    }

    private static ValidateOptionsResult Validated(DemoOptions options) => new DemoOptionsValidator().Validate(null, options);

    [Fact]
    public void WithNothingConfigured_TheDemoIsOff_AndThePoolHasItsDefaults()
    {
        var options = new DemoOptions();

        options.Enabled.Should().BeFalse("the demo is something a deployment turns on");
        options.CopyLifetimeHours.Should().Be(24);
        options.Pool.TargetFree.Should().Be(50);
        options.Pool.LowMark.Should().Be(20);
        options.Pool.MaxFreeAgeHours.Should().Be(44);
        options.Pool.MaxClaimsPerDay.Should().Be(150);
        options.Claim.MaxPerClientPerDay.Should().Be(10);
        options.Copy.MaxWrites.Should().Be(200);
        options.ClientKeySecret.Should().BeNull();

        Validated(options).Succeeded.Should().BeTrue("a deployment that never mentions the demo must start");
    }

    [Theory]
    [InlineData("Demo:CopyLifetimeHours", "0")]
    [InlineData("Demo:CopyLifetimeHours", "169")]
    [InlineData("Demo:Pool:TargetFree", "0")]
    [InlineData("Demo:Pool:TargetFree", "501")]
    [InlineData("Demo:Pool:LowMark", "-1")]
    [InlineData("Demo:Pool:MaxFreeAgeHours", "0")]
    [InlineData("Demo:Pool:MaxFreeAgeHours", "721")]
    [InlineData("Demo:Pool:MaxClaimsPerDay", "10001")]
    [InlineData("Demo:Claim:MaxPerClientPerDay", "0")]
    [InlineData("Demo:Claim:MaxPerClientPerDay", "1001")]
    [InlineData("Demo:Copy:MaxWrites", "9")]
    [InlineData("Demo:Copy:MaxWrites", "100001")]
    public void AValueOutsideItsRange_IsRefused_AndTheMessageNamesTheSetting(string key, string value)
    {
        // A target out of range also breaks the two rules that compare other settings with it, and
        // their messages name the target too. Those two are moved out of the way, so only the
        // target's own range can refuse.
        var options = key == "Demo:Pool:TargetFree"
            ? Bound((key, value), ("Demo:Pool:LowMark", "0"), ("Demo:Pool:MaxClaimsPerDay", "10000"))
            : Bound((key, value));

        var result = Validated(options);

        result.Failed.Should().BeTrue("{0}={1} is outside the range the pool was sized for", key, value);
        result.FailureMessage.Should().Contain(key, "an operator is sent to the setting that is wrong");
    }

    [Fact]
    public void ALowMarkAboveTheTarget_IsRefused()
    {
        // The pool can never hold more free copies than its target, so a mark above it would report
        // "low" after every run for ever.
        var result = Validated(Bound(("Demo:Pool:TargetFree", "10"), ("Demo:Pool:LowMark", "11")));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Demo:Pool:LowMark").And.Contain("Demo:Pool:TargetFree");
    }

    [Fact]
    public void ADailyCeilingBelowTheTarget_IsRefused()
    {
        // The top-up builds min(target, ceiling - claims): a ceiling under the target means the
        // target is never reached, even on a day with no claim.
        var result = Validated(Bound(("Demo:Pool:TargetFree", "50"), ("Demo:Pool:MaxClaimsPerDay", "49")));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Demo:Pool:MaxClaimsPerDay").And.Contain("Demo:Pool:TargetFree");
    }

    /// <summary>
    /// CONTROLS: both ends of every range are accepted. They pass against a validator that accepts
    /// everything too, so they say something only beside the refusals above: a range written one
    /// step too narrow fails here.
    /// </summary>
    [Fact]
    public void TheEndsOfEveryRange_AreAccepted()
    {
        var smallest = Bound(
            ("Demo:CopyLifetimeHours", "1"),
            ("Demo:Pool:TargetFree", "1"),
            ("Demo:Pool:LowMark", "0"),
            ("Demo:Pool:MaxFreeAgeHours", "1"),
            ("Demo:Pool:MaxClaimsPerDay", "1"),
            ("Demo:Claim:MaxPerClientPerDay", "1"),
            ("Demo:Copy:MaxWrites", "10"));
        var largest = Bound(
            ("Demo:CopyLifetimeHours", "168"),
            ("Demo:Pool:TargetFree", "500"),
            ("Demo:Pool:LowMark", "500"),
            ("Demo:Pool:MaxFreeAgeHours", "720"),
            ("Demo:Pool:MaxClaimsPerDay", "10000"),
            ("Demo:Claim:MaxPerClientPerDay", "1000"),
            ("Demo:Copy:MaxWrites", "100000"));

        Validated(smallest).Succeeded.Should().BeTrue(because: Validated(smallest).FailureMessage);
        Validated(largest).Succeeded.Should().BeTrue(because: Validated(largest).FailureMessage);
    }

    // ── The Seeder's own root ────────────────────────────────────────────────────────────────────

    /*
      THROUGH THE REAL ROOT, for the reason RealCompositionRootRefusalTests gives: a validator that
      exists and is registered nowhere refuses nothing. The Seeder never starts its host, so it runs
      the start-up validator by hand before any command; these call the same validator on the
      container AddSeederServices builds. No database is opened: registration does not connect.
    */
    private static ServiceProvider SeederRoot(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = CustomWebApplicationFactory.PlaceholderConnectionString,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeederServices(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), environment.Object);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheSeeder_ReadsTheDemoSection()
    {
        using var root = SeederRoot(
            ("Demo:Enabled", "true"),
            ("Demo:CopyLifetimeHours", "12"),
            ("Demo:Pool:TargetFree", "7"),
            ("Demo:Pool:LowMark", "3"),
            ("Demo:Pool:MaxFreeAgeHours", "30"),
            ("Demo:Pool:MaxClaimsPerDay", "70"),
            ("Demo:Claim:MaxPerClientPerDay", "4"));

        var options = root.GetRequiredService<IOptions<DemoOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.CopyLifetimeHours.Should().Be(12);
        options.Pool.TargetFree.Should().Be(7);
        options.Pool.LowMark.Should().Be(3);
        options.Pool.MaxFreeAgeHours.Should().Be(30);
        options.Pool.MaxClaimsPerDay.Should().Be(70);
        options.Claim.MaxPerClientPerDay.Should().Be(4);
    }

    [Theory]
    [InlineData("Demo:Pool:TargetFree", "0")]
    [InlineData("Demo:Pool:LowMark", "51")] // above the default target of 50
    [InlineData("Demo:CopyLifetimeHours", "169")]
    public void TheSeeder_RefusesToRunOnADemoSettingOutOfRange(string key, string value)
    {
        using var root = SeederRoot((key, value));

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(key);
    }

    /// <summary>CONTROL: the same root with nothing about the demo configured starts, as it does today.</summary>
    [Fact]
    public void TheSeeder_WithNoDemoSection_PassesItsStartupValidation()
    {
        using var root = SeederRoot();

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
    }
}
