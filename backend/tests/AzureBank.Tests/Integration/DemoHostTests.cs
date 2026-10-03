using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The hosts a test of the public demo runs on: the API and the BFF start with the demo off, and
/// with it on only when the test asks.
/// </summary>
/// <remarks>
/// Each reads the flag back from the started host's own <c>IOptions&lt;DemoOptions&gt;</c>, so it
/// is the value the host's code will read, having gone through binding and the checks at start.
/// InMemory database: nothing here opens it.
/// </remarks>
public sealed class DemoHostTests
{
    private static DemoOptions DemoOf(IServiceProvider host) =>
        host.GetRequiredService<IOptions<DemoOptions>>().Value;

    // CONTROL: green before this change. Run with Demo__Enabled=true in the environment, a host
    // no test asked the demo of has it on and no secret, and does not start: this is the test
    // that then fails by name.
    [Fact]
    public void TheDefaultHost_HasTheDemoOff()
    {
        using var factory = new CustomWebApplicationFactory();

        var demo = DemoOf(factory.Services);

        demo.Enabled.Should().BeFalse("no test asked for the demo, and no committed settings file sets it");
        demo.ClientKeySecret.Should().BeNull();
    }

    [Fact]
    public void AHostWithTheDemoEnabled_StartsWithTheFlagAndItsSecret()
    {
        using var factory = new CustomWebApplicationFactory();
        factory.EnableDemo();

        var demo = DemoOf(factory.Services);

        demo.Enabled.Should().BeTrue();
        demo.ClientKeySecret.Should().Be(CustomWebApplicationFactory.DemoClientKeySecret);
        demo.Copy.MaxWrites.Should().Be(200, "a setting the test did not give keeps its default");
    }

    [Fact]
    public void EnableDemo_CarriesTheSettingsItIsGiven()
    {
        using var factory = new CustomWebApplicationFactory();
        factory.EnableDemo(("Demo:Copy:MaxWrites", "10"), ("Demo:Claim:MaxPerClientPerDay", "3"));

        var demo = DemoOf(factory.Services);

        demo.Enabled.Should().BeTrue();
        demo.Copy.MaxWrites.Should().Be(10);
        demo.Claim.MaxPerClientPerDay.Should().Be(3);
    }

    [Fact]
    public void ThroughBffOverApiFactory_TheBffHasTheDemoOn_OnlyWhenAsked()
    {
        using var api = new CustomWebApplicationFactory();
        using var plainBff = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);

        using var demoApi = new CustomWebApplicationFactory();
        demoApi.EnableDemo();
        using var demoBff = new BffOverApiFactory(demoApi, CustomWebApplicationFactory.ServiceCredentialKey);
        demoBff.EnableDemo();

        DemoOf(plainBff.Services).Enabled.Should().BeFalse("this BFF was not asked");
        DemoOf(demoBff.Services).Enabled.Should().BeTrue();
        DemoOf(demoBff.Services).ClientKeySecret.Should().BeNull(
            "the BFF hashes no address: only the API holds the secret");
    }
}
