using AzureBank.Api.Validators.Transfer;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for InternalTransferRequestValidator (transfers between own accounts).
/// </summary>
public class InternalTransferRequestValidatorTests
{
    private readonly InternalTransferRequestValidator _validator = new();

    private static InternalTransferRequest CreateValidRequest()
    {
        var fromId = Guid.NewGuid();
        return new InternalTransferRequest
        {
            FromAccountId = fromId,
            ToAccountId = Guid.NewGuid(), // Different from FromAccountId
            Amount = 100m,
            Description = "Test internal transfer"
        };
    }

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

    #region ToAccountId Validation Tests

    [Fact]
    public void Validate_WithEmptyToAccountId_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.ToAccountId = Guid.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ToAccountId);
    }

    [Fact]
    public void Validate_WithSameSourceAndDestination_ShouldHaveError()
    {
        var sameId = Guid.NewGuid();
        var request = new InternalTransferRequest
        {
            FromAccountId = sameId,
            ToAccountId = sameId, // Same as source
            Amount = 100m
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ToAccountId)
            .WithErrorMessage("Cannot transfer to the same account.");
    }

    #endregion

    #region Amount Validation Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
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

    [Fact]
    public void Validate_WithMinAmount_ShouldNotHaveError()
    {
        var request = CreateValidRequest();
        request.Amount = ValidationRules.TransactionMinAmount;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithMaxAmount_ShouldNotHaveError()
    {
        var request = CreateValidRequest();
        request.Amount = ValidationRules.TransactionMaxAmount;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
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
    public void Validate_WithEmptyDescription_ShouldNotHaveError()
    {
        var request = CreateValidRequest();
        request.Description = "";
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
