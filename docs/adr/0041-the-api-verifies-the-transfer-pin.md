# ADR-0041: The API verifies the transfer PIN, not the BFF

**Status:** Accepted · **Date:** 2026-08-13 · Moves transfers off the session gate
[ADR-0008](0008-step-up-authentication.md) built and [ADR-0022](0022-client-money-mutation-protocol.md)
consumes. Does **not** supersede either: `/full-number` keeps that gate.

## Context

A transfer's PIN was checked in one place: `AuthLevelMiddleware`, in the **BFF process**. The API's
`TransferService` never asked for a PIN and had no way to.

Two consequences, and the second is the serious one.

**The check was a property of the session, not of the payment.** One PIN entry raised the session to
level 2 and every transfer for the next five minutes passed without another. The credential
authenticated *the user*, once, and then authorised *an unbounded number of payments*.

**Anything reaching the API directly moved money with no PIN at all.** That is not hypothetical: it
was measured on the running stack and is written up in `BearerTokenTransformProvider` — re-presenting
a login's own JWT as `Authorization: Bearer …` with no session cookie reached `POST /api/transfers`,
and `GET /api/accounts/{id}/full-number` returned the unmasked number, with no PIN ever entered.
That specific hole was closed by clearing the inbound header unconditionally in the YARP transform,
but the shape of the problem stayed: the only PIN check for a transfer lived in a different process
from the code that moved the money, and

> "a gate whose correctness depends on a different file getting something right is not a gate"
> — `AuthLevelMiddleware`, written when the previous instance of this was fixed.

"Only reachable from the BFF" is a documented anti-pattern (OWASP, NIST SP 800-207, Curity), and the
withdraw path had already shown the alternative: it carries the PIN **in the request** and
`TransactionService.WithdrawAsync` verifies it through `IPinVerifier`.

## Decision

**A transfer carries its PIN in the request body, and the API verifies it.**

`TransferRequest` and `InternalTransferRequest` gain `required string Pin`.
`TransferService.VerifyPinOrThrowAsync` runs in both transfer methods, in the position withdraw puts
it: **after the ownership check, before the funds check, before anything is written.** A wrong PIN
therefore costs nothing and moves nothing, and it goes through `IPinVerifier`, so wrong guesses
count against the same lockout ([ADR-0010](0010-pin-attempt-limiting.md)) rather than becoming an
uncounted brute-force oracle.

Measured on the real pipeline (`TransferPinVerificationTests`, every case dispatched straight at the
API with no BFF in the path):

| case | status | errorCode |
|---|---|---|
| correct PIN | 201 | — |
| wrong PIN | 401 | `INVALID_PIN` |
| repeated wrong PINs | 429 | `PIN_LOCKED` |
| no PIN enrolled | 422 | `PIN_REQUIRED` |
| PIN field absent (an old client) | 400 | validation |
| no `Idempotency-Key`, correct PIN | 400 | `IDEMPOTENCY_KEY_MISSING` |

The last row records an **order**, not just an outcome: the idempotency filter is an action filter,
so it runs before the controller action and therefore before the PIN check. The mock reproduces that
order, and this is where it was measured rather than assumed.

### What moved at the BFF, and what deliberately did not

`PinRequiredPaths` is now empty; transfers are no longer level-2 gated. Gating them twice would leave
the *weaker* check in the path and keep the five-minute session window alive for money movement.

But "is there a caller at all?" and "has that caller just proved it is them?" are different
questions, and they used to be answered by the same block — so moving transfers off the level-2 gate
would have silently taken their **no-session refusal** with it. That is not a live bypass (the YARP
transform clears the caller's own `Authorization` on every proxied path, so the API would 401), but
it would have moved the decision to another process while this ADR claimed to be strengthening one.
So the two questions are now separate sets, and transfers sit in `SessionRequiredPaths`: refused
locally, at the BFF, with the API's own 401 shape, trailing slash normalised the same way.

`/full-number` keeps the session model (decision D3a). It is a GET with no body, no amount and no
payee — there is nothing for an in-band credential to be **bound to** — and PSD2 does not treat it as
an SCA trigger at all: Art. 97(1)'s list is exhaustive, and Art. 4(32) says an account number is not
sensitive payment data. Two questions, two mechanisms, on purpose.

### The frontend

The PIN is collected as a third wizard step (`form → review → pin`), reusing withdraw's `PinInput`
and its error handling: a wrong PIN clears the boxes and re-focuses, a lock shows the server's own
`Retry-After` horizon, an un-enrolled user is sent to `/pin-setup`. The review screen already
promised "You'll confirm with your PIN on the next step" — **as of this change that sentence is
true**; before it, the modal appeared only if the session had gone cold.

## What this is NOT

**This is not dynamic linking, and A1 does not make the product PSD2-compliant.**

SCA-RTS Art. 5 requires the authentication code to be **specific to the amount and the payee**,
invalidated by any change to either, and accepted **once** (Art. 4(1)). The PIN verified here is none
of those things: it is the user's standing PIN, it is not bound to the amount or the recipient, and
nothing stops the same value authorising a different payment a second later. What A1 buys is that
the check now exists **where the money moves** and cannot be skipped by reaching the API directly —
a precondition for dynamic linking, not dynamic linking itself. That is A2.

**Withdraw is not compliant either, and never was.** It has carried the PIN in-band since before this
ADR, and this ADR makes transfers match it — including matching its limits. Nothing here should be
read as "withdraw was already right".

**`VerifyPinOrThrowAsync` improves on withdraw in one respect.** Withdraw collapses "no such user"
and "user has not enrolled a PIN" into a single `PIN_REQUIRED`, which tells a token-holder with no
row to go and set a PIN — advice that cannot be followed, for an account that is not there. The
transfer path separates them (`NotFoundException` vs `PIN_REQUIRED`). Withdraw should converge on
this, and has not yet.

## Consequences

- **Breaking contract change.** `Pin` is `required`, so every caller supplies it: the FE, the mock,
  the E2E suite, and 34 test initialisers across 8 API test files. An old client sending no `pin` gets 400,
  which is the intended answer — silently defaulting it would be the failure this ADR exists to
  prevent.
- **No monetary endpoint sits behind the step-up gate any more.** The interceptor's
  replay-the-same-body-with-the-same-key path (ADR-0022 §4) therefore has no live caller: reveal is
  a GET and carries no `Idempotency-Key`. The interceptor is still exercised — re-pointed at reveal
  in `policies.test.tsx` and `stepup.test.ts` — and `stepup-interceptor.test.tsx` keeps driving it
  through a **fabricated** 403 on `/api/transfers`, which its header now says out loud. Whether to
  retire that half of the interceptor is a separate decision, deliberately not taken here;
  `a transfer against the ALIGNED mock never triggers step-up` guards the new truth meanwhile.
- **The lockout is now reachable from three paths** (verify-pin, withdraw, transfer) and they share
  one counter. That is intended — it is what stops a transfer being an uncounted PIN oracle — but it
  means a user locked out by transfers cannot reveal an account number either.
- **Not addressed here:** account deletion still has no PIN check anywhere (C.1), and first-time PIN
  enrolment still needs only a session (the live residual noted in ADR-0040).
