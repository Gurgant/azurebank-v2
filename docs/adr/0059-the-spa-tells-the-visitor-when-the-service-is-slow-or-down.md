# ADR-0059: The SPA tells the visitor when the service is slow or down

**Status:** Accepted · **Date:** 2026-10-01 · **Amended:** 2026-10-01 (decisions 3 and 9: ADR-0009,
ADR-0022) · **Decision Makers:** Vladislav Aleshaev · The client half of ADR-0058. **Amends**
ADR-0022 decision 6, ADR-0057 §8 and the Consequences of ADR-0058.

## Context

ADR-0058 makes the API answer an outage with a 503 that names a wait (`retryAfterSeconds`) and, on a
money send, says whether anything changed (`applied`). Before this decision the SPA showed none of
it: a dashboard reloaded during an outage showed a spinner, then skeletons, for up to 119 s and no
message. RTK's `retry()` is never handed the error, so it could not wait what the answer asks; no
request had a limit of its own; a failed session check read as a sign-out. A money send's key lives
only in the page (ADR-0022), so a visitor told the wrong thing sends again with a new one.

## Decision

1. **Every request gives up at 65 s** (`REQUEST_TIMEOUT_MS`, `problemBaseQuery.ts`), reads and
   writes alike, because the BFF's worst answer is 5 + 55 = 60 s (ADR-0058) and an abort at exactly
   60 would race a 503 that says `applied: false`. The abort is worded as the outage, and a money
   send keeps its key on it (ADR-0022 decision 2).
2. **A read is retried once, after the wait the answer names, within 120 s of its first attempt**
   (`READ_BUDGET_MS`), because one retry that honours the API's 10 s brings back a database gone for
   about a minute with no error shown; **a write is never retried**, because sent again it can spend
   twice (ADR-0022). Retried: a failed connection (not the SPA's own 65 s abort, which would only
   double the wait), any 503, a 502 or 504 whose body is JSON or empty. The wait is the body's
   `retryAfterSeconds`, else `Retry-After` (RFC 9110), else 5 s after a gateway's answer and 1 s
   after a failed connection, plus up to a fifth at random, to spread one outage's readers.
3. **A 503 reads as the SPA's own sentence, whoever wrote it** (`toApiProblem`: `detail` becomes
   `SERVICE_UNAVAILABLE`, a 503 that is not JSON is `HTTP_503`, a failed fetch reads
   `CONNECTION_FAILED`), because the servers' sentences promise "shortly", a time nobody measured,
   and name a session that is fine. `isServiceOutage`, a 503 or the 65 s abort, is asked before any
   `NETWORK` branch. An `HTTP_503` does not slide the SPA's copy of the inactivity clock: the BFF
   never saw that request. `applied` is read only on the four money sends and never decides the key:
   `false`, on a 503, changes the words (decision 7); `true`, on a 409 `IDEMPOTENCY_RESULT_UNKNOWN`
   only (ADR-0009), offers no send: "Your deposit went through, but we couldn't show its receipt."
4. **A wait says nothing for 5 s, "Taking longer than usual…" from 5 s, and "Still trying…" from
   20 s** (`useWaitPhase`), because below 5 s a hint would flicker on most loads and teach people
   to ignore it. The session check at start-up waits 6 s: the BFF answers it from its cache at 5 s.
   A money send holding its key says "Still trying… Keep this page open.": a reload loses the key.
5. **Each host shows its own hint, under the control or spinner that started the wait**
   (`WaitHint`), because a Fluent modal hides everything outside it from assistive technology, so
   one hint for the app would be silent in the dialogs where a money send waits. Its `role="status"`
   is in the page, empty, from the first instant, because a polite region has to exist before its
   text changes. A read that said something says "Loaded.", unseen, when it loads (WCAG 4.1.3).
6. **Only a read can be stopped, from 20 s; its error bar follows the wait, and focus comes back to
   where the visitor can act.** A write is never given up on, because stopping it does not stop the
   server and, on a money send, drops the one key that can check it. The bar hides while its read
   reruns (`readWait`) and sits in one `role="alert"` per page, empty from the start (`AlertSlot`),
   because the APG, MDN and W3C's ARIA19 differ on whether an alert added already filled is read.
   After Stop or Retry `useWaitLanding` focuses the bar's Retry, unless the visitor moved focus.
7. **One sentence for the outage, and other words only where the visitor's next step differs:
   whether money moved, whether a change was saved, whether pressing the same button again is
   safe** (`problemMessages.ts`). Every 503 and the 65 s abort: "The service is temporarily
   unavailable. Please try again later." `applied: false`: "…, and nothing was changed." A PIN step
   that met an outage: "…, and no money was moved.", because the PIN attempt may have been counted
   (ADR-0058). A send or a keyless change that may have landed: "…we can't tell yet whether…".
8. **"Retrying won't charge you twice." stands only beside a control that re-sends the same key,
   and never during a wait**, because while a send is pending the only retry possible is a reload
   or a new tab, which sends a new key. Never on a deposit, where nothing is charged. The in-flight
   bar does not say "check your history", because the first request can still commit (ADR-0058).
9. **A money send rejected with no HTTP status asks for a check before any new key, and so do a 409
   whose body names no code and an edit while a key is held**, because each may have landed
   (`useIdempotentMutation` drops the key and latches `verifyRequired`, ADR-0022 decision 4). On a
   deposit the 65 s abort keeps the key, so an amount changed after it could otherwise land twice.
10. **A 5xx, a lost connection or no answer never signs anyone out.** At start-up only a 4xx means
    signed out; anything else shows the page "Temporarily unavailable" with "Try again". A "Sign
    out now" that fails with anything but a 401 ends nothing, because the BFF ends a session
    without the API, so the cookie is most likely alive. The exception: at 0:00 of the countdown
    the session ends as expired even if the check fails, so that no account data stays on screen.
11. **A sign-out that fails says the visitor is still signed in, and the expiry dialog opens on
    staying.** "We couldn't sign you out. You're still signed in.", because a visitor who leaves a
    shared computer believing they signed out leaves it signed in. On inactivity "Stay signed in"
    has focus, so Space or Enter keeps the session (HMRC's timeout pattern; WCAG 2.2.1).

## Rejected

- Rejected: RTK's `retry()`, tuned, because its back-off never sees the error or the wait it names.
- Rejected: timing the hint on the RTK request, because the time a PIN dialog is open would count.
- Rejected: reserving the hint's height, because every fast load would then move the page.
- Rejected: disabling Retry while its read runs again, because a second failure is not announced.
- Rejected: keeping the key on a rejection with no status, because it replays the same answer.
- Rejected: telling the visitor how long to wait, because the servers' number is for machines.

## Consequences

- No request waits for ever: a write ends by 65 s and a read by about 120 s. An outage reads the
  same on every surface and is never a sign-out. No backend behaviour changes.
- Not covered: the key dies with the page. A reload or a new tab during a stuck send sends a new key
  and can pay twice (the FCA's 2022 Final Notice to TSB, ¶4.24(e)); the browser only asks first.
- Not covered: nothing answers "did key X land?" without sending it again (no read-only status by
  key is built), and a commit whose answer was lost answers `IDEMPOTENCY_IN_FLIGHT` for two minutes.
- Not covered: opening an account carries no key, so a second attempt can open a second account.
- Not covered: nothing bounds retries across visitors (one read can reach the database ten times
  per operation, ADR-0058), and each page of an infinite query has its own 120 s.

## Revisit when

- Response times are measured (p50, p99): the 5 s and 20 s, set from no measurement, move with them.
- `BackendApi:TimeoutSeconds` or the renewal's wait grows: the 65 s and 120 s are derived again.
- Retries pile up in a drill or under real traffic: a retry budget shared by the process.

## Validation

- Tests: `outage.test.tsx`, `policies.test.tsx` and `timeoutChain.test.ts` in `frontend/src/api`,
  `WaitHint.test.tsx`; in `frontend/e2e`, `slowService.spec.ts` and `wentThrough.spec.ts`.
- Measured on the compose stack on 2026-10-01, in headless Chromium. Database stopped 60 s: each
  dashboard read was retried once and loaded at 65 s, no error; hung 60 s instead, a bar at 45 s. A
  payment committed with its answer lost (24, that day and the next): the same key after the stale
  age got the 409 with `applied: true`, the went-through view, focus on its sentence, one movement.
- Heard with NVDA 2026.2 and Chromium on Windows, against the mock handlers: the 22 expected texts
  of the waits, the outage and the failed sign-out; on the went-through view the sentence once in
  each of the four flows, and the title only as a dialog's name, not as a transfer page's heading.
- Not heard: the transfer pages' outage bars, `applied: false`, a failed PIN check, the start-up
  page, the PIN dialog, sign-in, the check view (it lands no focus); JAWS, Narrator, VoiceOver,
  TalkBack. Not measured: anything on Azure, any browser but Chromium, a reload during a send.

## Related

ADR-0009, ADR-0022, ADR-0057, ADR-0058.
