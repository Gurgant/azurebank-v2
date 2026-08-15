using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation;

namespace AzureBank.Api.Validators.Auth;

/// <summary>
/// FluentValidation validator for SetPinRequest.
/// </summary>
public class SetPinRequestValidator : AbstractValidator<SetPinRequest>
{
    public SetPinRequestValidator()
    {
        RuleFor(x => x.Pin)
            .NotEmpty().WithMessage("PIN is required.")
            .Matches(ValidationRules.PinPattern)
            .WithMessage(ValidationRules.PinPatternMessage);

        // Format only, and only when supplied. WHETHER it is required depends on the stored hash,
        // which the validator cannot see — AuthService.SetPinAsync owns that rule.
        RuleFor(x => x.CurrentPin)
            .Matches(ValidationRules.PinPattern)
            .WithMessage(ValidationRules.PinPatternMessage)
            .When(x => !string.IsNullOrEmpty(x.CurrentPin));

        // Presence only, and only when supplied — same split as CurrentPin above. WHETHER a
        // password is required depends on the stored PIN hash, which the validator cannot see.
        // No format rule on purpose: this is a check against a hash, not a new password being
        // chosen, so restating the registration policy here would only invent a way to reject a
        // correct password that predates a policy change.
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .When(x => x.Password is not null);
    }
}
