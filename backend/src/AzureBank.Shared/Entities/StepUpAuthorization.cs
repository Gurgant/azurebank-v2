using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// One row per authorisation minted from a PIN: proof that THIS account holder approved THIS amount
/// to THIS payee, spendable exactly once (ADR-0042, PSD2-RTS Art. 5 dynamic linking).
///
/// <para>
/// Before this table a PIN proved only "someone knows the PIN". Measured on <c>main</c> @ 4811667:
/// the same six digits authorised two different transfers back to back, and nothing recorded that an
/// authorisation had been given, let alone spent. Art. 5 asks for four things and only (a) held —
/// the payer saw the amount and payee. This row is (b), (c) and (d): specific to the amount and
/// payee, invalidated when either changes, and accepted once.
/// </para>
///
/// <para>
/// Deliberately shaped like <see cref="IdempotencyRecord"/> and NOT like <c>BaseEntity</c>: explicit
/// <see cref="CreatedAt"/>/<see cref="ExpiresAt"/> rather than managed timestamps, no soft delete, a
/// status enum, and a keyed hash standing in for the fields it binds. The two answer different
/// questions — "may this happen?" versus "has this already happened?" — so both travel on a transfer
/// and neither substitutes for the other.
/// </para>
///
/// <para>
/// Nothing deletes from this table. A consumed row is the evidence B3 has to produce, and a
/// mint-then-delete design would need a second write to record what the first one erased — two
/// writes that can disagree, with the disagreement invisible until someone asks for the evidence.
/// </para>
/// </summary>
public class StepUpAuthorization
{
    /// <summary>
    /// The authorisation reference. Returned by the mint endpoint and echoed back by the client in
    /// the <c>Step-Up-Authorization</c> request header — never in the body, because the idempotency
    /// fingerprint is computed over the body alone
    /// (<c>IdempotencyService.ComputeRequestHashAsync(Stream body, …)</c>). In the body, a retry
    /// carrying a different, expired or absent authorisation would change the bytes and be refused
    /// as <c>IDEMPOTENCY_KEY_REUSE</c> (422) rather than reaching the endpoint at all.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Who authorised. Every lookup is scoped by this as well as by <see cref="Id"/>: an
    /// authorisation reference is not a bearer token, and one user presenting another's id must not
    /// be able to spend it — nor to learn that it exists.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// What was authorised. Also part of <see cref="BindingHash"/>, so an authorisation minted for a
    /// transfer cannot be spent on an internal one; the column exists so the evidence pack can read
    /// it without recomputing anything.
    /// </summary>
    public StepUpOperation Operation { get; set; }

    /// <summary>
    /// HMAC-SHA256 (server-side key, never stored in the database) over the fields that define the
    /// operation, lowercase hex. See <c>IStepUpAuthorizationService.ComputeBindingHash</c> for the
    /// exact composition.
    ///
    /// <para>
    /// A hash rather than a column per bound field, for the reason
    /// <see cref="IdempotencyRecord.RequestHash"/> gives: a hash cannot be partially compared by
    /// accident, and adding a field to the operation forces the hash definition to change instead of
    /// silently leaving the new field unbound. Keyed rather than bare, because
    /// <c>(amount, payeeId)</c> is a small space — an unkeyed digest would let anyone with database
    /// read access confirm guesses about who paid whom, and how much.
    /// </para>
    /// </summary>
    public required string BindingHash { get; set; }

    public StepUpAuthorizationStatus Status { get; set; } = StepUpAuthorizationStatus.Pending;

    /// <summary>When the PIN was proved (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// End of the authorisation window (UTC). Short by design — it exists only to cross the gap
    /// between proving the PIN and the operation being performed, and with the client sending on the
    /// sixth digit that gap is milliseconds. An expired row is refused, never re-executed and never
    /// deleted.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When it was spent (UTC). Null while Pending.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>
    /// The transaction this authorisation paid for. What ties the evidence to the money movement,
    /// and the reason consumption is written by the operation rather than before it.
    /// </summary>
    public Guid? ConsumedByTransactionId { get; set; }
}
