# ADR-0040: Changing a credential requires proving the current one

**Status:** Accepted · **Date:** 2026-08-12 · **Supersedes nothing.** Closes a hole that made
[ADR-0008](0008-step-up-authentication.md)'s step-up gate and
[ADR-0010](0010-pin-attempt-limiting.md)'s attempt-limiting both inoperative.

## Context

`POST /api/auth/pin` assigned `user.PinHash` unconditionally. Nothing asked for the PIN already on
the account, and nothing required an elevated session to call it.

So a caller holding only a session could **replace** the PIN and then satisfy every gate the PIN
protects. Measured end to end through the BFF, session cookie only, no token ever visible to the
caller:

```
register             -> 201, authLevel 1
set-pin  "131313"    -> 200      (enrolment)
set-pin  "999999"    -> 200      <- no proof of "131313" asked for
verify-pin "999999"  -> 200, authLevel 2
GET .../full-number  -> 200      "AB-3142-8079-89", unmasked
```

The same chain works directly against the API on `:7215` with a bearer token, and there it also
reaches withdraw: a wrong PIN is 401 and a missing one 400, but an *attacker-chosen* one passes, and
the withdrawal then fails only on the balance check (422) — after `VerifyPinAsync` has already
accepted it.

**Two protections were nullified at once, and neither could have caught it.**

- **ADR-0010's attempt-limiting never engages.** It counts wrong guesses. Nothing was guessed.
- **ADR-0008's step-up gate cannot help.** It verifies that *a* PIN was entered, never that the
  entered PIN was the user's. The elevated session was, by the gate's own rules, legitimate.

That is why the check cannot live at either of those layers. The only place that can express it is
the point of replacement.

## Decision

**Changing a PIN requires the current one. Enrolling does not.**

1. `SetPinRequest` gains an optional `CurrentPin`. Optional deliberately: the requirement is
   conditional on stored state the schema cannot see, so `[Pin]` validates format only when a value
   is present and `AuthService.SetPinAsync` owns the rule.
2. When `user.PinHash` is non-null, `CurrentPin` is required and verified **through `IPinVerifier`**,
   not the hasher directly. That is load-bearing: it makes a wrong `CurrentPin` count against the
   same ADR-0010 lockout as every other wrong PIN. Verifying it any other way would turn this
   endpoint into an uncounted brute-force oracle — strictly worse than the hole it replaces.
3. **Enrolment stays open** when `PinHash` is null. The account password already gated getting
   there, and there is nothing to prove yet.
4. Failure shapes match what withdraw already returns, so the two step-up paths answer alike:
   missing proof is **422 `PIN_REQUIRED`** (a rule the schema cannot express — the split
   `BusinessRuleException` documents), a wrong one is **401 `INVALID_PIN`**, and a locked PIN is
   **429** inherited from the verifier.

The BFF needs no change: `/bff/auth/set-pin` forwards the shared DTO wholesale, so the field and
every error shape ride through untouched.

## What this does not close

**Enrolling the FIRST PIN still requires only a session.** An attacker holding a stolen session on
an account that has never set a PIN can enrol one of their choosing and elevate. Stated plainly
rather than left to be rediscovered.

It is narrower than what this ADR closes — it needs an account with no PIN yet, and the window shuts
the moment the real user enrols — but it is the same shape of escalation. What would close it is
requiring the account password at enrolment. Not done here because registration hands the user
straight to the PIN wizard seconds after authenticating with that same password, so the prompt would
be asking again for something just proven, and the exposure is a stolen session rather than a
credential gap. That reasoning is worth re-examining if the enrolment entry point ever moves away
from the post-registration handoff.

Also unchanged: the **direct-API step-up bypass** for transfers and `/full-number`
(ADR-0020/ADR-0038 residual). The API has no auth-level concept; enforcement for those two lives
only in BFF middleware. Withdraw is unaffected because it carries the PIN in-band and the API
verifies it. That inconsistency — two step-up models in one system, the weaker one guarding the
higher-value operation — is a separate decision, and this ADR is a precondition for it rather than a
substitute: hardening the API gate while the PIN can be replaced would be building a control that is
not one.

## Consequences

**Positive** — the PIN becomes a credential rather than a formality. ADR-0010's lockout now protects
something, because replacement is no longer a way around guessing.

**Negative** — a contract change (`SetPinRequest`, spec, generated frontend types), and a
**behaviour change for any caller that REPLACES a PIN**: those requests now fail with 422
`PIN_REQUIRED` until they send `CurrentPin`. Stated separately from the enrolment case because the
two differ. **Enrolment is unaffected**, and the frontend's only caller is `PinSetupPage`, which
bounces a user whose `hasPin` is already true — so this repo's own client reaches the endpoint
exclusively on the enrolment path. Any other consumer replacing a PIN must be updated.

## Notes on the evidence

The guard was not found by reading the code. It surfaced from a design review of a *different*
problem, and was then reproduced on the running stack before any code was written.

Nothing in the existing suite failed when the guard was added — the tests covered enrolling a PIN
and verifying a PIN, and never covered changing one. The MSW mock was worse than silent: it
documented itself as "set/overwrite … no old PIN and no step-up required" and implemented exactly
that, so a test asserting the mock would have pinned the bypass as correct behaviour.

## References

ADR-0008 (step-up) · ADR-0010 (PIN attempt-limiting) · ADR-0011 (PIN-hash pepper) ·
ADR-0020 (account-number reveal) · ADR-0038 (the session is the only credential the BFF accepts).
