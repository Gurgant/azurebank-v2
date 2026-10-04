using System.Globalization;
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
    private static ServiceProvider BoundRoot(bool api, string previousKey) =>
        BoundRoot(api, (previousKey, P1), ("4", P3));

    // The same two roots over the previous-pepper entries exactly as a test writes them: each path
    // goes under Security:PreviousPinPeppers as it is, so a test can give one id two keys, put a
    // section where the pepper belongs, or write an entry the wrong way round. A null path is the
    // section itself. The active pepper is P2 under key id 2.
    private static ServiceProvider BoundRoot(bool api, params (string? Path, string? Value)[] previous)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Security:PinPepper"] = P2,
            ["Security:PinPepperKeyId"] = "2",
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["ServiceCredential:BffKey"] = CustomWebApplicationFactory.ServiceCredentialKey,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["Jwt:Secret"] = CustomWebApplicationFactory.JwtSecret,
            ["ConnectionStrings:DefaultConnection"] = CustomWebApplicationFactory.PlaceholderConnectionString,
        };
        foreach (var (path, value) in previous)
        {
            settings.Add(
                path is null ? "Security:PreviousPinPeppers" : $"Security:PreviousPinPeppers:{path}", value);
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
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

    // Each spelling is a valid key alone (the rows above). Together they are two configuration
    // keys and one id: the binder keeps one pepper and the other is gone. The sentence puts the
    // two keys in ordinal order itself, whichever the configuration lists first.
    [Theory]
    [InlineData(false, "1", "01", "'01' and '1'")]
    [InlineData(false, "01", "1", "'01' and '1'")]
    [InlineData(false, "1", "+1", "'+1' and '1'")]
    [InlineData(false, "+1", "1", "'+1' and '1'")]
    [InlineData(true, "1", "01", "'01' and '1'")]
    [InlineData(true, "01", "1", "'01' and '1'")]
    [InlineData(true, "1", "+1", "'+1' and '1'")]
    [InlineData(true, "+1", "1", "'+1' and '1'")]
    public void TwoPreviousPepperKeysForOneId_AreRefusedAtStart_NamingBoth_WithoutAValue(
        bool api, string first, string second, string named)
    {
        using var root = BoundRoot(api, (first, P1), (second, P3));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal(
            $"Security:PreviousPinPeppers keys {named} name the same id; only one pepper can be held under it.");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APreviousPepperKeyThatHoldsASection_IsRefusedAtStart_WithoutItsValue(bool api)
    {
        // Security__PreviousPinPeppers__1__Value, or "1": { "Value": "..." } in a file: the key is
        // a whole number, and the binder reads no pepper under it.
        using var root = BoundRoot(api, ("1:Value", P1), ("4", P3));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal(
            "Security:PreviousPinPeppers key '1' was not read: it must be a whole number >= 1 that holds one value.");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APreviousPepperKeyThatHoldsAValueAndASection_IsRefusedAtStart_WithoutEitherValue(bool api)
    {
        // Security__PreviousPinPeppers__1 beside Security__PreviousPinPeppers__1__Value: the binder
        // reads the value on the key itself and leaves the one under it out, so the ring holds id 1
        // and a pepper is still lost without a word.
        using var root = BoundRoot(api, ("1", P1), ("1:Value", P3));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal(
            "Security:PreviousPinPeppers key '1' must hold exactly one value.");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APreviousPepperEntryWrittenTheWrongWayRound_IsRefusedAtStart_WithoutPrintingItsKey(bool api)
    {
        // "<pepper>": "1" where "1": "<pepper>" was meant. Here the key is the secret, so the
        // refusal gives its length and not its text.
        using var root = BoundRoot(api, (P1, "1"), ("4", P3));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal(
            "Security:PreviousPinPeppers key of 43 characters must be a whole number >= 1.");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APepperWithAColonWrittenAsTheKey_IsNamedByItsLength_NotByItsFirstPart(bool api)
    {
        // A key is one segment of a path. The pepper written as the key and holding a colon reaches
        // the validator as a short key with a section under it: quoted, it would print the secret's
        // first part.
        var firstPart = P1[..20];
        using var root = BoundRoot(api, ($"{firstPart}:{P1[20..]}", "1"), ("4", P3));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal(
            "Security:PreviousPinPeppers key of 20 characters must be a whole number >= 1.");
        refusal.ToString().Should().NotContain(firstPart).And.NotContain(P2).And.NotContain(P3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APepperWrittenOnTheSectionItself_IsRefusedAtStart_WithoutItsValue(bool api)
    {
        // Security__PreviousPinPeppers=<pepper>, with no key id under it: the section has no key to
        // judge, and the binder leaves the ring empty.
        using var root = BoundRoot(api, (null, P1));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal(
            "Security:PreviousPinPeppers holds a value of its own: each previous pepper goes under its key id.");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpacesAloneOnTheSectionItself_AreRefusedAtStart_LikeAPepperThere(bool api)
    {
        // Not a pepper, and not nothing either: somebody wrote it, and no id was given.
        using var root = BoundRoot(api, (null, "   "));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>().Which.Failures.Should().Equal(
            "Security:PreviousPinPeppers holds a value of its own: each previous pepper goes under its key id.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEmptyValueOnTheSection_IsNoPepper_AndTheHostStarts(bool api)
    {
        // CONTROL: green before this change. A variable set to nothing means no previous pepper.
        using var root = BoundRoot(api, (null, ""));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().NotThrow();
    }

    // The length from which a key could be a pepper is the length a pepper must have: one
    // character below it the key is still quoted, as every key in the rows above is.
    [Theory]
    [InlineData(false, 31, "key 'kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk'")]
    [InlineData(false, 32, "key of 32 characters")]
    [InlineData(true, 31, "key 'kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk'")]
    [InlineData(true, 32, "key of 32 characters")]
    public void AnUnreadableKey_IsQuotedBelowAPeppersLength_AndNamedByItsLengthFromThere(
        bool api, int length, string shown)
    {
        using var root = BoundRoot(api, (new string('k', length), P1), ("4", P3));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal($"Security:PreviousPinPeppers {shown} must be a whole number >= 1.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AControlCharacterInAPreviousPepperKey_IsNotPrintedRaw(bool api)
    {
        // A line feed printed as it is would break the one line a refusal is in the Seeder's log.
        using var root = BoundRoot(api, ("1\n", P1), ("4", P3));
        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        var refusal = start.Should().Throw<OptionsValidationException>().Which;
        refusal.Failures.Should().Equal("Security:PreviousPinPeppers key '1?' must be a whole number >= 1.");
        refusal.Message.Should().NotContain("\n");
        refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
    }

    // The binder converts a key with the host's culture, the validator reads it with the invariant
    // one, and the rows above run under whatever culture the machine has. Here the culture is
    // built, not looked up, so the rows do not depend on the machine's data: one that writes the
    // signs with a left-to-right mark before them (as he-IL does) cannot read "+1" or "-1", and one
    // whose minus is U+2212 (as sv-SE's is) reads "-1" and would print the id back with that sign.
    [Theory]
    [InlineData(false, "\u200e+", "\u200e-", "+1", false)]
    [InlineData(false, "\u200e+", "\u200e-", "-1", false)]
    [InlineData(false, "+", "\u2212", "-1", true)]
    [InlineData(true, "\u200e+", "\u200e-", "+1", false)]
    [InlineData(true, "\u200e+", "\u200e-", "-1", false)]
    [InlineData(true, "+", "\u2212", "-1", true)]
    public void ASignedKey_UnderACultureThatWritesSignsItsOwnWay_IsRefusedAtStart_InPlainAscii(
        bool api, string positiveSign, string negativeSign, string key, bool theBinderReadsIt)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = positiveSign;
        culture.NumberFormat.NegativeSign = negativeSign;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            using var root = BoundRoot(api, key);
            var start = () => root.GetRequiredService<IStartupValidator>().Validate();

            var rule = theBinderReadsIt
                ? "must be >= 1."
                : "was not read: it must be a whole number >= 1 that holds one value.";
            var refusal = start.Should().Throw<OptionsValidationException>().Which;
            refusal.Failures.Should().Equal($"Security:PreviousPinPeppers key '{key}' {rule}");
            refusal.ToString().Should().NotContain(P1).And.NotContain(P2).And.NotContain(P3);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
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
