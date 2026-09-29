namespace AzureBank.Shared.Enums;

/// <summary>
/// What happened, for one audit event. Derived from the security-event log sites rather than
/// invented: every one of them falls into exactly one of these four. There were 27 sites then, and
/// there are 27 again since PR-1, which removed the one <see cref="MitigationFailed"/> site and
/// added one <see cref="Succeeded"/> site that writes no row (<c>RefreshRenewalRateHigh</c>).
/// </summary>
/// <remarks>
/// The grouping is the useful part, because it decides HOW each event can be written. A
/// <see cref="Succeeded"/> event that writes a row rides the business transaction and is atomic
/// with it (<c>RefreshRenewalRateHigh</c> writes none). A
/// <see cref="Refused"/> one has no transaction to join — and two of them
/// (<c>AuthService</c>'s duplicate-registration races) sit INSIDE an
/// <c>ExecuteInTransactionAsync</c> that rolls back with the 409 they record, so writing them the
/// same way would erase them. See ADR-0044.
/// </remarks>
public enum AuditOutcome
{
    /// <summary>
    /// The caller was refused; nothing was written to the business tables. 17 of the 27 sites,
    /// including every event the BFF raises.
    /// </summary>
    Refused = 0,

    /// <summary>
    /// The action completed, and its row, where one is written, is its evidence. 5 sites today —
    /// account closed, account number revealed, handle renamed, PIN enrolled, and a grant renewed
    /// more often than the BFF renews one (<c>RefreshRenewalRateHigh</c>, since PR-1). That fifth is
    /// the one site of this outcome that writes NO row: the renewal was answered, so it belongs here,
    /// but a renewal writes nothing (ADR-0057 §6, anomaly 2), so its only record is the log line.
    /// </summary>
    Succeeded = 1,

    /// <summary>
    /// A generated identifier collided and was retried. 5 sites. Individually routine; a rising rate
    /// means a generator is running out of entropy, which is why it is on this channel at all.
    /// </summary>
    RetryCollision = 2,

    /// <summary>
    /// A detected compromise was NOT contained — the mitigation itself failed. No site raises it
    /// since PR-1: its one site was <c>RefreshTokenReuseRevokeFailed</c>, logged at Error, and the
    /// tripwire that replaced reuse detection revokes nothing, so no mitigation is left to fail
    /// (ADR-0057 F3). Kept for the rows already written, which the audit trail never purges.
    /// </summary>
    MitigationFailed = 3
}
