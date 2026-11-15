using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Options;

/// <summary>
/// The day's ceiling on a user's outgoing external transfers (ADR-0050). Binds to the "DailyLimit"
/// section in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Policy, not regulation. PSD2 Art. 68(1) lets the payer and the payment service provider agree a
/// spending limit, and fixes no figure, no window and no zone. The figure is ours, and it is the
/// first slice of the backlog's umbrella entry ("Transaction limits and tiering") — one number, one
/// window, external transfers only. Tiers, rolling windows, withdrawals under an aggregate and
/// customer-adjustable ceilings stay with the umbrella, which is why this is an option and not a
/// constant: a tier is a per-user value, and an option is what a test host overrides with
/// <c>UseSetting</c> to make the SQL Server proof cheap.
/// </para>
/// <para>
/// An aggregate has no JSON-schema slot, so this number is deliberately not a schema
/// <c>maximum</c>: <c>ValidationRules.TransactionMaxAmount</c> stays the one per-request bound the
/// document publishes (ADR-0046 D1), and this one reaches the client only through the refusal body.
/// </para>
/// </remarks>
public class DailyLimitOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "DailyLimit";

    /// <summary>
    /// The sum of a user's completed external <c>TransferOut</c> rows in the current UTC calendar
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

    /// <summary>
    /// How long a transfer may wait for the payer's application lock, in seconds, before the
    /// movement is refused as a fault. Bounds the queue same-payer transfers stand in
    /// (<c>TransferService.DailyLimitLockSql</c>); passed to <c>sp_getapplock</c> as
    /// <c>@LockTimeout</c>, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Called without a lock timeout, <c>sp_getapplock</c> waits for ever and only the 30-second
    /// command timeout ends the wait (ADR-0050 D5 has the measurement). The default, ten seconds,
    /// is above <see cref="AuditOptions.TailTimeoutSeconds"/> (five), because the lock's span
    /// contains the audit chain's tail read, and below the command timeout, so this bound is the
    /// one that fires. A refused lock is a fault, never a <c>DailyLimitExceededException</c>.
    /// </remarks>
    /*
      Range 1 to 29: zero would mean do not wait, so a typo would turn every concurrent transfer by
      one payer into an immediate fault, and a negative value is the wait for ever this option
      removes. 29 is the largest whole second below the 30-second command timeout.
    */
    [Range(1, 29, ErrorMessage =
        "DailyLimit:LockTimeoutSeconds must be between 1 and 29. Zero means DO NOT WAIT to "
        + "sp_getapplock, and a negative value means wait forever; the upper end stays strictly "
        + "below the 30-second CommandTimeout so this bound fires first.")]
    public int LockTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// <see cref="LockTimeoutSeconds"/> in the unit <c>sp_getapplock</c> takes.
    /// </summary>
    /// <remarks>
    /// Here rather than at the call site so the unit conversion is stated once, beside the range
    /// that makes it safe: 29 seconds is 29,000 ms, far inside <c>int</c>.
    /// </remarks>
    public int LockTimeoutMilliseconds => LockTimeoutSeconds * 1_000;
}
