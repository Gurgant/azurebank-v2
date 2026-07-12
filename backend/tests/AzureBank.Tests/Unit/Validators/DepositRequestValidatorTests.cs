using AzureBank.Api.Validators.Transaction;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for DepositRequestValidator.
/// Tests all validation rules for deposit requests.
/// </summary>
public class DepositRequestValidatorTests
{
    private readonly DepositRequestValidator _validator = new();

    #region Valid Request Tests

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = 100m,
            Description = "Test deposit"
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithMinimumValidAmount_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = ValidationRules.TransactionMinAmount // 0.01m
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithMaximumValidAmount_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = ValidationRules.TransactionMaxAmount // 100_000m
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithNullDescription_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = 100m,
            Description = null
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Validate_WithEmptyDescription_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = 100m,
            Description = string.Empty
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    #endregion

    #region AccountId Validation Tests

    [Fact]
    public void Validate_WithEmptyAccountId_ShouldHaveError()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.Empty,
            Amount = 100m
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.AccountId);
    }

    #endregion

    #region Amount Validation Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(-0.01)]
    public void Validate_WithAmountLessThanMinimum_ShouldHaveError(decimal amount)
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = amount
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithAmountExceedingMaximum_ShouldHaveError()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = ValidationRules.TransactionMaxAmount + 0.01m // 100_000.01m
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData(100_001)]
    [InlineData(200_000)]
    [InlineData(1_000_000)]
    public void Validate_WithLargeAmountsExceedingMaximum_ShouldHaveError(decimal amount)
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = amount
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    #endregion

    #region Description Validation Tests

    [Fact]
    public void Validate_WithDescriptionAtMaxLength_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = 100m,
            Description = new string('a', ValidationRules.TransactionDescriptionMaxLength) // 500 chars
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Validate_WithDescriptionExceedingMaxLength_ShouldHaveError()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.NewGuid(),
            Amount = 100m,
            Description = new string('a', ValidationRules.TransactionDescriptionMaxLength + 1) // 501 chars
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    #endregion

    #region Multiple Errors Tests

    [Fact]
    public void Validate_WithMultipleInvalidFields_ShouldHaveMultipleErrors()
    {
        // Arrange
        var request = new DepositRequest
        {
            AccountId = Guid.Empty,
            Amount = 0,
            Description = new string('a', 501)
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.AccountId);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    #endregion
}
