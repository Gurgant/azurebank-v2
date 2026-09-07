namespace AzureBank.Shared.Options;

/// <summary>
/// The day's ceiling on a user's outgoing external transfers (ADR-0050). Binds to the "DailyLimit"
/// section in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Policy, not regulation. PSD2 Art. 68(1) lets the payer and the payment service provider AGREE a
/// spending limit, and fixes no figure, no window and no zone. The figure is ours, and it is the
/// first slice of the backlog's umbrella entry ("Transaction limits and tiering") — one number, one
/// window, external transfers only. Tiers, rolling windows, withdrawals under an aggregate and
/// customer-adjustable ceilings stay with the umbrella, which is why this is an option and not a
/// constant: a tier is a per-user value, and an option is what a test host overrides with
/// <c>UseSetting</c> to make the SQL Server proof cheap.
/// </para>
/// <para>
/// An aggregate has no JSON-schema slot, so this number is deliberately NOT a schema
/// <c>maximum</c>: <c>ValidationRules.TransactionMaxAmount</c> stays the one per-request bound the
/// document publishes (ADR-0046 D1), and this one reaches the client only through the refusal body.
/// </para>
/// </remarks>
public class DailyLimitOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "DailyLimit";

    /// <summary>
    /// The sum of a user's COMPLETED external <c>TransferOut</c> rows in the current UTC calendar
    /// day, plus the requested amount, may not exceed this. Validated at startup: positive, and at
    /// most two decimals, so no sub-cent noise can reach the 422 body.
    ///
    /// <para>
    /// Default 5,000: below <c>TransactionMaxAmount</c>, so a single per-transaction-valid request
    /// of 5,000.01 is refused by this bound with zero money moved (the cheap contract row), and far
    /// above anything the automated real-stack traffic moves in a day, so the shared seeded fixture
    /// never exhausts its own day. The 1,000 that <c>ValidationRules.DailyTransferLimit</c> once
    /// carried is not restored: it was never a decision, only a dead constant (ADR-0046 D6).
    /// </para>
    /// </summary>
    public decimal Amount { get; set; } = 5_000m;
}
