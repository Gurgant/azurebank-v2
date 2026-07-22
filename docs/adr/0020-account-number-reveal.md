# ADR-0020: On-demand account-number reveal (masked-by-default + PIN-gated `/full-number`)

**Status**: Accepted

**Date**: 2026-07-21

**Decision Makers**: Vladislav Aleshaev

---

## Context

`AccountMapper` masks account numbers **server-side** (`AB-1234-5678-90` →
`AB-****-****-90`) on every account DTO, so before this ADR the full number never left
the backend on any endpoint — which also meant the owner could never see or copy their
own account number. An account number exists to be shared (it is how money reaches the
account), so a permanently invisible number is a functional hole, not a security win.

The architecture had already reserved the answer: the BFF's `AuthLevelMiddleware` gates
`/api/accounts/*/full-number` at auth level 2 (PIN), and `08-security-design.md`
specifies the route — but the API endpoint did not exist.

What the standards actually say, verified against primary sources:

- **PCI-DSS masking (first6/last4) applies to card PANs only** — the PCI SSC FAQ puts
  bank account numbers, sort codes and routing numbers out of scope.
- **PSD2 art. 4(32)** explicitly states the account owner's name and account number *do
  not constitute sensitive payment data*.
- **Neobank practice** (Monzo, Revolut, Wise, Starling): account numbers/IBANs are shown
  **in full** on an account-details screen with copy/share affordances, no re-auth. The
  eye-button + re-auth + timed-rehide treatment is what banks reserve for **card
  PAN/CVV** (Revolut "security check", Chase Face-ID + timed rehide).

## Decision

Keep **masked-by-default everywhere**, and expose the full number only through a
dedicated, audited, step-up-gated reveal endpoint — deliberately applying the
card-details-grade pattern to an account number (stricter than industry norm, chosen as
a portfolio demonstration of the mechanism):

1. **`GET /api/accounts/{id:guid}/full-number`** — the path is **load-bearing**: it is
   the exact suffix the BFF middleware already gates; any other spelling would silently
   bypass step-up. Returns `ApiResponse<AccountNumberResponse>` (`accountId` +
   unmasked `accountNumber`), a dedicated DTO so the unmasked value is a
   self-describing shape no generic mapping can adopt by accident (ASVS 14.2.6 data
   minimization stays the default).
2. **Ownership + step-up**: the service reuses the central
   `GetAccountWithOwnershipCheckAsync` (404/403 like every sibling); PIN level 2 is
   enforced by the BFF `AuthLevelMiddleware` (`Security:PinValidityMinutes`, default 5 —
   the dev example config uses 10; `verify-pin` to elevate) — ASVS 3.7.1.
3. **Audit without the value** (ASVS 8.3.5): `SecurityEvent AccountNumberRevealed` logs
   user + account **Guids only**. PII redaction in this codebase is opt-in per call
   site, so the number must never enter the logging pipeline at all. The event fires
   only when the value is actually returned — denied attempts surface through the
   exception pipeline instead.
4. **`Cache-Control: no-store` + `Pragma: no-cache`** on the response (ASVS 14.3.2);
   YARP forwards response headers untouched, so the BFF needs nothing.
5. **Middleware hardening**: gate matching now normalizes trailing slashes. Endpoint
   routing tolerates `/full-number/` (and `/api/transfers/`), so the previous raw
   suffix/exact match was a **step-up bypass**; `AuthLevelMiddlewareTests` — the
   middleware's first test suite — pins the 403 short-circuit (backend provably never
   called), the trailing-slash variants, the post-PIN pass-through, and PIN expiry.

6. **Frontend consumption (eye button) — implemented (PR-R2).** An `AccountNumberField`
   component replaces the masked `<Text>` in each account card. It calls `revealAccountNumber`,
   an RTK Query **mutation** (never cached, never auto-retried; a GET request with mutation
   semantics so the value never enters the query cache). Because the mutation rides the slice-wide
   `baseQueryWithStepUp`, an un-elevated reveal 403s and drives the shared PIN modal, which
   replays the request on elevation — the component carries no step-up logic. The unmasked value
   lives in **transient component state only** (ASVS 14.3.1) and is `reset()` out of the RTK Query
   store the instant it is captured; it **auto-rehides** after a 20s timer or on unmount
   (navigation). A **copy-to-clipboard** button (with a `role="status"` "Copied" confirmation) is
   the primary affordance, and the eye button carries **`aria-pressed`** toggle semantics with a
   per-account `aria-label`. A cancelled PIN step-up is a benign no-op (stays masked, no error).

## Consequences

- The owner can finally retrieve their real account number; every list/detail read stays
  masked.
- **Dual-mode caveat (accepted)**: when the API is called directly with a JWT (Swagger /
  dev mode), the endpoint is protected by JWT + ownership only — the auth level lives in
  the BFF session and the JWT carries no level claim. This matches the existing posture
  for transfers (ADR-0018/0019); closing it would require a level claim in the token and
  is deliberately out of scope.
- The reveal is rate-limited only by the BFF global limiter (300/min/IP) — acceptable for
  an owner-only, non-enumerable resource (Guid ids, ownership-checked).
- The OpenAPI spec grows to 19 paths; `schema.d.ts` regenerated. The FE gained
  `AccountNumberResponse`, now consumed by the eye button (PR-R2).
