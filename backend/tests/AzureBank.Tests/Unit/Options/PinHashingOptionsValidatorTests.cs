using AzureBank.Api.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Configuration;

/// <summary>
/// Unit tests for the shared PIN-pepper keyring validator (ADR-0011). The same
/// validator is registered by the API and the Seeder, so these rules gate both.
/// </summary>
public class PinHashingOptionsValidatorTests
{
    private readonly PinHashingOptionsValidator _sut = new(new ConfigurationBuilder().Build());

    private const string P1 = "valid-pepper-one-0123456789abcdef0123456789";
    private const string P2 = "valid-pepper-two-9876543210fedcba9876543210";
    private const string P3 = "valid-pepper-three-abcdefabcdefabcdefabcdef";

    private bool IsValid(PinHashingOptions o) => _sut.Validate(null, o).Succeeded;

    // Both the shared validator after binding and the API's real startup registrations. Reading
    // only a hand-built Dictionary<int, string> cannot expose a configuration key the binder lost.
    private static ServiceProvider BoundRoot(bool api, string previousKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:PinPepper"] = P2,
            ["Security:PinPepperKeyId"] = "2",
            [$"Security:PreviousPinPeppers:{previousKey}"] = P1,
            ["Security:PreviousPinPeppers:4"] = P3,
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["ServiceCredential:BffKey"] = CustomWebApplicationFactory.ServiceCredentialKey,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["Jwt:Secret"] = CustomWebApplicationFactory.JwtSecret,
            ["ConnectionStrings:DefaultConnection"] = CustomWebApplicationFactory.PlaceholderConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        if (api)
        {
            services.AddApplicationServices(configuration);
        }
        else
        {
            services.AddOptions<PinHashingOptions>()
                .Bind(configuration.GetSection(PinHashingOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<PinHashingOptions>, PinHashingOptionsValidator>();
        }

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false, "v1")]
    [InlineData(false, "one")]
    [InlineData(false, "1 ")]
    [InlineData(false, " 1")]
    [InlineData(false, "1.5")]
    [InlineData(false, "2147483648")]
    [InlineData(true, "v1")]
    [InlineData(true, "one")]
    [InlineData(true, "1 ")]
    [InlineData(true, " 1")]
    [InlineData(true, "1.5")]
    [InlineData(true, "2147483648")]
    public void AnUnreadablePreviousPepperKey_IsRefusedAtStart_WithoutItsValue(bool api, string key)
    {
        using var root = BoundRoot(api, key);
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal($"Security:PreviousPinPeppers key '{key}' must be a whole number >= 1.");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    [Theory]
    [InlineData(false, "1", 1)]
    [InlineData(false, "01", 1)]
    [InlineData(false, "+1", 1)]
    [InlineData(false, "2147483647", int.MaxValue)]
    [InlineData(true, "1", 1)]
    [InlineData(true, "01", 1)]
    [InlineData(true, "+1", 1)]
    [InlineData(true, "2147483647", int.MaxValue)]
    public void AValidPreviousPepperKey_StillStarts_AndKeepsItsPepper(bool api, string key, int id)
    {
        using var root = BoundRoot(api, key);
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().NotThrow();
        root.GetRequiredService<IOptions<PinHashingOptions>>().Value.PreviousPinPeppers
            .Should().BeEquivalentTo(new Dictionary<int, string> { [id] = P1, [4] = P3 });
    }

    [Theory]
    [InlineData(false, "0")]
    [InlineData(false, "-1")]
    [InlineData(true, "0")]
    [InlineData(true, "-1")]
    public void ANonPositivePreviousPepperKey_KeepsTheExistingRefusal(bool api, string key)
    {
        using var root = BoundRoot(api, key);
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal($"Security:PreviousPinPeppers key '{key}' must be >= 1.");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    [Fact]
    public void SingleActivePepper_IsValid()
    {
        IsValid(new PinHashingOptions { PinPepper = P1, PinPepperKeyId = 1 }).Should().BeTrue();
    }

    [Fact]
    public void FullKeyring_IsValid()
    {
        IsValid(new PinHashingOptions
        {
            PinPepper = P2,
            PinPepperKeyId = 2,
            PreviousPinPeppers = new() { [1] = P1 },
        }).Should().BeTrue();
    }

    [Fact]
    public void ShortActivePepper_Fails()
    {
        IsValid(new PinHashingOptions { PinPepper = "too-short", PinPepperKeyId = 1 }).Should().BeFalse();
    }

    [Fact]
    public void EmptyActivePepper_Fails()
    {
        IsValid(new PinHashingOptions { PinPepper = "", PinPepperKeyId = 1 }).Should().BeFalse();
    }

    [Fact]
    public void NonPositiveActiveKeyId_Fails()
    {
        IsValid(new PinHashingOptions { PinPepper = P1, PinPepperKeyId = 0 }).Should().BeFalse();
    }

    [Fact]
    public void ShortPreviousPepper_Fails()
    {
        IsValid(new PinHashingOptions
        {
            PinPepper = P2,
            PinPepperKeyId = 2,
            PreviousPinPeppers = new() { [1] = "too-short" },
        }).Should().BeFalse();
    }

    [Fact]
    public void PreviousContainingActiveKeyId_Fails()
    {
        IsValid(new PinHashingOptions
        {
            PinPepper = P2,
            PinPepperKeyId = 2,
            PreviousPinPeppers = new() { [2] = P3 }, // collides with the active key id
        }).Should().BeFalse();
    }

    [Fact]
    public void NullPreviousPinPeppers_Fails()
    {
        IsValid(new PinHashingOptions { PinPepper = P1, PinPepperKeyId = 1, PreviousPinPeppers = null! })
            .Should().BeFalse();
    }

    [Fact]
    public void DuplicatePepperValue_Fails()
    {
        IsValid(new PinHashingOptions
        {
            PinPepper = P1,
            PinPepperKeyId = 2,
            PreviousPinPeppers = new() { [1] = P1 }, // same secret reused → no-op "rotation"
        }).Should().BeFalse();
    }
}
