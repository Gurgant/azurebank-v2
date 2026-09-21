using AzureBank.Api.Validators.Transaction;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for WithdrawRequestValidator.
/// </summary>
/// <remarks>
/// The PIN tests are NOT missing: they moved to
/// <c>WithdrawalAuthorizationRequestValidatorTests</c> with the field itself
/// (ADR-0056). A withdrawal no longer carries a PIN; the mint proves it.
/// </remarks>
public class WithdrawRequestValidatorTests
{
    private readonly WithdrawRequestValidator _validator = new();

    private static WithdrawRequest CreateValidRequest() => new()
    {
        AccountId = Guid.NewGuid(),
        Amount = 100m,
        Description = "Test withdrawal"
    };

    #region Valid Request Tests

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var result = _validator.TestValidate(CreateValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    #endregion

    #region AccountId Validation Tests

    [Fact]
    public void Validate_WithEmptyAccountId_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.AccountId = Guid.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AccountId);
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
