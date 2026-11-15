using AzureBank.Api.Validators.Transfer;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// Unit tests for <see cref="TransferAuthorizationRequestValidator"/> and
/// <see cref="InternalTransferAuthorizationRequestValidator"/>, the two transfer mints.
/// </summary>
/// <remarks>
/// Only the SCALE rule is pinned here, and it is the one a mutation would take out without a sound.
/// <c>[MoneyRange]</c> on the DTOs checks the range and not the scale, so <c>.ValidMoneyScale()</c> in
/// these validators is the only thing that refuses 10.00001 at a mint. Without it the mint answers 201
/// for an amount the transfer then refuses, and the authorisation, bound to the amount rendered to four
/// places, would spend a transfer of 10.00. The withdrawal mint's twin of these theories is in
/// <c>WithdrawalAuthorizationRequestValidatorTests</c>.
/// </remarks>
public class StepUpAuthorizationRequestValidatorTests
{
    private readonly TransferAuthorizationRequestValidator _transferValidator = new();
    private readonly InternalTransferAuthorizationRequestValidator _internalValidator = new();

    private static TransferAuthorizationRequest CreateValidTransferRequest() => new()
    {
        FromAccountId = Guid.NewGuid(),
        RecipientAzureTag = "recipient",
        Amount = 100m,
        Pin = "123456"
    };

    private static InternalTransferAuthorizationRequest CreateValidInternalRequest() => new()
    {
        FromAccountId = Guid.NewGuid(),
        ToAccountId = Guid.NewGuid(),
        Amount = 100m,
        Pin = "123456"
    };

    [Fact]
    public void Validate_TransferMint_WithValidRequest_ShouldNotHaveErrors()
    {
        var result = _transferValidator.TestValidate(CreateValidTransferRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_InternalTransferMint_WithValidRequest_ShouldNotHaveErrors()
    {
        var result = _internalValidator.TestValidate(CreateValidInternalRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(10.001)]
    [InlineData(10.00001)]  // the amount that minted a 201 the transfer then refused on #113
    public void Validate_TransferMint_WithMoreThanTwoDecimalPlaces_ShouldHaveError(decimal amount)
    {
        var request = CreateValidTransferRequest();
        request.Amount = amount;
        var result = _transferValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(10.5)]
    [InlineData(10.55)]
    public void Validate_TransferMint_WithAtMostTwoDecimalPlaces_ShouldNotHaveError(decimal amount)
    {
        var request = CreateValidTransferRequest();
        request.Amount = amount;
        var result = _transferValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData(10.001)]
    [InlineData(10.00001)]  // the amount that minted a 201 the transfer then refused on #113
    public void Validate_InternalTransferMint_WithMoreThanTwoDecimalPlaces_ShouldHaveError(decimal amount)
    {
        var request = CreateValidInternalRequest();
        request.Amount = amount;
        var result = _internalValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(10.5)]
    [InlineData(10.55)]
    public void Validate_InternalTransferMint_WithAtMostTwoDecimalPlaces_ShouldNotHaveError(decimal amount)
    {
        var request = CreateValidInternalRequest();
        request.Amount = amount;
        var result = _internalValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }
}
