using AzureBank.Api.Validators.Account;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Enums;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for CreateAccountRequestValidator and UpdateAccountRequestValidator.
/// </summary>
public class AccountValidatorTests
{
    private readonly CreateAccountRequestValidator _createValidator = new();
    private readonly UpdateAccountRequestValidator _updateValidator = new();

    #region CreateAccountRequestValidator Tests

    [Fact]
    public void CreateAccount_WithValidRequest_ShouldNotHaveErrors()
    {
        var request = new CreateAccountRequest { Name = "My Account", Type = AccountType.Savings };
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void CreateAccount_WithEmptyName_ShouldHaveError()
    {
        var request = new CreateAccountRequest { Name = "", Type = AccountType.Checking };
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void CreateAccount_WithNameTooShort_ShouldHaveError()
    {
        var request = new CreateAccountRequest { Name = "A", Type = AccountType.Checking }; // Min is 2
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void CreateAccount_WithNameTooLong_ShouldHaveError()
    {
        var request = new CreateAccountRequest
        {
            Name = new string('a', ValidationRules.AccountNameMaxLength + 1),
            Type = AccountType.Checking
        };
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void CreateAccount_WithMinNameLength_ShouldNotHaveError()
    {
        var request = new CreateAccountRequest { Name = "AB", Type = AccountType.Checking };
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void CreateAccount_WithMaxNameLength_ShouldNotHaveError()
    {
        var request = new CreateAccountRequest
        {
            Name = new string('a', ValidationRules.AccountNameMaxLength),
            Type = AccountType.Checking
        };
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [Theory]
    [InlineData(AccountType.Checking)]
    [InlineData(AccountType.Savings)]
    [InlineData(AccountType.Investment)]
    public void CreateAccount_WithValidAccountType_ShouldNotHaveError(AccountType type)
    {
        var request = new CreateAccountRequest { Name = "My Account", Type = type };
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Type);
    }

    [Fact]
    public void CreateAccount_WithInvalidAccountType_ShouldHaveError()
    {
        var request = new CreateAccountRequest { Name = "My Account", Type = (AccountType)999 };
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Type);
    }

    #endregion

    #region UpdateAccountRequestValidator Tests

    [Fact]
    public void UpdateAccount_WithValidRequest_ShouldNotHaveErrors()
    {
        var request = new UpdateAccountRequest { Name = "Updated Account" };
        var result = _updateValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateAccount_WithEmptyName_ShouldHaveError()
    {
        var request = new UpdateAccountRequest { Name = "" };
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void UpdateAccount_WithNameTooShort_ShouldHaveError()
    {
        var request = new UpdateAccountRequest { Name = "A" };
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void UpdateAccount_WithNameTooLong_ShouldHaveError()
    {
        var request = new UpdateAccountRequest
        {
            Name = new string('a', ValidationRules.AccountNameMaxLength + 1)
        };
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    #endregion
}
