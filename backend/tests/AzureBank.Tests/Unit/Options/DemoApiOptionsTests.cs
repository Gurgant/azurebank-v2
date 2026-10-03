using AzureBank.Api.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// The folder's existing namespace: a namespace literally named "Options" under Unit would shadow
// `Options.Create(...)` in every sibling test file.
namespace AzureBank.Tests.Unit.Configuration;

/// <summary>
/// The API reads the demo's settings and checks them at STARTUP: every range whether the demo is
/// on or off, and, with the demo on, the secret a client's address is hashed with.
/// </summary>
/// <remarks>
/// <para>
/// Through the real composition root, <c>AddApplicationServices</c>, and
/// <see cref="IStartupValidator"/>, which is what <c>ValidateOnStart()</c> registers and what the
/// host runs before it listens (the <c>ServiceCredentialTests</c> idiom). So these prove that the
/// API binds the section and registers both rules, and not only that the rules exist: through the
/// host a refusal arrives as a disposed provider, and its message cannot be read.
/// </para>
/// <para>
/// THE SECRET'S RULE IS THE API'S OWN. <see cref="DemoOptionsValidator"/> has none: the Seeder
/// runs that validator too and holds no secret, so a rule there would stop <c>seed-pool</c> and
/// <c>recycle</c>. <c>DemoOptionsTests</c> holds the ranges themselves.
/// </para>
/// </remarks>
public class DemoApiOptionsTests
{
    /// <summary>32 characters, the fewest the rule accepts. Test-only value, NOT a real secret.</summary>
    private const string SecretOf32Characters = "test-only-demo-client-key-32-ch!";

    /// <summary>The real root, with every other secret it validates at start, and these settings.</summary>
    private static ServiceProvider RealRoot(params (string Key, string? Value)[] demo)
    {
        // Every secret the root validates at start, so the ONE failure left is the demo's:
        // IStartupValidator aggregates, and an aggregate hides which rule a test is about.
        var values = new Dictionary<string, string?>
        {
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["ServiceCredential:BffKey"] = CustomWebApplicationFactory.ServiceCredentialKey,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["Jwt:Secret"] = CustomWebApplicationFactory.JwtSecret,
            ["ConnectionStrings:DefaultConnection"] = CustomWebApplicationFactory.PlaceholderConnectionString,
        };
        foreach (var (key, value) in demo)
        {
            values[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("                                ")] // 32 spaces: long enough, and no secret
    [InlineData("test-only-demo-client-key-31-ch")] // 31 characters
    public void WithTheDemoOn_AndNoUsableClientKeySecret_TheApiRefusesToStart(string? secret)
    {
        (secret?.Length ?? 0).Should().BeOneOf([0, 31, 32], "ARRANGE: the rows are none, too short, and blank");
        using var root = RealRoot(("Demo:Enabled", "true"), ("Demo:ClientKeySecret", secret));

        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Demo:ClientKeySecret must be configured with at least 32 characters");
    }

    [Fact]
    public void WithTheDemoOn_AndASecretOf32Characters_TheApiStarts()
    {
        SecretOf32Characters.Length.Should().Be(32, "ARRANGE: one character more than the row that is refused");
        using var root = RealRoot(("Demo:Enabled", "true"), ("Demo:ClientKeySecret", SecretOf32Characters));

        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().NotThrow();
        var demo = root.GetRequiredService<IOptions<DemoOptions>>().Value;
        demo.Enabled.Should().BeTrue("the API binds the section, so the flag a deployment sets reaches it");
        demo.ClientKeySecret.Should().Be(SecretOf32Characters);
    }

    // CONTROL: green before this change. A deployment that never mentions the demo starts with no
    // secret, before the API reads the section and after.
    [Fact]
    public void WithTheDemoOff_TheApiStartsWithoutASecret()
    {
        using var root = RealRoot();

        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().NotThrow();
        var demo = root.GetRequiredService<IOptions<DemoOptions>>().Value;
        demo.Enabled.Should().BeFalse();
        demo.ClientKeySecret.Should().BeNull();
    }

    [Fact]
    public void ADemoValueOutOfRange_StopsTheApi_EvenWithTheDemoOff()
    {
        // 9 is one below the floor of Demo:Copy:MaxWrites. The demo is off and the API would never
        // read the value: it is refused on the day it is written, not on the day the demo is
        // turned on.
        using var root = RealRoot(("Demo:Copy:MaxWrites", "9"));

        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Demo:Copy:MaxWrites must be between 10 and 100000, and is 9.");
    }
}
