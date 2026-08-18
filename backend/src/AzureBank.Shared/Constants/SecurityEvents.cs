namespace AzureBank.Shared.Constants;

/// <summary>
/// The names of the security events this system logs, and the single place they are spelled.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is emitted as the first argument of a <c>"SecurityEvent {SecurityEvent}: …"</c>
/// log message, which is what an operator alerts on. Before this class they were 21 bare string
/// literals across two projects, so a rename in one place and not another produced two event streams
/// with no compiler complaint and no failing test — the alert would simply stop matching, silently,
/// which for an audit signal is the worst possible failure mode.
/// <c>AzureBank.Tests.Architecture.SecurityEventConstantTests</c> is what stops this from decaying
/// back into decoration: it fails the build if any log site writes the name inline. (Named in prose
/// rather than with <c>see cref</c> — Shared cannot reference the test assembly, and a cref that
/// does not resolve is a build warning.)
/// </para>
/// <para>
/// This lives in Shared, not the API, and that is load-bearing rather than tidy: six of the
/// twenty-one sites are in the BFF (<c>AuthLevelMiddleware</c>, <c>FetchMetadataMiddleware</c>,
/// <c>TokenRefresher</c>, <c>Program</c>), so an API-side class would leave nearly a third of the
/// vocabulary unable to reference it — the exact split that let the literals drift in the first place.
/// </para>
/// <para>
/// OWASP MAPPING, and why the values are ours rather than OWASP's.
/// Each constant below names its counterpart in the OWASP Logging Vocabulary Cheat Sheet where one
/// exists. The counterpart is documented, NOT adopted as the value: these strings are already in
/// operators' alert rules and in shipped log history, so changing them would break every existing
/// query to buy a naming convention. The map is what makes the log translatable by standard tooling
/// without rewriting the past. Where no counterpart exists it says so plainly instead of forcing a
/// near-miss — a wrong mapping is worse than an absent one, because it makes a generic alert fire on
/// an event it does not describe.
/// </para>
/// </remarks>
public static class SecurityEvents
{
    // ── Authentication and tokens ──────────────────────────────────────────────────────────────

    /// <summary>A refresh was presented with a token the store has never seen.</summary>
    /// <remarks>
    /// No OWASP counterpart. <c>authn_token_reuse</c> describes a token that WAS issued and is being
    /// replayed; this one was never issued, so the two must stay distinguishable — conflating them
    /// would make a reuse alert fire on ordinary garbage.
    /// </remarks>
    public const string RefreshTokenUnknown = "RefreshTokenUnknown";

    /// <summary>A revoked refresh token was presented again — the theft signal (ADR-0021).</summary>
    /// <remarks>OWASP: <c>authn_token_reuse[:userid,tokenid]</c>. An exact match.</remarks>
    public const string RefreshTokenReuse = "RefreshTokenReuse";

    /// <summary>
    /// Reuse was detected but revoking the token family FAILED — the mitigation did not happen.
    /// </summary>
    /// <remarks>
    /// No OWASP counterpart: the vocabulary names events, not failures to respond to them. This is
    /// deliberately its own name and logged at Error, because it is the one case where a detected
    /// compromise was left un-contained and a human has to act.
    /// </remarks>
    public const string RefreshTokenReuseRevokeFailed = "RefreshTokenReuseRevokeFailed";

    /// <summary>The BFF's refresh of a proxied session was rejected; the session was revoked.</summary>
    /// <remarks>OWASP: <c>session_expired:[userid,reason]</c> — the outcome is the session ending.</remarks>
    public const string RefreshRejected = "RefreshRejected";

    // ── Step-up authentication (ADR-0008 / ADR-0041) ───────────────────────────────────────────

    /// <summary>A level-2 route was reached with a live session below level 2 — 403 + step-up.</summary>
    /// <remarks>OWASP: <c>authz_fail:[userid,resource]</c>. Authenticated, not authorised yet.</remarks>
    public const string StepUpRequired = "StepUpRequired";

    /// <summary>A level-2 route was reached with NO session at all — 401, route to login.</summary>
    /// <remarks>
    /// No OWASP counterpart. <c>session_use_after_expire</c> is the near miss and is wrong for the
    /// common case: this also fires for a caller that never had a session, and the two demand
    /// different responses (re-authenticate versus investigate). Kept separate from
    /// <see cref="StepUpRequired"/> for the same reason — "prove it is you" and "you are not signed
    /// in" are different states with different remedies.
    /// </remarks>
    public const string StepUpWithoutSession = "StepUpWithoutSession";

    // ── Request-level defences (BFF) ───────────────────────────────────────────────────────────

    /// <summary>A cross-site request was refused by the Sec-Fetch-Site check.</summary>
    /// <remarks>OWASP: <c>malicious_csrf:[userid|IP]</c>.</remarks>
    public const string CrossSiteRequestBlocked = "CrossSiteRequestBlocked";

    /// <summary>
    /// A caller reached the proxied <c>/api/auth/refresh</c> directly, which no browser should.
    /// </summary>
    /// <remarks>OWASP: <c>malicious_direct_reference:[userid|IP, useragent]</c>.</remarks>
    public const string RawRefreshBlocked = "RawRefreshBlocked";

    /// <summary>A request was refused by a rate-limiter partition.</summary>
    /// <remarks>OWASP: <c>excess_rate_limit_exceeded[userid,max]</c>. An exact match.</remarks>
    public const string RateLimitExceeded = "RateLimitExceeded";

    // ── Account and user data ──────────────────────────────────────────────────────────────────

    /// <summary>An unmasked account number was returned to its owner (ADR-0020).</summary>
    /// <remarks>
    /// OWASP: <c>sensitive_read:[userid,file|object]</c>. This is the detective control that makes
    /// the reveal endpoint auditable — WHO revealed WHICH account, and when.
    /// </remarks>
    public const string AccountNumberRevealed = "AccountNumberRevealed";

    /// <summary>An account was closed by its owner — the soft delete of ADR-0008's level table.</summary>
    /// <remarks>
    /// <para>
    /// OWASP: <c>sensitive_delete:[userid,file|object]</c> — the same family as
    /// <see cref="AccountNumberRevealed"/>'s <c>sensitive_read</c>, which is why it is used here
    /// rather than <c>user_deleted</c>: that one names the deletion of a USER IDENTITY, and an alert
    /// written for it firing on a closed bank account would be describing the wrong event.
    /// </para>
    /// <para>
    /// It is a SOFT delete — <c>Account.IsDeleted</c> behind a global query filter — so "deleted"
    /// here means withdrawn from the owner's view, not erased. That distinction matters less than it
    /// sounds: measured, the owner cannot see it, list it, or fetch it by id afterwards
    /// (<c>404 ACCOUNT_NOT_FOUND</c>), and no endpoint restores it. Only an operator with database
    /// access can undo it, so treat this as irreversible from the account holder's side.
    /// </para>
    /// <para>
    /// Why it exists at all: until now this was a plain
    /// <c>LogInformation("Soft deleted account {AccountId}")</c>, so it never reached the stream an
    /// operator alerts on — while revealing your own account number, a strictly less consequential
    /// act, did. Two guards make the money case unreachable (non-zero balance and primary account
    /// both refuse with 422), so this is a detective control over integrity, not over funds.
    /// </para>
    /// </remarks>
    public const string AccountDeleted = "AccountDeleted";

    /// <summary>A user changed their own public handle (ADR-0015).</summary>
    /// <remarks>OWASP: <c>user_updated:[userid,onuserid,attributes[…]]</c>.</remarks>
    public const string AzureTagRenamed = "AzureTagRenamed";

    /// <summary>A PIN was bound to an account for the FIRST time, behind a password proof.</summary>
    /// <remarks>
    /// OWASP has no exact counterpart: <c>user_updated:[userid,onuserid,attributes[…]]</c> is the
    /// closest, and it undersells the event — this binds an authentication factor that unlocks
    /// every money movement, which is why it is recorded separately from an ordinary profile edit.
    ///
    /// <para>
    /// ⚠️ This is a DETECTIVE record in the operator's log. It is NOT the notification NIST
    /// SP 800-63-4B §4.1.2 requires ("the CSP SHALL notify the subscriber via a mechanism
    /// INDEPENDENT of the transaction binding the new authenticator") — that has to reach the
    /// account owner, not the operator, and this codebase has no email transport at all. Tracked
    /// separately; do not let this line stand in for it.
    /// </para>
    /// </remarks>
    public const string PinEnrolled = "PinEnrolled";

    /// <summary>A self-registration was refused because the email or handle already exists.</summary>
    /// <remarks>
    /// No OWASP counterpart: <c>user_created</c> covers the success only. This exists because the
    /// RESPONSE is deliberately enumeration-neutral (ADR-0013) — the caller cannot tell which field
    /// collided, so the server-side log is the only place the real reason survives, and a burst of
    /// these is what account-enumeration probing looks like.
    /// </remarks>
    public const string DuplicateRegistration = "DuplicateRegistration";

    /// <summary>
    /// A self-registration was refused by Identity for a reason that is NOT a duplicate — a password
    /// that fails policy, an invalid user name, anything else Identity validates.
    /// </summary>
    /// <remarks>
    /// <para>OWASP: <c>input_validation_fail:[(fieldone,fieldtwo…),userid]</c> — the closest fit, since
    /// Identity's rejection is server-side validation of the submitted credentials.</para>
    /// <para>
    /// This one was NOT in the first inventory of this vocabulary, and how it was missed is worth
    /// recording: its site chooses between two names with a ternary
    /// (<c>isDuplicate ? DuplicateRegistration : RegistrationRejected</c>), so a scan that reads one
    /// name per log statement counts the site once and sees only the first. The architecture test
    /// found it on its first run — which is the argument for the test, not a footnote to it.
    /// </para>
    /// </remarks>
    public const string RegistrationRejected = "RegistrationRejected";

    // ── Generated-identifier collisions ────────────────────────────────────────────────────────

    /// <summary>A generated account number collided with an existing one and was retried.</summary>
    /// <remarks>
    /// No OWASP counterpart — the vocabulary has no category for a uniqueness retry. It is logged on
    /// this channel anyway because a rising rate means the generator's entropy is being exhausted,
    /// which is a security property even though a single occurrence is routine.
    /// </remarks>
    public const string AccountNumberCollision = "AccountNumberCollision";

    /// <summary>A generated transaction number collided and was retried.</summary>
    /// <remarks>No OWASP counterpart; same reasoning as <see cref="AccountNumberCollision"/>.</remarks>
    public const string TransactionNumberCollision = "TransactionNumberCollision";
}
