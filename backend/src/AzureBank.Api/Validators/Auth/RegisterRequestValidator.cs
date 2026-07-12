using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation;

namespace AzureBank.Api.Validators.Auth;

/// <summary>
/// FluentValidation validator for RegisterRequest.
/// </summary>
public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.AzureTag)
            .NotEmpty().WithMessage("AzureTag is required.")
            .Length(ValidationRules.AzureTagMinLength, ValidationRules.AzureTagMaxLength)
            .WithMessage($"AzureTag must be between {ValidationRules.AzureTagMinLength} and {ValidationRules.AzureTagMaxLength} characters.")
            .Matches(ValidationRules.AzureTagPattern)
            .WithMessage(ValidationRules.AzureTagPatternMessage);

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.")
            .MaximumLength(ValidationRules.EmailMaxLength)
            .WithMessage($"Email cannot exceed {ValidationRules.EmailMaxLength} characters.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .Length(ValidationRules.PasswordMinLength, ValidationRules.PasswordMaxLength)
            .WithMessage($"Password must be between {ValidationRules.PasswordMinLength} and {ValidationRules.PasswordMaxLength} characters.")
            .Matches(ValidationRules.PasswordPattern)
            .WithMessage(ValidationRules.PasswordPatternMessage);

        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .Length(ValidationRules.FirstNameMinLength, ValidationRules.FirstNameMaxLength)
            .WithMessage(ValidationRules.FirstNameLengthMessage)
            .Matches(ValidationRules.NamePattern)
            .WithMessage(ValidationRules.NamePatternMessage);

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .Length(ValidationRules.LastNameMinLength, ValidationRules.LastNameMaxLength)
            .WithMessage(ValidationRules.LastNameLengthMessage)
            .Matches(ValidationRules.NamePattern)
            .WithMessage(ValidationRules.NamePatternMessage);
    }
}
