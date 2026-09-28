namespace AzureBank.Shared.Enums;

/// <summary>
/// Why a refresh token (the BFF session's grant) was revoked. Stored by NAME in
/// <c>RefreshTokens.RevokedReason</c>, so the runbook's SQL can write it and a reader of the table
/// can read it without this file.
/// </summary>
/// <remarks>
/// <para>
/// The reason decides what a later renewal with the same grant means (06 §4.3). Only
/// <see cref="SessionEnded"/> makes a renewal received AFTER the revoke a tripwire: the BFF ended
/// that session itself and holds the grant nowhere else, so nothing legitimate can present it again.
/// Every other reason is a lever pulled from outside the session, which a live session can learn of
/// late and innocently.
/// </para>
/// <para>
/// NULL on a row is a legacy row: revoked by the rotation this replaced, or by a sign-out before the
/// column existed. Such a row is refused like any other revoked grant, as "revoked for another
/// reason".
/// </para>
/// </remarks>
public enum RefreshTokenRevokedReason
{
    /// <summary>The BFF ended the session that held the grant ("Esci", idle expiry, the cap, a new sign-in).</summary>
    SessionEnded = 0,

    /// <summary>Every grant of the user, through <c>POST /api/auth/logout</c> or the runbook's SQL.</summary>
    SignOutEverywhere = 1,

    /// <summary>The operator's answer to a tripwire row (06 §5): the user's grants, revoked by hand.</summary>
    ReuseContainment = 2,

    /// <summary>The nuclear lever (06 §5): every active grant, after the keys were rotated.</summary>
    Incident = 3,

    /// <summary>
    /// Set by the migration that introduced this column on every grant still active then: those were
    /// issued for seven days and sliding, and the grant now lives only as long as its session.
    /// </summary>
    Deployment = 4,
}
