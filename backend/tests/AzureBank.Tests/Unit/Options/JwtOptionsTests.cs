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
/// The JWT signing key is checked at STARTUP. Until 2026-09-25 nothing checked it: measured, both
/// hosts as Production in containers, a 31-byte key started and the first sign-in answered 500.
/// </summary>
/// <remarks>
/// Driven through <see cref="IStartupValidator"/>, which is what <c>ValidateOnStart()</c> registers,
/// so these prove the registration and not only the predicate.
/// </remarks>
public class JwtOptionsTests
{
    private static ServiceProvider Root(string? secret)
    {
        var values = new Dictionary<string, string?>();
        if (secret is not null)
        {
            values["Jwt:Secret"] = secret;
        }

        var services = new ServiceCollection();
        services.AddJwtOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("                                        ")]
    [InlineData("thirty-one-bytes-of-ascii-key!!")]
    public void AKeyTooShortToSignWith_StopsTheHostAtStart(string? secret)
    {
        using var root = Root(secret);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Jwt:Secret must be configured with at least 32 bytes");
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    public void TheRuleCountsBytes_NotCharacters(int characters, bool starts)
    {
        // U+00E9 is two bytes in UTF-8, the encoding both signing sites use: sixteen of them are 32
        // bytes, fifteen are 30. A rule counting characters would refuse both.
        using var root = Root(new string('\u00e9', characters));

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        if (starts)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<OptionsValidationException>();
        }
    }

    [Fact]
    public void AKeyOfThirtyTwoBytes_Starts_AndIsTheKeyBound()
    {
        const string secret = "thirty-two-bytes-of-ascii-key!!!";
        using var root = Root(secret);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
        root.GetRequiredService<IOptions<JwtOptions>>().Value.Secret.Should().Be(secret);
        JwtOptions.MinimumSecretBytes.Should().Be(32);
    }

    [Fact]
    public void ThroughTheRealRoot_AShortKey_IsRefused_ByTheValidatorTheHostRunsAtStart()
    {
        // Every other value the root validates is supplied, so the one failure left is the key's.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["ServiceCredential:BffKey"] = CustomWebApplicationFactory.ServiceCredentialKey,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["ConnectionStrings:DefaultConnection"] = CustomWebApplicationFactory.PlaceholderConnectionString,
            ["Jwt:Secret"] = "thirty-one-bytes-of-ascii-key!!",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);
        using var root = services.BuildServiceProvider();

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Jwt:Secret must be configured");
    }
}
