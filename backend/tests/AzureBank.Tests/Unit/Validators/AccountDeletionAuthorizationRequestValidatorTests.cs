using AzureBank.Api.Validators.Account;
using AzureBank.Shared.DTOs.Account;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for <see cref="AccountDeletionAuthorizationRequestValidator"/> (ADR-0049) — the same
/// six-digit rule the transfer mints apply, pinned here so the closure mint cannot drift from them.
/// </summary>
public class AccountDeletionAuthorizationRequestValidatorTests
{
    private readonly AccountDeletionAuthorizationRequestValidator _validator = new();

    [Fact]
    public void Validate_WithASixDigitPin_ShouldNotHaveErrors()
    {
        var result = _validator.TestValidate(new AccountDeletionAuthorizationRequest { Pin = "123456" });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]        // Empty
    [InlineData("12345")]   // Too short
    [InlineData("1234567")] // Too long
    [InlineData("12345a")]  // Not all digits
    [InlineData("12 456")]  // Whitespace inside
    public void Validate_WithAPinThatIsNotSixDigits_ShouldHaveError(string pin)
    {
        var result = _validator.TestValidate(new AccountDeletionAuthorizationRequest { Pin = pin });
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }
}
