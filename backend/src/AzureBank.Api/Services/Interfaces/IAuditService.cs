using AzureBank.Shared.Enums;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Writes to the durable audit trail (ADR-0044). TWO methods, and choosing between them is a
/// decision about atomicity, not a style preference — read the remarks before calling either.
/// </summary>
/// <remarks>
/// <para>
/// THE RULE: does the event describe something that COMMITTED, or something that was REFUSED?
/// </para>
/// <list type="bullet">
/// <item>
/// Something committed — the account was closed, the number was revealed — then the audit row must
/// be atomic with it. That is <see cref="Record"/>: it only calls <c>Add</c>, and the operation's own
/// <c>SaveChangesAsync</c> writes it inside whatever transaction already exists. If the audit insert
/// fails, the whole unit rolls back and the action does not happen. That is the decision recorded in
/// ADR-0044, in the owner's words: better a bank that is blocked than a bank that is emptied.
/// </item>
/// <item>
/// Something was refused — a duplicate registration, a reused refresh token — then there is usually
/// no transaction to join, and where there IS one it is about to roll back. Measured:
/// <c>AuthService</c>'s two duplicate-registration races log from inside
/// <c>strategy.ExecuteInTransactionAsync</c>, and that transaction rolls back with the 409 it
/// records. Using <see cref="Record"/> there would write the row and then delete it. That is what
/// <see cref="RecordRefusalAsync"/> is for: it writes on its own connection and commits immediately,
/// so the refusal survives the rollback of whatever refused.
/// </item>
/// </list>
/// <para>
/// A refusal is the event an operator most wants to see. An audit trail that keeps the successes and
/// loses the refusals is worse than none, because it reads as if nothing was ever attempted.
/// </para>
/// </remarks>
public interface IAuditService
{
    /// <summary>
    /// Enlists an audit row in the CURRENT unit of work. Does NOT save — the caller's next
    /// <c>SaveChangesAsync</c> writes it, atomically with the business change.
    /// </summary>
    /// <remarks>
    /// The contract is copied deliberately from <c>IIdempotencyService.MarkExecutedPending</c>,
    /// which is this codebase's existing precedent for "a bookkeeping row that rides the business
    /// commit". Nothing here opens a transaction: at <c>TransferService</c> the row joins the
    /// explicit one, and at <c>AccountService.DeleteAccountAsync</c> — which has none — it joins the
    /// implicit multi-statement transaction EF opens for <c>SaveChanges</c>. The chain fields are
    /// filled later, by <c>AuditChainInterceptor</c>, because they can only be computed inside that
    /// transaction.
    /// </remarks>
    /// <param name="securityEvent">One of the <c>SecurityEvents</c> constants. Never a literal.</param>
    /// <param name="outcome">Must be <see cref="AuditOutcome.Succeeded"/> or <see cref="AuditOutcome.RetryCollision"/>.</param>
    /// <param name="actorUserId">Who acted. Null only where nobody could be identified.</param>
    /// <param name="subjectType">What kind of thing <paramref name="subjectId"/> names.</param>
    /// <param name="subjectId">The object acted on.</param>
    /// <param name="detail">Event-specific JSON. Must contain no personal data (ADR-0044 D5).</param>
    void Record(
        string securityEvent,
        AuditOutcome outcome,
        Guid? actorUserId = null,
        string? subjectType = null,
        Guid? subjectId = null,
        string? detail = null);

    /// <summary>
    /// Writes a refusal on its OWN connection and commits immediately, so that the rollback of the
    /// operation being refused cannot take the record of the refusal with it.
    /// </summary>
    /// <remarks>
    /// Deliberately not fire-and-forget: it is awaited, and a failure to write is allowed to
    /// surface. The alternative — swallowing it — would produce exactly the silent gap this whole
    /// task exists to close.
    /// </remarks>
    Task RecordRefusalAsync(
        string securityEvent,
        AuditOutcome outcome,
        Guid? actorUserId = null,
        string? subjectType = null,
        Guid? subjectId = null,
        string? detail = null,
        CancellationToken cancellationToken = default);
}
