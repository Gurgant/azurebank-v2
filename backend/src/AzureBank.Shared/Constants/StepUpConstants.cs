namespace AzureBank.Shared.Constants;

/// <summary>
/// Wire-level constants for step-up authorisations (ADR-0042).
/// </summary>
public static class StepUpConstants
{
    /// <summary>
    /// Request header carrying the authorisation reference (UUID) minted by one of the three
    /// mints: <c>POST /api/transfers/authorizations</c>,
    /// <c>POST /api/transfers/internal/authorizations</c> and
    /// <c>POST /api/accounts/{id}/deletion-authorizations</c>.
    ///
    /// <para>
    /// This said <c>POST /api/auth/step-up</c> until 2026-09-21. There has never been such a
    /// route: <c>AuthController</c> is <c>[Route("api/auth")]</c> with login, register, refresh,
    /// me, logout, pin and pin/verify, and the committed contract publishes no path containing
    /// <c>step-up</c>. Every other piece of prose in the repository named the real mints.
    /// </para>
    ///
    /// <para>
    /// A HEADER, never a body field, and the reason is measured rather than stylistic:
    /// <c>IdempotencyService.ComputeRequestHashAsync(Stream body, …)</c> fingerprints the request
    /// BODY only. Keeping the authorisation out of the body is what lets the same transfer be
    /// resent byte-identically — with the same <c>Idempotency-Key</c> — while carrying a different,
    /// expired, or absent authorisation. In the body, each of those would change the fingerprint and
    /// be refused as <c>IDEMPOTENCY_KEY_REUSE</c> (422) before the endpoint ever saw it.
    /// </para>
    /// </summary>
    public const string HeaderName = "Step-Up-Authorization";
}
