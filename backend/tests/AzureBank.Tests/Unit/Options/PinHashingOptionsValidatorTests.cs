using AzureBank.Shared.Options;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Configuration;

/// <summary>
/// Unit tests for the shared PIN-pepper keyring validator (ADR-0011). The same
/// validator is registered by the API and the Seeder, so these rules gate both.
/// </summary>
public class PinHashingOptionsValidatorTests
{
    private readonly PinHashingOptionsValidator _sut = new();

    private const string P1 = "valid-pepper-one-0123456789abcdef0123456789";
    private const string P2 = "valid-pepper-two-9876543210fedcba9876543210";
    private const string P3 = "valid-pepper-three-abcdefabcdefabcdefabcdef";

    private bool IsValid(PinHashingOptions o) => _sut.Validate(null, o).Succeeded;

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
