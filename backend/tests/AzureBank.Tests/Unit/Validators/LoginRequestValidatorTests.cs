using AzureBank.Api.Validators.Auth;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for LoginRequestValidator.
/// </summary>
public class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    #region Valid Request Tests

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var request = new LoginRequest { Email = "test@example.com", Password = "password" };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    #endregion

    #region Email Validation Tests

    [Fact]
    public void Validate_WithEmptyEmail_ShouldHaveError()
    {
        var request = new LoginRequest { Email = "", Password = "password" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_WithInvalidEmailFormat_ShouldHaveError()
    {
        var request = new LoginRequest { Email = "notanemail", Password = "password" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_WithEmailExceedingMaxLength_ShouldHaveError()
    {
        // Create email that exceeds 255 characters: 250 'a' + "@test.com" (9) = 259 chars
        var longEmail = new string('a', ValidationRules.EmailMaxLength - 5) + "@test.com";
        var request = new LoginRequest { Email = longEmail, Password = "password" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    #endregion

    #region Password Validation Tests

    [Fact]
    public void Validate_WithEmptyPassword_ShouldHaveError()
    {
        var request = new LoginRequest { Email = "test@example.com", Password = "" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void Validate_WithNullPassword_ShouldHaveError()
    {
        var request = new LoginRequest { Email = "test@example.com", Password = null! };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    #endregion
}
