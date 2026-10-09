namespace AzureBank.Shared.Enums;

/// <summary>
/// Lifecycle of an idempotency record (ADR-0009).
///
/// Processing → Executed → Completed
///
/// The Executed intermediate state is flipped ATOMICALLY with the business
/// commit (the record update rides the same SaveChanges/transaction), so a
/// crashed request is always distinguishable:
/// - stale Processing  = the business operation provably never committed (safe to take over)
/// - stale Executed    = committed, and its response will never be stored
///                       (409 IDEMPOTENCY_RESULT_UNKNOWN with applied: true)
///
/// The answer carries applied: true because Executed read from the database is the proof of the
/// commit: it is what tells a committed operation from one whose outcome is not known.
/// </summary>
public enum IdempotencyStatus
{
    /// <summary>Claimed: the request holds the lock but has not committed anything yet.</summary>
    Processing = 0,

    /// <summary>The business operation committed; the response is not yet stored.</summary>
    Executed = 1,

    /// <summary>The response is stored and can be replayed.</summary>
    Completed = 2
}
