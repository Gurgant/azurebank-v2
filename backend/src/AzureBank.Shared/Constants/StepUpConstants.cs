namespace AzureBank.Shared.Constants;

/// <summary>
/// Wire-level constants for step-up authorisations (ADR-0042).
/// </summary>
public static class StepUpConstants
{
    /// <summary>
    /// Request header carrying the authorisation reference (UUID) minted by
    /// <c>POST /api/auth/step-up</c>.
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
