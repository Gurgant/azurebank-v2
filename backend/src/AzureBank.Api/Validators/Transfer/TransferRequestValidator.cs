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

        // Mirrors WithdrawRequestValidator exactly: same two rules, same order, same messages,
        // so the two in-band PIN endpoints answer a malformed PIN identically.
        RuleFor(x => x.Pin)
            .NotEmpty().WithMessage("PIN is required for transfers.")
            .Matches(ValidationRules.PinPattern)
            .WithMessage(ValidationRules.PinPatternMessage);

        RuleFor(x => x.Description)
            .MaximumLength(ValidationRules.TransactionDescriptionMaxLength)
            .WithMessage(ValidationRules.DescriptionMaxLengthMessage)
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}
