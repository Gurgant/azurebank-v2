using AzureBank.Api.Validation;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentValidation;

namespace AzureBank.Api.Validators.Transaction;

/// <summary>
/// FluentValidation validator for WithdrawRequest.
/// </summary>
/// <remarks>
/// The PIN rules left with the field (ADR-0056): the PIN is proved at the withdrawal MINT, and
/// <see cref="WithdrawalAuthorizationRequestValidator"/> carries them now, including the money
/// scale rule that <c>[MoneyRange]</c> does not check.
/// </remarks>
public class WithdrawRequestValidator : AbstractValidator<WithdrawRequest>
{
    public WithdrawRequestValidator()
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
