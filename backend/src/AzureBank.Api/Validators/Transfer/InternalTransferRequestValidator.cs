using AzureBank.Api.Validation;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation;

namespace AzureBank.Api.Validators.Transfer;

/// <summary>
/// FluentValidation validator for InternalTransferRequest (transfer between own accounts).
/// </summary>
public class InternalTransferRequestValidator : AbstractValidator<InternalTransferRequest>
{
    public InternalTransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId)
            .NotEmpty().WithMessage("Source account is required.");

        RuleFor(x => x.ToAccountId)
            .NotEmpty().WithMessage("Destination account is required.")
            .NotEqual(x => x.FromAccountId)
            .WithMessage("Cannot transfer to the same account.");

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(ValidationRules.TransactionMinAmount)
            .WithMessage($"Amount must be at least {ValidationRules.TransactionMinAmount:C}.")
            .LessThanOrEqualTo(ValidationRules.TransactionMaxAmount)
            .WithMessage($"Amount cannot exceed {ValidationRules.TransactionMaxAmount:C}.")
            .ValidMoneyScale();

        RuleFor(x => x.Description)
            .MaximumLength(ValidationRules.TransactionDescriptionMaxLength)
            .WithMessage(ValidationRules.DescriptionMaxLengthMessage)
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}
