using AzureBank.Api.Validation;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation;

namespace AzureBank.Api.Validators.Transfer;

/*
  WHY THESE EXIST, and why their absence was a real defect rather than a missing formality.

  The DTOs mirror TransferRequest / InternalTransferRequest attribute for attribute, and the comment
  on them says exactly that. But the transfer endpoints are guarded by TWO layers — DataAnnotations
  AND FluentValidation — and only the first was mirrored. `[MoneyRange]` checks the range; it does
  NOT check the scale. `.ValidMoneyScale()` lives only here.

  Measured consequence of the gap: an amount of 10.00001 minted an authorisation (201) that the
  transfer endpoint then refused (400), leaving a row nothing could ever spend. An authorisation
  whose validation is laxer than the operation's is an authorisation for a transfer that cannot
  happen — which is the one thing the DTO comment promised could not drift. Found by CodeRabbit on
  #113; the promise was mine, and it was already broken when I wrote it.

  So these mirror the transfer validators rule for rule, minus Description (not part of the binding:
  RTS Art. 5(1)(d) invalidates on a change to the amount or the payee, and a different memo moves the
  same money to the same person).
*/

/// <summary>
/// FluentValidation validator for <see cref="TransferAuthorizationRequest"/>. Mirrors
/// <see cref="TransferRequestValidator"/> so an authorisation can never be minted for an amount or a
/// payee the transfer itself would reject.
/// </summary>
public class TransferAuthorizationRequestValidator : AbstractValidator<TransferAuthorizationRequest>
{
    public TransferAuthorizationRequestValidator()
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
            .WithMessage($"Amount must be at least {ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)}.")
            .LessThanOrEqualTo(ValidationRules.TransactionMaxAmount)
            .WithMessage($"Amount cannot exceed {ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)}.")
            .ValidMoneyScale();

        RuleFor(x => x.Pin)
            .NotEmpty().WithMessage("PIN is required for transfers.")
            .Matches(ValidationRules.PinPattern)
            .WithMessage(ValidationRules.PinPatternMessage);
    }
}

/// <summary>
/// FluentValidation validator for <see cref="InternalTransferAuthorizationRequest"/>. Mirrors
/// <see cref="InternalTransferRequestValidator"/>, including the same-account rule — otherwise an
/// authorisation could be minted for a movement the transfer refuses during model validation.
/// </summary>
public class InternalTransferAuthorizationRequestValidator
    : AbstractValidator<InternalTransferAuthorizationRequest>
{
    public InternalTransferAuthorizationRequestValidator()
    {
        RuleFor(x => x.FromAccountId)
            .NotEmpty().WithMessage("Source account is required.");

        RuleFor(x => x.ToAccountId)
            .NotEmpty().WithMessage("Destination account is required.")
            .NotEqual(x => x.FromAccountId)
            .WithMessage("Cannot transfer to the same account.");

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(ValidationRules.TransactionMinAmount)
            .WithMessage($"Amount must be at least {ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)}.")
            .LessThanOrEqualTo(ValidationRules.TransactionMaxAmount)
            .WithMessage($"Amount cannot exceed {ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)}.")
            .ValidMoneyScale();

        RuleFor(x => x.Pin)
            .NotEmpty().WithMessage("PIN is required for transfers.")
            .Matches(ValidationRules.PinPattern)
            .WithMessage(ValidationRules.PinPatternMessage);
    }
}
