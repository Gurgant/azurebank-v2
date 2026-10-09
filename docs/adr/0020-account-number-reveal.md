# ADR-0020: On-demand account-number reveal (masked-by-default + PIN-gated `/full-number`)

**Status:** Accepted · **Date:** 2026-07-21 · **Amended:** 2026-08-13 (the reading of PSD2), and
by ADR-0038 · **Decision Makers:** Vladislav Aleshaev

## Context

`AccountMapper` masks account numbers server-side (`AB-1234-5678-90` → `AB-****-****-90`) on every
account DTO, so before this decision the full number left the backend on no endpoint and the
account's owner could never see or copy it. An account number exists to be shared: it is how money
reaches the account. PCI-DSS masking (first6/last4) applies to card PANs only; the PCI SSC FAQ puts
bank account numbers out of scope. PSD2 Art. 4(32) takes the account number out of sensitive
payment data only for payment-initiation and account-information providers, which this app is not.
Neobanks (Monzo, Revolut, Wise, Starling) show the number in full with no re-authentication, and
keep the eye button, re-authentication and timed re-hide for card PAN and CVV.

## Decision

The number stays masked by default everywhere, and the full number is exposed only through a
dedicated, audited, step-up-gated endpoint: the card-details-grade pattern applied to an account
number, stricter than the industry norm, chosen to demonstrate the mechanism.

1. **`GET /api/accounts/{id:guid}/full-number`**: the path is load-bearing, because it is the exact
   suffix the BFF middleware gates and any other spelling would silently bypass step-up. It
   returns `ApiResponse<AccountNumberResponse>` (`accountId` and the unmasked `accountNumber`), a
   dedicated DTO, so that no generic mapping can adopt the unmasked value by accident (ASVS 14.2.6).
2. **Ownership and step-up**: the service reuses the central `GetAccountWithOwnershipCheckAsync`
   (404 and 403 like every sibling), and PIN level 2 is enforced by the BFF's `AuthLevelMiddleware`
   (`Security:PinValidityMinutes`, default 5; `verify-pin` elevates), per ASVS 3.7.1.
3. **Audit without the value** (ASVS 8.3.5): the security event `AccountNumberRevealed` logs the
   user's and the account's Guids only, because PII redaction is opt-in per call site, so the
   number must never enter the logging pipeline. The event fires only when the value is returned;
   a denied attempt surfaces through the exception pipeline.
4. **`Cache-Control: no-store` and `Pragma: no-cache`** on the response (ASVS 14.3.2); YARP
   forwards response headers untouched, so the BFF adds nothing.
5. **The gate matches the path with trailing slashes normalised**, because endpoint routing
   tolerates `/full-number/` and a raw suffix match was a step-up bypass.
6. **In the SPA, `AccountNumberField` shows the masked number with an eye button.** It calls
   `revealAccountNumber`, an RTK Query mutation although the request is a GET, so that the value
   is never cached and never retried automatically. The mutation rides `baseQueryWithStepUp`: an
   un-elevated reveal answers 403 and drives the shared PIN modal, which replays the request, so
   the component carries no step-up logic. The unmasked value lives in transient component state
   only (ASVS 14.3.1), is `reset()` out of the RTK Query store the instant it is captured, and is
   hidden again after 20 s or on unmount. Copy-to-clipboard, with a `role="status"` "Copied"
   confirmation, is the primary affordance; the eye button carries `aria-pressed` and a
   per-account `aria-label`. A cancelled PIN step-up leaves the number masked and shows no error.

## Rejected

- Rejected: the number in full on the account screen with no re-authentication, the neobank norm,
  because the stricter pattern is the mechanism this project sets out to demonstrate.
- Rejected: a level claim in the token, which would let the API enforce the level itself, because
  it is deliberately out of scope here.

## Consequences

- The account's owner can retrieve the real number; every list and detail read stays masked.
- Not covered: called directly with a JWT, the API protects the endpoint with the token and the
  ownership check only, because the level lives in the BFF session and the JWT carries no level
  claim (ADR-0041 keeps the reveal on the session model; ADR-0055 refuses a caller that does not
  hold the BFF's service credential). The same bypass through the BFF, a self-obtained bearer
  token on the proxied path with no session cookie, is closed by ADR-0038.
- Not covered: the reveal is rate-limited only by the BFF's global limiter (300 a minute per IP),
  which is accepted for an owner-only resource that cannot be enumerated (Guid ids, ownership).

## Verified by

- `AuthLevelMiddlewareTests`: the 403 with the backend never called, trailing slashes, PIN expiry.
- `AccountEndpointTests` (`FullNumber_ForOwner_ReturnsUnmaskedNumber_WithNoStore` and siblings).
- `frontend/src/pages/account-reveal.test.tsx`: the eye button, the timer and the copy.

## Related

ADR-0008, ADR-0038, ADR-0041, ADR-0055.
