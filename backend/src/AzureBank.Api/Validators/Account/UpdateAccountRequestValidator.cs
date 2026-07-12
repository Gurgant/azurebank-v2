using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using FluentValidation;

namespace AzureBank.Api.Validators.Account;

/// <summary>
/// FluentValidation validator for UpdateAccountRequest.
/// </summary>
public class UpdateAccountRequestValidator : AbstractValidator<UpdateAccountRequest>
{
    public UpdateAccountRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Account name is required.")
            .Length(ValidationRules.AccountNameMinLength, ValidationRules.AccountNameMaxLength)
            .WithMessage(ValidationRules.AccountNameLengthMessage);
    }
}
