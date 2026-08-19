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
it: **after the ownership check, before the funds check, and before any balance, ledger row or
other transfer mutation.** A wrong PIN therefore moves no funds. It is not write-free in the
absolute sense, and the distinction matters: it goes through `IPinVerifier`, which records the
failed attempt and can set the lockout ([ADR-0010](0010-pin-attempt-limiting.md)) — that write is
the point, since without it a transfer would be an uncounted brute-force oracle. `PinService` keeps
its own DbContext scope, so that bookkeeping neither rides this request's transaction nor finalises
its idempotency record.

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

The last row records an **order**, not just an outcome: idempotency is **middleware**
(`app.UseIdempotency()`), so it runs before MVC is entered at all — and therefore before model
binding, the controller and the PIN check. The mock reproduces that order, and this is where it was
measured rather than assumed.

_Corrected 2026-08-16 (ADR-0042): this paragraph said "action filter". The outcome is unchanged, but
the distinction is load-bearing for ADR-0042 — a filter would run inside MVC after model binding,
whereas middleware short-circuits a replay before any of it._

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

> **Amended 2026-08-17.** `SessionRequiredPaths` is gone; the check is now deny-by-default and the
> set that remains, `SessionlessPostPaths`, names the two paths that may be reached WITHOUT a session.
> The reasoning above is unchanged and is now the general rule rather than a rule about transfers.
>
> The allowlist was silently incomplete, which is the defect an allowlist has by construction: it
> named two paths and could not notice the ones nobody added. Both authorisation mint endpoints
> (ADR-0042), deposit, withdraw, PIN binding and account creation had all fallen through it.
>
> Measured on the running stack before the change, every one of those already answered 401, and the
> BFF's 401 and the API's are byte-identical apart from `traceId` — so this changed no response a
> client can observe, only which process decided it. What it buys is that the refusal no longer
> depends on `BearerTokenTransformProvider` continuing to clear the inbound `Authorization` header;
> the paragraph above is precisely the argument for not resting a layer on another file's
> correctness, and it applied to two paths while six went without.
>
> Deliberately still uncovered: the check is POST-only, so `PATCH /api/accounts/{id}`,
> `PATCH /api/accounts/{id}/set-primary`, `DELETE /api/accounts/{id}` and
> `PATCH /api/users/me/azuretag` fall outside it. Reaching them is a matter of widening the METHOD
> condition, nothing more — the gate matches on the `/api/` prefix, so a parameterised path is
> already covered the moment its method is.
>
> It is a separate decision because of the exemption side, not the matching side. `SessionlessPostPaths`
> is a POST-shaped set: login and register are POST, so today no exemption has to name a method. Widen
> the gate to every method and the set has to become per-method, or the first sessionless GET anyone
> adds is refused by a list that cannot express it. Widening to ALL methods also swallows the reads,
> where `/full-number` already has its own rule at a different level. So it is one decision with two
> halves, and only the POST half is made here.

> **Amended 2026-08-19.** `SessionlessPostPaths` is gone too, and the gate is now unconditional for
> every POST under `/api/`. The two paths it exempted — `/api/auth/login` and `/api/auth/register` —
> are answered **404** by the same branch that already handles `/api/auth/refresh`, as if the routes
> did not exist.
>
> The exemption rested on "they are how a caller obtains the session everything else requires". That
> was true of the shape and false of this deployment: the SPA never calls either. It signs in through
> the BFF's own `/bff/auth/login`, which reaches the API with `IHttpClientFactory` server-side and
> keeps the token there. The proxied pair had no legitimate caller at all.
>
> And unlike the 2026-08-17 amendment, this one was not defence in depth. Measured end to end with no
> session cookie: `POST /bff/auth/register` → 201, then `POST /api/auth/login` → **200 carrying a
> 392-character API JWT and a refresh token**, and that token sent straight to the API answered
> `GET /api/accounts` → **200**. The BFF was issuing the credential it exists to withhold. Only
> `BearerTokenTransformProvider` clearing the inbound header kept the same token from working back
> THROUGH the BFF — a defence sitting on a different path from the one handing it out.
> `POST /api/auth/register` answered 201 to the same sessionless caller.
>
> The two YARP routes stay in `appsettings.json` deliberately. They carry `RateLimiterPolicy "auth"`,
> and the table also holds a catch-all `/api/{**catch-all}` with no policy: deleting the dedicated
> pair would drop the path onto that catch-all, so a future weakening of this middleware would proxy
> login **unrate-limited**. Keeping them costs nothing behind the 404 and keeps the fallback no worse
> than today.
>
> The credential-guessing surface is unaffected: `/bff/auth/login`, `/bff/auth/register` and
> `/bff/auth/reauthenticate` all carry `[EnableRateLimiting(RateLimitPolicies.Auth)]` — the same
> policy the proxied routes had.

> **Amended 2026-08-19.** The method condition is gone: the gate now requires a session for EVERY
> proxied request under `/api/`, not only POSTs.
>
> The 2026-08-17 amendment above deferred exactly this, and gave a concrete reason —
> `SessionlessPostPaths` was a POST-shaped set, so widening the gate would have forced it to become
> per-method, or the first sessionless GET anyone added would be refused by a list that could not
> express it. **That reason stopped existing on 2026-08-19**, when the set was deleted outright
> rather than emptied. There is nothing left to make per-method, so the blocker dissolved instead of
> being argued away.
>
> What it closes: no YARP route carries a `Methods` clause, so every GET, PATCH and DELETE under
> `/api/` was forwarded sessionless and refused only by the API's own `[Authorize]` — with
> `FetchMetadataMiddleware` returning false for GET/HEAD and false again for a request with no
> `Sec-Fetch-Site`, a non-browser caller met nothing on the way. Never a data leak, because
> `BearerTokenTransformProvider` clears the inbound header, but it is the shape `AuthLevelMiddleware`
> itself calls unacceptable: a gate whose correctness depends on a different file getting something
> right. Measured after the change, sessionless: `GET /api/accounts`, `PATCH /api/users/me/azuretag`
> and `DELETE /api/accounts/{id}` all answer **401** at the BFF with the API's own
> `AUTH_TOKEN_MISSING` body, so no client can observe the move. With a session, `/bff/auth/register`
> and `/bff/auth/login` answer 201 and 200 and the proxied `GET /api/accounts` and
> `GET /api/transactions` answer 200 — naming the two prefixes apart deliberately, because the
> proxied `/api/auth/login` and `/api/auth/register` answer 404 **even with a valid session**:
> `BlockedProxiedAuthPaths` is matched before the session is ever read.
>
> Reads are swallowed too, and that was the other half of the deferral. It is fine: `/full-number`
> keeps its own PIN rule at a different level, and needing a session first is strictly weaker than
> needing a verified PIN.
>
> The drift sweep moved with it. It filtered the spec on `post`, which made it shaped like the rule
> it checked — so it was blind to precisely the surface that had no gate. It now enumerates every
> verb and substitutes templated paths, and was falsified by disabling the gate: eleven tests fail,
> naming `GET /api/accounts` among them.

`/full-number` keeps the session model (decision D3a). It is a GET with no body, no amount and no
payee — there is nothing for an in-band credential to be **bound to**. Two questions, two mechanisms,
on purpose.

> **Correction (review of this PR).** An earlier draft of this paragraph justified that with
> "PSD2 does not treat it as an SCA trigger at all: Art. 97(1)'s list is exhaustive, and Art. 4(32)
> says an account number is not sensitive payment data." Both halves were overstated. Art. 4(32)
> excludes the account owner's name and account number from *sensitive payment data* **for the
> activities of payment-initiation and account-information service providers** — it is not a general
> exclusion, and this app is neither. And Art. 97(1)(c) is a catch-all — "any action through a remote
> channel which may imply a risk of payment fraud or other abuses" — so the list is not exhaustive in
> the way the sentence claimed. Keeping the session model here is a PRODUCT decision about a request
> with nothing to bind to; it is **not** a finding that the regulation is silent. Anything stronger
> needs jurisdiction-specific legal review, which nobody on this repo has done.

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
  one counter. That is intended — it is what stops a transfer being an uncounted PIN oracle.

  An earlier draft added "so a user locked out by transfers cannot reveal an account number either."
  **That is false, and the code says so.** The counter lives in the API (`PinService`); the reveal
  gate reads `UserSession.AuthLevel` in the BFF (`AuthLevelMiddleware` → `SessionService`), and the
  API holds no reference to `ISessionService` or to any session at all. `SessionService` drops an
  elevated session back to level 1 only when its own PIN-verification window lapses
  (`IsPinVerificationValid`, `PinValidityMinutes`) — never in response to a lockout. So a session
  that was elevated **before** the lockout keeps revealing account numbers until that window runs
  out. Closing it needs a cross-process signal that does not exist today; recorded here rather than
  invented.

  **What is actually exposed, and when to revisit.** The window is bounded by
  `SecurityOptions.PinValidityMinutes` — 5 by default, 10 in dev — after which `GetAuthLevel` drops
  the session unconditionally. And after this ADR the level-2 gate protects exactly one thing, an
  owner-checked read-only GET. No money moves: transfers and withdraw both verify in-band at the API
  now, so the lockout bites them immediately whatever the session says. That is why a per-request
  cross-process check is not worth its cost today.

  **Reopen this if either becomes true:** (a) the level-2 gate starts protecting anything beyond a
  read. Deliberately stated as a PROPERTY rather than as a list of mechanisms: `AuthLevelMiddleware`
  has **two independent branches** — an exact-path set (`PinRequiredPaths`, empty today) and a
  `PinRequiredPrefixes` × `PinRequiredSuffixes` pair, which is what actually gates `/full-number`.
  A trigger naming only the set would miss a level-2 route added through the other branch, and would
  miss a third branch entirely. The condition is `RequiresPinVerification` returning true for a new
  operation, whichever rule decides it. Or (b) the session store becomes shared or user-indexed — the
  Redis move `InMemoryTokenStore` already anticipates — because per-user revocation is then cheap,
  and the response-observation design that is inadequate today becomes a real fix. Until then, note
  that observing a 429 on the locked-out user's OWN session would close only that case and leave the
  stolen-second-session one open, which is worse than leaving it visibly open: it would let someone
  write "closed" over a hole that is not.

### What review round 1 changed

Three of these were real defects in the first cut of this ADR's implementation, and all three are
worth recording because none was caught by the gates that were run:

- **The PIN recovery branches were dead code.** `run()` wrote the failure into `useState` and the
  awaiting handler read `wizard.lastErrorCode` — a value captured in the render that called it, so
  always the PREVIOUS attempt's code. A wrong PIN neither cleared the boxes nor showed a lock.
  `lastProblem` is a ref now, and `transfer-pin-recovery.test.tsx` fails on all three branches
  without it. The whole PIN failure surface had no test at all; that is why it shipped.
- **The PIN lock never expired.** Both pages stored a duration and never touched it again, so the
  send controls stayed dead for the life of the page. This is a REGRESSION, not inherited: the flow
  it replaced put a locked-out user in front of the root step-up modal, whose Cancel stays enabled —
  one click, form and verified recipient intact. The new step offered only a disabled Send and a
  Back that did not clear the lock. (`WithdrawDialog` has the same gap and is left as-is here; a
  shared fix is tracked separately.)
- **The mock grew a second PIN path.** `transferPinGate` + `checkPinInBand` were wired into the two
  transfer routes while withdraw kept its own inline copy — so the same file described two different
  PIN behaviours, and the shape hole survived on the endpoint that has carried an in-band PIN
  longest. Withdraw now goes through the same two functions.

### What review round 2 changed

Round 1 fixed the behaviour; round 2 found that the RECORD of it was still imprecise in four places,
and one of those imprecisions hid a real fidelity bug.

- **A non-string `pin` was accepted by the mock and refused by the API.** The gate coerced with
  `String(pin)`, and `String(123456)` is `"123456"` — which matches the six-digit rule. So a numeric
  pin passed here and would have failed only in production. The API fails the *conversion* before
  any annotation runs, and keys the answer by JSON PATH: `{"$.pin":["The JSON value could not be
  converted to System.String…"]}`. The gate is now split along that seam — a bind failure ABORTS
  and can never carry a second field; annotation failures are collected.
- **One model-state pass, not two early exits.** `[MoneyRange]` and `[Pin]` are evaluated together,
  so `amount: -5, pin: "12"` names BOTH fields. The mock returned from its amount check first, so a
  form would have highlighted one field where the API highlights two.
- **"before anything is written" was false**, and contradicted the next sentence of this ADR:
  `IPinVerifier` records the failed attempt and can set the lockout. That write is the point. The
  claim is now scoped to balances, ledger rows and transfer mutations.
- **Evidence attributed to a request that was never made.** `Transfer_WithNoPinField…` posts to
  `/api/transfers` but pasted the *internal* DTO's deserialisation message. Re-measured against the
  endpoint the test actually calls. Minor in effect, and exactly the class of error this PR is about.

Also: the ADR-0020 correction had REWRITTEN the sentence it was correcting, against this repo's own
rule that an ADR keeps what it got wrong and annotates it. The original is restored with the note
beneath it.

Two review points were **declined**, with reasons:

- Documenting `PIN_REQUIRED` / `INVALID_PIN` / `PIN_LOCKED` in the OpenAPI response descriptions.
  Those strings come from `AuthorizationResponseTransformer` and `IdempotencyOperationTransformer`,
  which are applied to EVERY authorised / every idempotent operation. Adding PIN codes there would
  claim deposit can answer `PIN_REQUIRED` and that every endpoint can answer `INVALID_PIN`. Per-
  operation wording needs different machinery, and withdraw — the older in-band-PIN endpoint —
  carries byte-identical generic strings today.
- Re-dating this ADR. The file and the index row already agree, and the date is the day the work
  was done.

- **Not addressed here:** account deletion still has no PIN check anywhere (C.1), and first-time PIN
  enrolment still needs only a session (the live residual noted in ADR-0040).
