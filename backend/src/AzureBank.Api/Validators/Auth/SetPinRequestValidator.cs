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
    }
}
