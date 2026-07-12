using AzureBank.Api.Validators.Auth;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for RegisterRequestValidator.
/// </summary>
public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _validator = new();

    private static RegisterRequest CreateValidRequest() => new()
    {
        AzureTag = "testuser",
        Email = "test@example.com",
        Password = "Password123!",
        FirstName = "John",
        LastName = "Doe"
    };

    #region Valid Request Tests

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var result = _validator.TestValidate(CreateValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    #endregion

    #region AzureTag Validation Tests

    [Fact]
    public void Validate_WithEmptyAzureTag_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.AzureTag = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AzureTag);
    }

    [Theory]
    [InlineData("ab")]  // Too short (min 3)
    [InlineData("a")]
    public void Validate_WithAzureTagTooShort_ShouldHaveError(string tag)
    {
        var request = CreateValidRequest();
        request.AzureTag = tag;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AzureTag);
    }

    [Fact]
    public void Validate_WithAzureTagTooLong_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.AzureTag = new string('a', ValidationRules.AzureTagMaxLength + 1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AzureTag);
    }

    [Theory]
    [InlineData("1user")]    // Starts with number
    [InlineData("_user")]    // Starts with underscore
    [InlineData("User")]     // Contains uppercase
    [InlineData("user name")] // Contains space
    [InlineData("user@tag")] // Contains special char
    public void Validate_WithInvalidAzureTagPattern_ShouldHaveError(string tag)
    {
        var request = CreateValidRequest();
        request.AzureTag = tag;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AzureTag);
    }

    [Theory]
    [InlineData("abc")]           // Min length
    [InlineData("user123")]       // With numbers
    [InlineData("user_name")]     // With underscore
    [InlineData("abcdefghijklmnopqrst")] // Max length (20)
    public void Validate_WithValidAzureTag_ShouldNotHaveError(string tag)
    {
        var request = CreateValidRequest();
        request.AzureTag = tag;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.AzureTag);
    }

    #endregion

    #region Email Validation Tests

    [Fact]
    public void Validate_WithInvalidEmail_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.Email = "notanemail";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    #endregion

    #region Password Validation Tests

    [Fact]
    public void Validate_WithPasswordTooShort_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.Password = "Pass1!"; // 6 chars, min is 8
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Theory]
    [InlineData("password123!")]  // No uppercase
    [InlineData("PASSWORD123!")]  // No lowercase
    [InlineData("Password!!!")]   // No digit
    [InlineData("Password123")]   // No special char
    public void Validate_WithPasswordMissingRequirements_ShouldHaveError(string password)
    {
        var request = CreateValidRequest();
        request.Password = password;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Theory]
    [InlineData("Password1!")]
    [InlineData("MyP@ssw0rd")]
    [InlineData("Abcd1234!@#$")]
    public void Validate_WithValidPassword_ShouldNotHaveError(string password)
    {
        var request = CreateValidRequest();
        request.Password = password;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Password);
    }

    #endregion

    #region Name Validation Tests

    [Fact]
    public void Validate_WithFirstNameTooShort_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.FirstName = "A"; // Min is 2
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.FirstName);
    }

    [Fact]
    public void Validate_WithLastNameTooLong_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.LastName = new string('a', ValidationRules.LastNameMaxLength + 1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.LastName);
    }

    [Theory]
    [InlineData("John123")]   // Contains numbers
    [InlineData("John@Doe")]  // Contains special char (not allowed)
    public void Validate_WithInvalidNamePattern_ShouldHaveError(string name)
    {
        var request = CreateValidRequest();
        request.FirstName = name;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.FirstName);
    }

    [Theory]
    [InlineData("Mary-Jane")]     // Hyphenated
    [InlineData("O'Connor")]      // With apostrophe
    [InlineData("José")]          // Accented
    [InlineData("Anna Maria")]    // With space
    public void Validate_WithValidNamePatterns_ShouldNotHaveError(string name)
    {
        var request = CreateValidRequest();
        request.FirstName = name;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.FirstName);
    }

    #endregion
}
