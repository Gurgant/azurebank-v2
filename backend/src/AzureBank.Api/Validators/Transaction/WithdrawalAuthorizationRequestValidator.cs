using AzureBank.Api.Validation;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentValidation;

namespace AzureBank.Api.Validators.Transaction;

/// <summary>
/// FluentValidation validator for <see cref="WithdrawalAuthorizationRequest"/>. Mirrors
/// <see cref="WithdrawRequestValidator"/> so an authorisation can never be minted for an amount the
/// withdrawal itself would reject.
/// </summary>
/// <remarks>
/// Rule for rule with the withdrawal's own validator, minus Description, which is not part of the
/// binding. The one that matters and is invisible in the DTO is <c>ValidMoneyScale</c>:
/// <c>[MoneyRange]</c> checks the RANGE and not the SCALE, so without this line an amount of
/// 10.00001 would mint a 201 that the withdrawal then refuses with 400 -- a row nothing can ever
/// spend. That is not hypothetical; it is what happened on the transfer mints before their
/// validators existed (#113).
/// </remarks>
public class WithdrawalAuthorizationRequestValidator
    : AbstractValidator<WithdrawalAuthorizationRequest>
{
    public WithdrawalAuthorizationRequestValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage(ValidationRules.AccountNotEmptyGuid);

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(ValidationRules.TransactionMinAmount)
            .WithMessage($"Amount must be at least {ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)}.")
            .LessThanOrEqualTo(ValidationRules.TransactionMaxAmount)
            .WithMessage($"Amount cannot exceed {ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)}.")
            .ValidMoneyScale();

        RuleFor(x => x.Pin)
            .NotEmpty().WithMessage("PIN is required for withdrawals.")
            .Matches(ValidationRules.PinPattern)
            .WithMessage(ValidationRules.PinPatternMessage);
    }
}
