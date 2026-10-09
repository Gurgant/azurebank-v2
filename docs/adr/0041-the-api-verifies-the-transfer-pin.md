# ADR-0041: The API verifies the transfer PIN, not the BFF

**Status:** Accepted · **Date:** 2026-08-13 · **Amended:** 2026-08-16 (ADR-0042), 2026-08-17 and
2026-08-19 (decisions 4 and 5), 2026-09-06 (ADR-0049), 2026-10-05 (decision 4)

## Context

Before this decision a transfer's PIN was checked in one place, `AuthLevelMiddleware` in the BFF
process, and the API's `TransferService` never asked for one. The check was a property of the
session, not of the payment: one PIN entry raised the session to level 2 and every transfer of
the next five minutes passed without another. And anything reaching the API directly moved money
with no PIN at all, because the only PIN check lived in a different process from the code that
moved it. "Only reachable from the BFF" is a documented anti-pattern (OWASP, NIST SP 800-207).

## Decision

1. **The API verifies a transfer's PIN, and the BFF's level-2 gate no longer covers transfers**
   (`PinRequiredPaths` is empty), because a check inside the process that moves the money cannot
   be skipped by reaching that process directly, and gating twice would leave the weaker check in
   the path. Since ADR-0042 the PIN is presented to the authorisation mint, not in the transfer's
   body, and the transfer carries the minted authorisation in the `Step-Up-Authorization` header.
2. **The PIN is checked after the source account's ownership, through `IPinVerifier`**, which
   records a failed attempt and can set the lockout (ADR-0010) in `PinService`'s own DbContext
   scope, because an uncounted check would be a brute-force oracle. A refusal moves no money.
3. **The answers, measured at the API with no BFF in the path**: the mint answers 201 for the
   right PIN, 401 `INVALID_PIN`, 429 `PIN_LOCKED` after repeated wrong PINs, 422 `PIN_REQUIRED`
   with none enrolled and 400 with no `pin` field. A transfer with no `Idempotency-Key` is refused
   400 `IDEMPOTENCY_KEY_MISSING` first, because idempotency is middleware and runs before binding.
4. **The BFF requires a live session of every proxied request**: any method under `/api/`, and
   the bare path `/api`, or it answers 401 with the API's own `AUTH_TOKEN_MISSING` body and
   forwards nothing. There is no exception list, because a list cannot notice the path nobody
   added, and the refusal must not rest on `BearerTokenTransformProvider` clearing the inbound
   `Authorization`: a gate that depends on a different file getting something right is not a gate.
5. **The proxied `/api/auth/login` and `/api/auth/register` answer 404, even to a valid
   session**, because the SPA signs in through `/bff/auth/login` and the proxied login handed a
   sessionless caller the API's JWT, which the BFF exists to withhold. Their YARP routes stay,
   because they carry `RateLimiterPolicy "auth"` and the catch-all route carries none.
6. **`/full-number` keeps the session model (decision D3a)**, because a GET with no body, no
   amount and no payee has nothing to bind an in-band credential to. It is a product decision,
   not a finding that PSD2 is silent: Art. 4(32) takes an account number out of sensitive payment
   data only for payment-initiation and account-information providers, Art. 97(1)(c) is a
   catch-all, and no legal review has been done. A closure has a subject, the account, so it
   does not share the exemption: it is authorised at the API on ADR-0042's rail (ADR-0049).
7. **The SPA collects the PIN on the wizard's third step (`form → review → pin`), where Send is**:
   a wrong PIN clears the boxes, a lock shows the server's `Retry-After`, no PIN set: `/pin-setup`.

## Rejected

- Rejected: a list of paths that need a session, because six endpoints fell through it unseen.
- Rejected: a session gate for POST only, because every other method was left to the API alone.
- Rejected: defaulting an absent `pin`, because that is the failure this decision exists to prevent.
- Rejected: dropping a session's level when its own request meets the lockout's 429, because
  another session, a stolen one, would stay elevated while the gap read as closed.

## Consequences

- No monetary endpoint sits behind the BFF's step-up gate: the SPA's replay after an elevation
  (ADR-0022 §4) has the reveal as its only live caller. Every PIN check shares one lockout.
- Not covered: this decision alone is not dynamic linking. SCA-RTS Art. 5 asks for a code specific
  to amount and payee, accepted once (Art. 4(1)); a standing PIN is neither. ADR-0042 adds it.
- Not covered: the lockout is the API's (`PinService`), the level the BFF's: a session elevated
  before a lockout still reveals numbers, and moves no money, until `PinValidityMinutes` (5) ends.
- Not covered: the API asks for no PIN on the reveal (decision 6): a caller holding a bearer token
  and the BFF's service credential (ADR-0055) reads the number. `SECURITY.md` lists it.

## Revisit when

- (a) The level-2 gate protects anything beyond a read (`RequiresPinVerification` true for a new
  operation, by either of its branches or a third): the lockout gap then exposes more than a number.
- (b) The session store becomes shared or user-indexed: per-user revocation is then cheap.

## Verified by

- `TransferPinVerificationTests`: decisions 2 and 3, each case sent straight to the API.
- `AuthLevelMiddlewareTests`: decisions 1 and 4 to 6 at the BFF, nothing forwarded on a refusal.
- `transfer-pin-recovery.test.tsx`, `stepup-interceptor.test.tsx`: decision 7 and the SPA's gate.

## Related

ADR-0001, ADR-0008, ADR-0010, ADR-0020, ADR-0022, ADR-0038, ADR-0042, ADR-0049, ADR-0055.
