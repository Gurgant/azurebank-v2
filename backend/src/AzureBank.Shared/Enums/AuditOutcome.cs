namespace AzureBank.Shared.Enums;

/// <summary>
/// What happened, for one audit event. Derived from the 23 existing security-event log sites rather
/// than invented: every one of them falls into exactly one of these four.
/// </summary>
/// <remarks>
/// The grouping is the useful part, because it decides HOW each event can be written. A
/// <see cref="Succeeded"/> event rides the business transaction and is atomic with it. A
/// <see cref="Refused"/> one has no transaction to join — and two of them
/// (<c>AuthService</c>'s duplicate-registration races) sit INSIDE an
/// <c>ExecuteInTransactionAsync</c> that rolls back with the 409 they record, so writing them the
/// same way would erase them. See ADR-0044.
/// </remarks>
public enum AuditOutcome
{
    /// <summary>
    /// The caller was refused; nothing was written to the business tables. 13 of the 23 sites,
    /// including every event the BFF raises.
    /// </summary>
    Refused = 0,

    /// <summary>
    /// The action completed and this row is its evidence. 4 sites today — account closed, account
    /// number revealed, handle renamed, PIN enrolled.
    /// </summary>
    Succeeded = 1,

    /// <summary>
    /// A generated identifier collided and was retried. 5 sites. Individually routine; a rising rate
    /// means a generator is running out of entropy, which is why it is on this channel at all.
    /// </summary>
    RetryCollision = 2,

    /// <summary>
    /// A detected compromise was NOT contained — the mitigation itself failed. One site today
    /// (<c>RefreshTokenReuseRevokeFailed</c>), and the only one logged at Error, because it is the
    /// case where a human has to act.
    /// </summary>
    MitigationFailed = 3
}
