using System.ComponentModel.DataAnnotations;

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

    /// <summary>
    /// How long a transfer may wait for the payer's application lock, in seconds, before the
    /// movement is refused as a fault. Bounds the queue same-payer transfers stand in
    /// (<c>TransferService.DailyLimitLockSql</c>); passed to <c>sp_getapplock</c> as
    /// <c>@LockTimeout</c>, in MILLISECONDS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED, which is why this exists at all. <c>sp_getapplock</c> called without
    /// <c>@LockTimeout</c> waits at <c>@@LOCK_TIMEOUT</c>, and that session default is
    /// <b>-1 — wait forever</b> (read off the waiter's own connection on LocalDB, 2026-09-07). The
    /// only thing that ended the wait was the global 30-second command timeout set in
    /// <c>AddInfrastructure</c>
    /// (<c>AzureBank.Infrastructure/Extensions/ServiceCollectionExtensions.cs</c>,
    /// <c>sqlOptions.CommandTimeout(30)</c>) — which bounds the whole statement rather than the
    /// wait, and holds an open transaction and a pooled connection for half a minute while it does.
    /// With the parameter supplied the wait ends at the parameter: a holder took the lock in one
    /// transaction and a second connection asking with <c>@LockTimeout = 2000</c> was refused
    /// <b>-1 after 2,006-2,012 ms across three runs</b>.
    /// </para>
    /// <para>
    /// TEN SECONDS, and the number is bracketed rather than guessed. The lock is held from the
    /// first statement of the transfer's transaction to its commit, and that span CONTAINS the
    /// audit chain's tail read, which <see cref="AuditOptions.TailTimeoutSeconds"/> already bounds
    /// at five — so anything at or below five would refuse a waiter whose winner was still inside a
    /// wait the bank had been told to tolerate. Ten is two of those, and a third of the command
    /// timeout above it, so the refusal is always THIS bound's and never the statement's.
    /// </para>
    /// <para>
    /// A REFUSED LOCK IS A FAULT, NOT A LIMIT REFUSAL. Waiting out this bound says the server was
    /// busy, not that the payer's day is full, so the batch raises and the transfer fails loudly —
    /// it must never become a <c>DailyLimitExceededException</c>, whose 422 tells the client a
    /// figure that was never computed.
    /// </para>
    /// </remarks>
    /*
      RANGE-VALIDATED, because the two invalid values fail in opposite and equally bad ways — the
      shape Audit:TailTimeoutSeconds records for the same reason.
      ZERO is the dangerous one here in the other direction: sp_getapplock reads @LockTimeout = 0 as
      DO NOT WAIT, so a plausible typo would turn every concurrent transfer by one payer into an
      immediate fault instead of a queue of milliseconds.
      NEGATIVE is the bug this option removes: -1 is sp_getapplock's own "wait forever", so a
      negative value would silently restore the unbounded wait.
      TWENTY-NINE is not a considered maximum; it is the largest whole second STRICTLY BELOW the
      30-second CommandTimeout, so the wait can never outlive the statement that carries it.
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
