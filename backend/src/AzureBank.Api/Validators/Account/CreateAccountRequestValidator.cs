using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Enums;
using FluentValidation;

namespace AzureBank.Api.Validators.Account;

/// <summary>
/// FluentValidation validator for CreateAccountRequest.
/// </summary>
public class CreateAccountRequestValidator : AbstractValidator<CreateAccountRequest>
{
    public CreateAccountRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Account name is required.")
            .Length(ValidationRules.AccountNameMinLength, ValidationRules.AccountNameMaxLength)
            .WithMessage(ValidationRules.AccountNameLengthMessage);

        RuleFor(x => x.Type)
            .IsInEnum().WithMessage("Invalid account type. Must be Checking, Savings, or Investment.");
    }
}
