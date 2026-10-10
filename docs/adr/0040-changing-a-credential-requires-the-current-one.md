# ADR-0040: Changing a credential requires proving the current one

**Status:** Accepted · **Date:** 2026-08-12 · **Amended:** 2026-08-15 (the enrolment password),
2026-09-03 (ADR-0045), 2026-09-04 (ADR-0041, ADR-0042). Supersedes nothing.

## Context

`POST /api/auth/pin` used to assign `user.PinHash` unconditionally: nothing asked for the PIN
already on the account, and nothing required an elevated session. A caller holding only a session
could replace the PIN and then satisfy every gate the PIN protects: set a PIN, set another with no
proof of the first, verify the new one, read the unmasked account number. Two protections fail at
once and neither can catch it. The attempt-limiting of ADR-0010 counts wrong guesses, and nothing is
guessed. The step-up gate of ADR-0008 verifies that a PIN was entered, never that it was the user's.
The only place that can express the check is the point of replacement.

## Decision

**Changing a PIN requires the current one. Enrolling does not: it requires the account password.**

1. **`SetPinRequest` has an optional `CurrentPin`.** Optional, because the requirement depends on
   stored state the schema cannot see: `[Pin]` validates the format only when a value is present,
   and `AuthService.SetPinAsync` owns the rule.
2. **When `user.PinHash` is non-null, `CurrentPin` is required and verified through
   `IPinVerifier`**, not the hasher directly, because a wrong `CurrentPin` then counts against the
   same ADR-0010 lockout as every other wrong PIN. Verified any other way, the endpoint would be an
   uncounted brute-force oracle, which is worse than the hole it replaces.
3. **When `PinHash` is null the request is an enrolment.** No current PIN is asked for, because
   there is none to prove. The account password is required in the same request, because a session
   cookie alone would otherwise mint the credential that authorises every money movement.
4. **Failure shapes match the API's other PIN checks**, so the step-up paths answer alike. A missing
   proof is **422 `PIN_REQUIRED`** and not 400 (a rule the schema cannot express: the split
   `BusinessRuleException` documents), a wrong PIN is **401 `INVALID_PIN`**, and a locked PIN is
   **429**, inherited from the verifier. An enrolment without the password is 422
   `PASSWORD_REQUIRED`.

The BFF needs no change: `/bff/auth/set-pin` forwards the shared DTO whole, so the fields and every
error shape ride through.

## Rejected

- Rejected: the check at the step-up gate or in the attempt-limiting, because neither layer can tell
  a replaced PIN from the user's own.
- Rejected: verifying `CurrentPin` with the hasher directly, because wrong values would not count
  toward the lockout.
- Rejected: enrolling on a session alone, because a stolen session on an account that has never set
  a PIN could then enrol one of its choosing and elevate.

## Consequences

- The PIN is a credential and not a formality: the lockout of ADR-0010 protects something, because
  replacement is no longer a way around guessing.
- The contract changes (`SetPinRequest`, the spec, the generated frontend types), and any caller
  that replaces a PIN fails with 422 `PIN_REQUIRED` until it sends `CurrentPin`.
- The frontend has two callers: `PinSetupPage` on the enrolment path, which turns away a user whose
  `hasPin` is already true, and the Change PIN dialog in Settings, which sends the current PIN.
- A notice follows an enrolment (ADR-0045) and a change (ADR-0047).
- Not covered: the direct-API step-up bypass for `/full-number`. The API has no auth-level concept,
  so the reveal's gate lives only in BFF middleware, and it keeps that session model by ADR-0041,
  decision 6 (`frontend/src/mocks/stepup.test.ts`). Transfers, withdrawals and account closures do
  not share it: the API itself verifies their PIN, and the authorisation it mints is bound to the
  operation and spent once (ADR-0041, ADR-0042, ADR-0049, ADR-0056). The stronger model is the one
  that guards the money.

## Verified by

- `PinReplacementTests`: a change without the current PIN or with a wrong one is refused, wrong
  values trip the same lockout, and a locked PIN cannot be replaced.
- `AuthEndpointTests` (`SetPin_Enrolling_WithoutPassword_IsRefused`).

## Related

ADR-0008, ADR-0010, ADR-0011, ADR-0020, ADR-0038, ADR-0041, ADR-0042, ADR-0045, ADR-0047, ADR-0049,
ADR-0056.
