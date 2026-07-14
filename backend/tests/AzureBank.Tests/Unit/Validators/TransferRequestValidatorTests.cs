using AzureBank.Api.Validators.Transfer;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for TransferRequestValidator (external transfers).
/// </summary>
public class TransferRequestValidatorTests
{
    private readonly TransferRequestValidator _validator = new();

    private static TransferRequest CreateValidRequest() => new()
    {
        FromAccountId = Guid.NewGuid(),
        RecipientAzureTag = "recipient",
        Amount = 100m,
        Description = "Test transfer"
    };

    #region Valid Request Tests

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var result = _validator.TestValidate(CreateValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    #endregion

    #region FromAccountId Validation Tests

    [Fact]
    public void Validate_WithEmptyFromAccountId_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.FromAccountId = Guid.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.FromAccountId);
    }

    #endregion

    #region RecipientAzureTag Validation Tests

    [Fact]
    public void Validate_WithEmptyRecipientAzureTag_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.RecipientAzureTag = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.RecipientAzureTag);
    }

    [Theory]
    [InlineData("ab")]       // Too short
    [InlineData("1user")]    // Starts with number
    [InlineData("USER")]     // Uppercase not allowed
    [InlineData("user@tag")] // Special char
    public void Validate_WithInvalidRecipientAzureTag_ShouldHaveError(string tag)
    {
        var request = CreateValidRequest();
        request.RecipientAzureTag = tag;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.RecipientAzureTag);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("user123")]
    [InlineData("user_name")]
    public void Validate_WithValidRecipientAzureTag_ShouldNotHaveError(string tag)
    {
        var request = CreateValidRequest();
        request.RecipientAzureTag = tag;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.RecipientAzureTag);
    }

    #endregion

    #region Amount Validation Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithInvalidAmount_ShouldHaveError(decimal amount)
    {
        var request = CreateValidRequest();
        request.Amount = amount;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithAmountExceedingMax_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.Amount = ValidationRules.TransactionMaxAmount + 1;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    #endregion

    #region Description Validation Tests

    [Fact]
    public void Validate_WithNullDescription_ShouldNotHaveError()
    {
        var request = CreateValidRequest();
        request.Description = null;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Validate_WithDescriptionTooLong_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.Description = new string('a', ValidationRules.TransactionDescriptionMaxLength + 1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    #endregion

    #region Amount Scale Tests

    [Theory]
    [InlineData(10.12345)]
    [InlineData(0.011)]
    public void Validate_WithMoreThanTwoDecimalPlaces_ShouldHaveError(decimal amount)
    {
        var request = CreateValidRequest();
        request.Amount = amount;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData(10.12)]
    [InlineData(0.01)]
    public void Validate_WithAtMostTwoDecimalPlaces_ShouldNotHaveError(decimal amount)
    {
        var request = CreateValidRequest();
        request.Amount = amount;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    #endregion
}
