using AzureBank.Api.Validation;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentValidation;

namespace AzureBank.Api.Validators.Transaction;

/// <summary>
/// FluentValidation validator for DepositRequest.
/// </summary>
public class DepositRequestValidator : AbstractValidator<DepositRequest>
{
    public DepositRequestValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage(ValidationRules.AccountNotEmptyGuid);

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(ValidationRules.TransactionMinAmount)
            .WithMessage($"Amount must be at least {ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)}.")
            .LessThanOrEqualTo(ValidationRules.TransactionMaxAmount)
            .WithMessage($"Amount cannot exceed {ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)}.")
            .ValidMoneyScale();

        RuleFor(x => x.Description)
            .MaximumLength(ValidationRules.TransactionDescriptionMaxLength)
            .WithMessage(ValidationRules.DescriptionMaxLengthMessage)
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}
