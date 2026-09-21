using AzureBank.Api.Validators.Transaction;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for <see cref="WithdrawalAuthorizationRequestValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The PIN cases here came from <c>WithdrawRequestValidatorTests</c> with the field itself
/// (ADR-0056). They are not new coverage and they are not duplicated: a withdrawal no longer
/// carries a PIN, and this is now the only endpoint that does, so this is where the rules are.
/// </para>
/// <para>
/// The SCALE case is the one that would be cheapest to leave out and most expensive to lose.
/// <c>[MoneyRange]</c> on the DTO checks the RANGE and not the SCALE, so FluentValidation is the
/// only layer that refuses 10.00001 -- and on the transfer mints, before their validators existed,
/// exactly that amount minted a 201 the transfer then refused with 400, leaving a row nothing could
/// ever spend (#113). A mint whose validation is laxer than its operation's is an authorisation for
/// a withdrawal that cannot happen.
/// </para>
/// </remarks>
public class WithdrawalAuthorizationRequestValidatorTests
{
    private readonly WithdrawalAuthorizationRequestValidator _validator = new();

    private static WithdrawalAuthorizationRequest CreateValidRequest() => new()
    {
        AccountId = Guid.NewGuid(),
        Amount = 100m,
        Pin = "123456"
    };

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var result = _validator.TestValidate(CreateValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithEmptyAccountId_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.AccountId = Guid.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AccountId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithInvalidAmount_ShouldHaveError(decimal amount)
    {
        var request = CreateValidRequest();
        request.Amount = amount;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithAmountExceedingMax_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.Amount = ValidationRules.TransactionMaxAmount + 0.01m;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithMinAmount_ShouldNotHaveError()
    {
        var request = CreateValidRequest();
        request.Amount = ValidationRules.TransactionMinAmount;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData(10.001)]
    [InlineData(10.00001)]  // the exact amount that minted an unspendable authorisation on #113
    public void Validate_WithMoreThanTwoDecimalPlaces_ShouldHaveError(decimal amount)
    {
        var request = CreateValidRequest();
        request.Amount = amount;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(10.5)]
    [InlineData(10.55)]
    public void Validate_WithAtMostTwoDecimalPlaces_ShouldNotHaveError(decimal amount)
    {
        var request = CreateValidRequest();
        request.Amount = amount;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Validate_WithEmptyPin_ShouldHaveError()
    {
        var request = CreateValidRequest();
        request.Pin = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }

    [Theory]
    [InlineData("12345")]    // Too short
    [InlineData("1234567")]  // Too long
    [InlineData("12345a")]   // Contains letter
    public void Validate_WithInvalidPin_ShouldHaveError(string pin)
    {
        var request = CreateValidRequest();
        request.Pin = pin;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Pin);
    }
}
