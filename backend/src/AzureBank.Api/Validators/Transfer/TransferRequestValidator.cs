using AzureBank.Api.Validation;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation;

namespace AzureBank.Api.Validators.Transfer;

/// <summary>
/// FluentValidation validator for TransferRequest (external transfer to another user).
/// </summary>
public class TransferRequestValidator : AbstractValidator<TransferRequest>
{
    public TransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId)
            .NotEmpty().WithMessage(ValidationRules.AccountNotEmptyGuid);

        RuleFor(x => x.RecipientAzureTag)
            .NotEmpty().WithMessage("Recipient AzureTag is required.")
            .Length(ValidationRules.AzureTagMinLength, ValidationRules.AzureTagMaxLength)
            .WithMessage($"AzureTag must be between {ValidationRules.AzureTagMinLength} and {ValidationRules.AzureTagMaxLength} characters.")
            .Matches(ValidationRules.AzureTagPattern)
            .WithMessage(ValidationRules.AzureTagPatternMessage);

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
