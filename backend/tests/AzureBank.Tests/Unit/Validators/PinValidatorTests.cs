using AzureBank.Api.Validators.Auth;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for SetPinRequestValidator and VerifyPinRequestValidator.
/// </summary>
public class PinValidatorTests
{
    private readonly SetPinRequestValidator _setPinValidator = new();
    private readonly VerifyPinRequestValidator _verifyPinValidator = new();

    #region SetPinRequestValidator Tests

    [Fact]
    public void SetPin_WithValidPin_ShouldNotHaveErrors()
    {
        var request = new SetPinRequest { Pin = "123456" };
        var result = _setPinValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void SetPin_WithEmptyPin_ShouldHaveError()
    {
        var request = new SetPinRequest { Pin = "" };
        var result = _setPinValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }

    [Theory]
    [InlineData("12345")]    // Too short (5 digits)
    [InlineData("1234567")]  // Too long (7 digits)
    [InlineData("12")]       // Way too short
    public void SetPin_WithWrongLength_ShouldHaveError(string pin)
    {
        var request = new SetPinRequest { Pin = pin };
        var result = _setPinValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }

    [Theory]
    [InlineData("12345a")]   // Contains letter
    [InlineData("1234 5")]   // Contains space
    [InlineData("12-456")]   // Contains hyphen
    [InlineData("abcdef")]   // All letters
    public void SetPin_WithNonDigits_ShouldHaveError(string pin)
    {
        var request = new SetPinRequest { Pin = pin };
        var result = _setPinValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("123456")]
    [InlineData("999999")]
    public void SetPin_WithValidPins_ShouldNotHaveError(string pin)
    {
        var request = new SetPinRequest { Pin = pin };
        var result = _setPinValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Pin);
    }

    #endregion

    #region VerifyPinRequestValidator Tests

    [Fact]
    public void VerifyPin_WithValidPin_ShouldNotHaveErrors()
    {
        var request = new VerifyPinRequest { Pin = "123456" };
        var result = _verifyPinValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void VerifyPin_WithEmptyPin_ShouldHaveError()
    {
        var request = new VerifyPinRequest { Pin = "" };
        var result = _verifyPinValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }

    [Fact]
    public void VerifyPin_WithInvalidPattern_ShouldHaveError()
    {
        var request = new VerifyPinRequest { Pin = "12345a" };
        var result = _verifyPinValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }

    #endregion
}
