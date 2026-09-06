using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using FluentValidation;

namespace AzureBank.Api.Validators.Account;

/// <summary>
/// FluentValidation validator for <see cref="AccountDeletionAuthorizationRequest"/> (ADR-0049).
/// The same six-digit PIN rule as <c>TransferAuthorizationRequestValidator</c>, because the second
/// layer must exist here for the same reason it exists there: the transfer mints shipped with only
/// the DataAnnotations half at first, and a rule that lives in one layer only drifts from the
/// other. Registered by <c>AddValidatorsFromAssemblyContaining&lt;Program&gt;</c>, like every
/// validator here.
/// </summary>
public class AccountDeletionAuthorizationRequestValidator
    : AbstractValidator<AccountDeletionAuthorizationRequest>
{
    public AccountDeletionAuthorizationRequestValidator()
    {
        RuleFor(x => x.Pin)
            .NotEmpty().WithMessage("PIN is required to close an account.")
            .Matches(ValidationRules.PinPattern)
            .WithMessage(ValidationRules.PinPatternMessage);
    }
}
