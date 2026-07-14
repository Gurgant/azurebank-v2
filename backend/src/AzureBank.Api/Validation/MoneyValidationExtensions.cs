using AzureBank.Shared.Constants;
using FluentValidation;

namespace AzureBank.Api.Validation;

/// <summary>
/// Shared FluentValidation rules for monetary amounts.
/// Reused by the Deposit, Withdraw, Transfer, and InternalTransfer validators
/// so the scale rule lives in exactly one place.
///
/// Lives in AzureBank.Api.Validation (not .Validators) because the architecture
/// naming convention requires every type under a *Validators* namespace to be a
/// *Validator; this is a shared rule helper, not a validator.
/// </summary>
public static class MoneyValidationExtensions
{
    /// <summary>
    /// Rejects amounts with more than <see cref="ValidationRules.MoneyDecimalPlaces"/>
    /// decimal places (e.g. 10.12345). Without this, the client is told a full-precision
    /// amount the DECIMAL(19,4) column silently rounds away (sub-cent creation).
    /// </summary>
    public static IRuleBuilderOptions<T, decimal> ValidMoneyScale<T>(
        this IRuleBuilder<T, decimal> ruleBuilder) =>
        ruleBuilder
            .Must(amount => decimal.Round(amount, ValidationRules.MoneyDecimalPlaces) == amount)
            .WithMessage($"Amount cannot have more than {ValidationRules.MoneyDecimalPlaces} decimal places.");
}
