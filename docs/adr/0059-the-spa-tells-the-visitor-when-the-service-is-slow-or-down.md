# ADR-0059: The SPA tells the visitor when the service is slow or down

**Status:** Accepted · **Date:** 2026-10-01 · **Decision Makers:** Vladislav Aleshaev ·
**The client half of** [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) ·
**Amends** [ADR-0022](0022-client-money-mutation-protocol.md) decision 6 (an edit while a key is
held asks for a check instead of a new key), and
[ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) §8 and the
Consequences of ADR-0058 (a read's 503 is retried once, after the wait the answer names, and the
SPA reads `applied`)

**Where the code's citations point.** The code cites this record as "ADR-0059", with no section:
`problemBaseQuery.ts` for decisions 1 to 3, `useWaitPhase.ts`, `WaitHint.tsx`, `AlertSlot.tsx` and
the wait rules in `frontend/CONVENTIONS.md` for decisions 4 to 6, and `problemMessages.ts` for
decisions 7 and 8.

## Context

Measured on 2026-09-30 on the local compose stack, before this decision: headless Chromium through
the SPA the BFF serves, signed in, with the outage scripts ADR-0058 used, kept outside this
repository. One run per row.

| Outage, and what the visitor did | What the visitor got |
|---|---|
| Database stopped 60 s; the dashboard reloaded | 64.85 s with no message at any time: a spinner for 5.07 s, then skeletons over the words "Across 0 accounts" for 59.78 s. Each of the three reads got the API's 503 at 40.01 to 40.04 s, which asked for `Retry-After: 10`; the SPA retried each 0.36 to 0.63 s later, and those retries got 200 |
| Database paused 60 s; the dashboard reloaded | The accounts read spent its three attempts 4.8 s before the database answered again, and the whole page became "The service is temporarily unavailable. Try again shortly." with a Retry. It stayed after the database was back, until Retry was pressed, and nothing announced it |
| API paused 120 s; the dashboard reloaded | 119.19 s with no message at any time: a spinner for 5.09 s, then skeletons over "Across 0 accounts" until the data came |
| API paused 120 s; "Sign in" | An unlabelled spinner in the button for 55.03 s, then the same sentence |
| Database stopped 60 s; the sixth digit of a transfer's PIN | 40.0 s of disabled PIN boxes, with no spinner and no text; then the same sentence, with the boxes still full and no button to try again |
| Database stopped 60 s; a transfer's send | "Sending €7.31" for 45.03 s; then the same sentence above "We couldn't reach the bank. Your transfer may or may not have gone through — check again." and "Check again", which sent the same key: one debit |

In no run was anybody signed out, and no page said "session expired" or "An unexpected error
occurred"; a control run after the outages saw each of those two checks fire, on a real 401 and
on a 500 handed to the browser.

Read in the code as it was then:

- **The retry was RTK's `retry()`**: up to three attempts of a read on RTK's own back-off, which is
  handed the attempt number and never the error, so it could not wait what the answer asked for.
- **No request had a limit of its own.** Nothing in the SPA ended a request that got no answer.
- **The words were the servers'.** Every surface that shows `detail` showed the server's 503
  sentence: "Try again shortly." promises a time nobody measured, and the BFF's renewal 503 ("The
  session could not be renewed just now. Try again shortly.") names a session to a visitor whose
  session is fine. A 503 that was not JSON, such as an ingress page, was an unreadable answer and
  was never retried; a failed fetch printed the runtime's "TypeError: Failed to fetch".
- **`applied: false` never reached a page**: `problemBaseQuery` passes on an allow-list of members
  and `applied` was not on it, so a money send the API knew had changed nothing was worded like one
  that may have landed.

And at start-up any failure of the session check went to the sign-in page, and a failed "Sign out
now" in the expiry dialog ended as "Your session has expired.": both read as a sign-out that had
not happened.

## Decision

### How long the SPA waits, and when it asks again

1. **Every request gives up at 65 s** (`REQUEST_TIMEOUT_MS`, `fetchBaseQuery`'s `timeout`), reads
   and writes alike. The BFF's worst answer is 5 + 55 = 60 s: the session's renewal may take 5 s
   (`TokenRefresher.ForegroundWait`) before the request goes on to the API, which the BFF gives
   55 s (`BackendApi:TimeoutSeconds`, ADR-0058). The 5 s above that are the margin. The abort
   becomes `status: 'NETWORK'`, `errorCode: 'TIMEOUT_ERROR'`, worded as the outage (decision 7):
   the service did not answer, which is the outage seen from this side. A money send keeps its key
   on it, as on every `NETWORK` (ADR-0022 decision 2).
   - **Not 60:** an answer at exactly 5 + 55 would race the abort, and a lost 503 that said
     `applied: false` would turn into "we can't tell".
   - **Not longer:** after 60 s only a BFF that is stuck is still pending. Locally nothing else
     bounds that wait; on Azure the ingress cuts it at 240 s (ADR-0058).
   - `timeoutChain.test.ts` reads the 5 and the 55 from the files that set them, and fails when the
     abort comes before their sum plus 5 s, at or after 240 s, or when a read's budget (decision 2)
     is less than twice their sum.

2. **A read is retried once, after the wait the answer names, and only within two minutes. A
   write is never retried.**
   - Only a query, and only when the service could not answer: a failed connection (not the SPA's
     own 65 s abort: a second wait that long would only double the visitor's), a 503 whatever its
     body, or a 502 or 504 whose body is JSON or empty. An unreadable answer, a 502 or 504 that is
     not JSON among them (decision 3), a 500 and every 4xx, a 429 included, are answers.
   - The wait is `retryAfterSeconds`: the body's first, the `Retry-After` header's otherwise, as a
     number of seconds or as an HTTP-date (RFC 9110 allows both). A date can come in any of the
     three forms RFC 9110 has a recipient accept, all three GMT, the asctime one without saying so;
     the SPA reads them itself, because `Date.parse` reads that one in the visitor's zone. A date
     is rounded up to the whole second; a value that is neither names no wait. On the wire it is
     10 s for an outage (ADR-0058), 5 s for a refused service key and 1 to 15 s for a renewal that
     failed (ADR-0057 §4.5). The visitor is never told the number (ADR-0058: "a visitor is told no
     time").
   - When the answer names no wait: 5 s after a 502, 503 or 504, 1 s after a failed connection.
     Both servers put a wait on every 503 they send, so a gateway answer that names none comes
     from something in front of the BFF and says that what is behind it is down, and a second
     later it most likely still is; Microsoft's guidance for clients of its identity platform puts
     the first retry at least 5 s after such an answer. A failed connection may have been a blink,
     and a second finds out.
   - To the wait the SPA adds up to a fifth of it, at random, and never takes anything off. Every
     reader in one outage hears the same `Retry-After`, the Dashboard's three reads among them;
     without a spread they would all come back in the same second.
   - The retry is sent only if the first attempt's time, the wait with its spread and 5 s still
     fit in `READ_BUDGET_MS`, 120 s from the first attempt, and the retry's own abort is cut to
     what is left of it. A wait that does not fit means no retry. What is left is worked out before
     the wait, never after it: a phone can suspend a page in the background for minutes, and a
     limit counted after that could fall to zero, which RTK reads as no limit at all.
   - A read the visitor stops (decision 6) ends the wait before its retry at once, and sends
     nothing more.
   - Mutations are never retried (ADR-0022): a money send is sent again only by the visitor, with
     the same key.
   - One retry that honours the API's 10 s lets a database the API cannot reach at all for about a
     minute come back without the visitor seeing an error, and the 120 s, two of the BFF's worst
     answers, bound everything else. A hung database, measured, failed each attempt in less than
     40 s, so a hang of a minute can still put a bar on the page (Validation).

3. **A 503 reads as the SPA's own sentence, whoever wrote it.** `toApiProblem` sets the `detail`
   of every 503 to `SERVICE_UNAVAILABLE`: the API's, the BFF's, an empty one, and one that is not
   JSON at all, which is now `status: 503` with `errorCode: 'HTTP_503'`, read and retried as the
   outage (a 502 or 504 that is not JSON stays an unreadable answer). The title and the support
   code stay the server's, and a 500 keeps its own detail. A failed fetch reads
   `CONNECTION_FAILED` instead of the runtime's error. `isServiceOutage(problem)`, a 503 or the
   65 s abort, is exported, and every surface that words an outage asks it **before** its
   `NETWORK` branch, because the abort is `NETWORK` too.
   - `applied` is passed on to the caller, and ~~read only as `applied === false`~~, only on the four
     money sends: it changes the words, never the key (ADR-0058: "A client never drops the key on
     `applied: false`").
     *(2026-10-01: it is also read as `applied === true`, only on a money send's 409
     `IDEMPOTENCY_RESULT_UNKNOWN` (ADR-0009's note of this date). There it changes the words, the
     actions offered and what the page keeps in memory (the page's own copies of the PIN and of
     the held authorisation are cleared; RTK Query's mutation hooks keep their last arguments and
     answer, those two among them, until the page or the dialog unmounts, as they do after a
     success), never the key, which that code drops either way. The balances and the lists are
     read again on that code with or without it (ADR-0022 decision 4's note).)*
   - A 503 whose `errorCode` no server wrote (`HTTP_503`) does not slide the SPA's copy of the
     session's inactivity clock. Both servers put `SERVICE_UNAVAILABLE` on every 503 they send, so
     this one came from in front of the BFF, which never saw the request; counting it would move
     the expiry warning later, the unsafe direction.

### What a wait looks like

4. **A wait says nothing for 5 s, "Taking longer than usual…" from 5 s, and "Still trying…" from
   20 s** (`SLOW_AFTER_MS`, `STILL_TRYING_AFTER_MS`, `useWaitPhase`). Below 5 s a hint would
   flicker on most loads, and a sentence that appears and vanishes teaches people to ignore it.
   Neither number comes from measured response times yet; both live in one file, so a measurement
   moves them for every wait at once. Every wait starts from zero.
   - The session check at start-up says its first word at 6 s (`SESSION_CHECK_SLOW_AFTER_MS`).
     When the API is slow or down, the BFF answers that check from its cache once its own 5 s
     read-through ceiling is up, so a word due at 5 s raced that answer: measured in Chromium on
     2026-10-01, it flashed for 20 to 33 ms on each of five reloads during an outage, then the page
     came. With the word at 6 s, five reloads during an outage removed the region at 5.09 to 5.14 s
     with no word in it (Validation). `timeoutChain.test.ts` reads the ceiling from the BFF's code
     and holds the word a second past it.
   - On a money send (a transfer, a move between the visitor's own accounts, a deposit, a
     withdrawal) the 20 s words are "Still trying… Keep this page open." While it is pending the
     one unsafe thing left to the visitor is a reload: the key lives only in the page (ADR-0022
     decision 1), and a reload sends the money again with a new one. The words ask; nothing stops
     a reload (see the Consequences). Ratified on 2026-09-30. They need a key to protect: while a
     PIN step's authorisation runs before the send, and no key is held yet, the same wait says
     "Still trying…", and its words change when the send starts.
   - A read that said something and then loads says "Loaded." (decision 5). Ratified on
     2026-09-30.

5. **Each host shows its own hint, under the control or spinner that started the wait**
   (`WaitHint`).
   - Not one hint for the app: a Fluent modal hides everything outside it from assistive
     technology, so a page-level hint would be silent inside the dialogs where a money send waits;
     and a mutation shows its pending state beside its own control (`frontend/CONVENTIONS.md`).
   - Never inside a `role="alert"` or inside an element an `aria-describedby` points at, or its
     words are read as part of those.
   - The hint follows the host's own waiting flag, not the life of the RTK request, which on the
     reveal of a full account number includes the time the PIN dialog is open.
   - Its words sit in a `role="status"` of their own, atomic, not `RetryCountdown`'s timer. The
     region is in the page, empty, from the first instant of the wait, because a polite region has
     to exist before its text changes; it is gone once nothing is pending. Until it has words it
     is out of the page's flow and visually hidden, still read, so a wait that ends inside 5 s
     moves nothing on the page, and a wait that reaches its words moves what is below it once. The
     Dashboard's hint sits above its grid while the page loads.
   - A read whose content takes the wait's place, and whose wait said something, says "Loaded."
     in the same region when it loads, read and not shown, and the region goes 2 s later
     (`LOADED_KEPT_MS`; NVDA spoke it 0.06 s after it appeared, Validation). WCAG 4.1.3 names
     this case: a non-visible status message that the system is available. A load inside 5 s said
     nothing and says nothing at its end either, and a failure says nothing more, because its
     alert speaks.
     A recipient check that finds nobody counts as a failure here: its line says so.
     The hosts that do it: the Dashboard, Accounts, History (its first page and "Load more"), a
     transaction's details, a transfer page's first load of the accounts and the recipient check.
     Not the funds check on Continue nor the session check at start-up: their success moves the
     visitor on rather than putting something on the page.
   - A transfer's confirm, the PIN step's authorisation and then the send, is one wait with one
     hint from the sixth digit, so the hand-off between the two requests does not start it again.
   - The hosts: the Dashboard (one hint for its three reads), Accounts, History (its first page,
     and "Load more"), a transaction's details; on both transfer pages the first load of the
     accounts, the funds check on Continue and the confirm, and on the external one the recipient
     check; deposit, withdraw (its funds check, then its authorisation and send), closing an
     account, opening one, both renames and the PIN change; the step-up PIN dialog; the expiry
     dialog, while it signs in again or signs out; sign-in, registration and PIN set-up; and the
     session check at start-up.
   - No hint: the session probes nobody waits on; the reveal of a full account number, whose
     spinner names the wait and whose PIN dialog has its own; a refetch with data on the page,
     which stays; and "Set as primary" and "Log out", which have no pending state to put one in.

6. **Only a read can be stopped, from 20 s; its error bar follows the wait, and focus comes back
   to where the visitor can act.**
   - "Stop waiting" (Fluent's medium button, 32 px high; the small one is 24) follows the words,
     outside the region, on the Dashboard, Accounts, History's first page, a transaction's
     details, a transfer page's first load of the accounts and the recipient check. Not on the
     funds check, which fails open, so stopping it would only skip it; not on "Load more", whose
     loaded rows stay readable; not on the session check, which has nothing to show instead.
   - A write is never given up on: stopping it does not stop the server, it only hides the
     answer, and for a money send it would drop the one key that can check it.
   - Stop aborts the request running for that cache entry (`abortRunning`), and any wait before a
     retry with it. The entry is rejected with no status, so the page shows its usual "Could not
     load…" bar. The server may still finish the read; nothing is written.
   - A read's spinner and bar follow `readWait`: it is waiting on its first load and on a refetch
     after a failure, and it has failed only once nothing is fetching. RTK keeps the old error
     through a refetch and leaves `isLoading` false, so a bar shown on `error` stayed up, and was
     never announced again, through a Retry. Now the bar goes while the read runs again, the hint
     comes back, and a second failure puts a new bar on the page, announced again.
   - Each read page keeps one `role="alert"`, atomic, in the page from the start and empty until a
     read fails (`AlertSlot`), and its bar goes inside it: in on a failure, out while the read runs
     again, in again if that fails too. The APG says an alert added to the page already filled is
     announced by most screen readers; MDN says it generally is not, since nothing in it changed;
     W3C's technique ARIA19 keeps the container in the page from the start. A slot that is
     always there satisfies all three. The read pages, both transfer pages' accounts bar and the
     page of decision 10 at start-up have one; the bar inside is Fluent's `MessageBar`, whose root
     is a `group` and not a live region, so no live region is nested in another. The recipient check has its own under the handle, where its
     line has always been: it answers the visitor's Verify, and Verify empties it, so a second
     failure is a change again.
   - The Dashboard has one slot for its three bars, above its grid. An outage fails "this month"
     and "recent activity" together, and WAI-ARIA 1.2 lets a screen reader drop queued speech when
     an assertive change arrives, so two alerts raised in one render may be heard as one. One
     atomic alert reads both. The section bars therefore sit above the grid, not in their
     sections, which show their headings and no figures while they have failed.
   - `useWaitLanding` puts focus on the new bar's Retry (on Verify for the recipient check) when a
     wait the visitor stopped or retried is over, because the control they pressed has gone. It is
     armed only when Stop stopped something or Retry was pressed, it is spent when that request is
     over, and it moves focus only while nothing holds it, so a visitor who went on meanwhile
     keeps their place.
   - The Dashboard's accounts bar replaces the page, so its Retry reloads every read that failed
     behind it, and the page never shows "Welcome" or a zero balance while the accounts are on
     their way. A transfer page with no accounts yet holds its form until they load, so nothing
     can start beside that wait. With accounts on the page, reloading them shows no hint and no
     Stop: the funds check reads the same accounts, and a Stop there would skip it.

### What the visitor is told

7. **One sentence for the outage, and other words only where the visitor's next step differs:
   whether money moved, whether a change was saved, whether pressing the same button again is
   safe.** The sentences are `problemMessages.ts`'s, named for what they say.
   - Every 503, and the 65 s abort: "The service is temporarily unavailable. Please try again
     later." (`SERVICE_UNAVAILABLE`), on every surface that shows `detail`, in toasts, and in the
     step-up dialog, the re-authentication dialog and the recipient check, which had words of
     their own.
   - A money send answered `applied: false`: "…, and nothing was changed." Only that answer earns
     it; a 503 without it may have landed.
   - A PIN step's authorisation that failed on an outage: "…, and no money was moved." No send had
     started, but the PIN attempt may have been counted (ADR-0058 residual risk 2), so not
     "nothing was changed". When that authorisation gets no usable answer at all (an outage, a
     lost connection, an unreadable answer, a 5xx), both transfer pages empty the PIN boxes and
     put the caret in the first one: six digits are the retry, and nothing says the PIN was wrong.
   - Any other outage on a money send: on a transfer, the outage sentence and the resend bar
     (decision 8); on a deposit or a withdrawal, "…we can't tell yet whether your deposit went
     through. Tap Deposit again to check." and its withdrawal twin, since the same button re-sends
     the same key.
   - A change that carries no key (opening an account, both renames, changing or setting the PIN)
     may have landed, and a second attempt is a new request, not a check. The renames and the PIN
     say "we can't tell yet whether your change was saved". Opening an account says it cannot tell
     yet whether the account was opened and to check the accounts before trying again, and has the
     account list read again when the dialog closes, so that an account the database kept shows
     up. Not while the dialog is open: behind a modal the page is hidden from assistive
     technology, and a read that failed there would put its bar where nobody hears it. Closing an account and
     registering keep the plain sentence: a repeated close of a closed account answers
     `ACCOUNT_NOT_FOUND`, which the dialog treats as done, and the same registration sent again
     gets the neutral 409 (ADR-0058 residual risk 7).
   - The Dashboard's "Could not load this month." and "Could not load recent activity." add the
     outage sentence to their own, which names the section.
   - No sentence already in the app is reworded: new words are appended, or replace generic words
     only on an outage. "We couldn't reach the bank." stays on the transfer's unknown bar after a
     503, which from the visitor's side is what happened; the outage alert above it names the
     cause. A 500, a 502 or 504, the 4xx copy and the lock countdowns are unchanged.

8. **"Retrying won't charge you twice." only beside a control that re-sends the same key.** The
   transfer's resend bar in its three forms (still processing, with "Check again"; nothing
   changed, with "Try again"; can't tell, with "Check again"), withdraw's in-flight note and its
   two outage messages. Between the visitor's own accounts it reads "Retrying won't move the money
   twice." **Never during a wait**: while a send is pending nothing on the page can send it again,
   and the key lives only in the page's memory (ADR-0022 decision 1), so the only retry possible
   then, a reload or a new tab, sends a new key, and the promise would be false exactly there.
   Never on a deposit, where nothing is charged; never after `RESULT_UNKNOWN` or the check of
   decision 9, when the key is gone.
   - The plan this work was approved under, kept outside this repository, put this sentence in the
     20 s hint. It moved here for the reason above, ratified on 2026-09-30.
   - The in-flight bar does not say "check your history", which the plan asked for: while it is
     up the first request can still commit (up to 71 s after a commit starts, ADR-0058), and a
     visitor who finds nothing in the history and starts again pays twice; nor would the page let
     them leave while the key is live. Ratified on 2026-09-30.

9. **A money send rejected with no HTTP status asks for a check before any new key, and so does
   an edit while a key is held.**
   *(2026-10-01: and so does a 409 on a money send whose body names no code, which used to drop
   the key with no check: [ADR-0022](0022-client-money-mutation-protocol.md) decision 3's note of
   this date.)*
   - `problemBaseQuery` gives every answer and every transport failure a status, so what is left
     is a 2xx whose body failed its schema, where the server acted, or an abort. Either may have
     landed, which is what `RESULT_UNKNOWN` says, so `useIdempotentMutation` drops the key and
     latches `verifyRequired` as it does for that code (ADR-0022 decision 4). It used to drop the
     key quietly: the flow said "failed", and the next press sent a second key. A PIN step's
     authorisation with no status keeps its old words, since it moves no money.
   - Deposit: an edit while a key is retained latches the same check instead of making a new key,
     as withdraw has done since 2026-09-23. The 65 s abort keeps the key, so without this any slow
     deposit whose amount was then changed could land twice. A tap or a keystroke that leaves the
     form as it was sent is not an edit, and keeps the same-key check on offer.

10. **A 5xx, a lost connection or no answer never signs anyone out.**
    - At start-up the session check moves to signed out only on a 4xx. A lost connection, an
      unreadable answer, a 5xx, the 65 s abort or a rejection with no status leave the session
      undecided, and the visitor gets a page of its own: a `main` with the heading "Temporarily
      unavailable", the outage sentence in an alert, and "Try again", which takes focus back if
      the check fails again. The check and that page are one `main`, so the alert is there, empty,
      while the check runs. The page is titled "Temporarily unavailable", as the page for a route
      that failed is titled for itself, and the route's own title comes back when the service
      answers. A Back or Forward to another guarded route keeps that page, its title, and says
      "Temporarily unavailable page loaded": the route announcer writes the title and the
      announcement, and the page puts its own title in place of the route's while it is shown. A
      check RTK skipped because another was running decides nothing.
    - "Sign out now" in the expiry dialog that fails with anything but a 401 ends nothing and
      clears nothing: the dialog stays, says so and why (decision 11), focus goes back to the
      button, and the button works again. The BFF ends a session without the API, so a logout that
      failed most likely never ran and the cookie is alive. A failed revocation still never passes
      for a sign-out.
    - At 0:00 of the countdown, if the status check fails, the session still ends as expired: the
      countdown ended it, not the error, and keeping account data on screen past the session while
      the service is down would be worse. The plan said a 5xx never shows "session expired"; this
      exception was ratified on 2026-09-30.

11. **A sign-out that fails says the visitor is still signed in, and the expiry dialog opens on
    staying.**
    - Whenever a sign-out fails with anything but a 401 — "Sign out now" in the expiry dialog, and
      the sign-outs in the shell and in Settings — the visitor reads first "We couldn't sign you
      out. You're still signed in.", then why: in the expiry dialog the connection sentence for a
      failed connection or an unreadable answer and the outage sentence otherwise, the 65 s abort
      included; in the shell's and Settings' toast the problem's own words. A visitor who leaves a
      shared computer believing they signed out leaves it signed in. A 401 says the session was
      already gone, which is what a sign-out asks for. Ratified on 2026-09-30.
    - In the inactivity branch "Stay signed in" comes first, in the page and on screen. The dialog
      opens with focus on its first control, so Space or Enter, pressed by someone who meant to
      stay, keeps the session instead of ending it: one `/me`, no logout. HMRC's timeout pattern
      puts focus on its "Stay signed in" button, and WCAG 2.2.1 gives the space bar as the simple
      way to extend. At the cap the password field comes first, as before.
    - The dialog is described (`aria-describedby`) by its sentence and its countdown only, not by
      the content around them, which can hold a wait's words or a failure's alert: those speak for
      themselves.
    - "Sign out now" is disabled while it is pending, and a browser hands the focus of a control
      it disables to the page (measured in headless Chromium 151: focus went to `body` and stayed
      there when the button was enabled again). When the sign-out fails, `useWaitLanding` puts
      focus back on it.

## The numbers

| Constant (file) | Value | Why |
|---|---|---|
| `REQUEST_TIMEOUT_MS` (`problemBaseQuery.ts`) | 65 s | The BFF's worst answer, 5 + 55 = 60 s, and 5 s of margin; below the ingress's 240 s |
| `READ_BUDGET_MS` | 120 s | Two of the BFF's worst answers: a first attempt and its retry |
| The wait after a 502, 503 or 504 that names none | 5 s | Something in front of the BFF answered for a service that is down; both servers put a wait on every 503 they send |
| The wait after a failed connection | 1 s | A connection may have blinked |
| The spread added to a retry's wait | 0 to 20 % | Readers that heard the same wait do not all come back in the same second; never less than asked |
| The least a retry may be left | 5 s | Less could only time out |
| `SLOW_AFTER_MS`, `STILL_TRYING_AFTER_MS` (`useWaitPhase.ts`) | 5 s, 20 s | Not measured; one place to move them |
| `SESSION_CHECK_SLOW_AFTER_MS` (`useWaitPhase.ts`) | 6 s | The BFF's 5 s ceiling on the session check, and 1 s so that its answer from the cache is not raced (decision 4) |
| `LOADED_KEPT_MS` (`useWaitPhase.ts`) | 2 s | How long "Loaded." stays in the region before it goes; measured in the page at 2.008 to 2.012 s; NVDA spoke it 0.06 s after it appeared |

What a visitor would meet, reasoned from ADR-0058's numbers before any browser run. Every wait
says "Taking longer than usual…" at 5 s (the session check at start-up at 6 s) and "Still trying…"
at 20 s ("Still trying… Keep this page open." once a money send holds its key). A browser on the
compose stack has since measured the reads of the first, third and fourth rows, the money writes
of the first row, and of the third row only a sign-in and a step-up PIN check, not a money send;
each held (Validation). The second row and the last row's write were not measured, and a database
that hangs, where the measured first row's could not be reached at all, answers each attempt
sooner than the first row says.

| Outage | A read | A write |
|---|---|---|
| Database down about a minute (the API's 503 at 40 s, `Retry-After: 10`) | the retry at about 50 to 52 s, then the data, "Loaded." and no error | the flow's outage words at about 40 s, never retried |
| Database down longer | the outage sentence at about 40 + 10 to 12 + 40 = 90 to 92 s | the flow's outage words at about 40 s |
| API frozen (the BFF's 503 at 55 s) | the retry at about 65 to 67 s, cut where the 120 s end: the outage sentence at about 120 s | the flow's outage words at about 55 s |
| BFF stuck | the outage sentence at 65 s, no retry | the flow's outage words at 65 s |

## Rejected

- **RTK's `retry()`**, kept and tuned: its back-off never sees the error, so it cannot wait what
  the answer asks for.
- **A retry at exactly the wait asked for, and 1 s when none is named:** every reader of one
  outage would come back in the same second, and a gateway's 5xx would be asked again while what
  is behind it is most likely still down (decision 2).
- **Retrying a read that got no answer in 65 s:** it doubles the visitor's wait for a service that
  has just shown it does not answer.
- **Retrying a write automatically,** keyed or not: ADR-0022's reason, a possible single spend made
  a probable double one; and a write without a key has nothing that makes a second one safe.
- **One hint for the whole app,** and **timing the RTK request instead of the host's wait:** the
  first is silent inside a modal, the second counts the time a PIN dialog is open as waiting.
- **Reserving the hint's height:** the region is there for every wait, so a reserved height would
  move the page on every fast load.
- **"Stop waiting" on a write:** decision 6.
- **The no-double-charge sentence in the hint,** and **"check your history" on the in-flight
  bar:** decision 8.
- **Disabling Retry while its read runs again,** rather than hiding the bar: the bar would stay
  mounted through the wait, so a second failure would still not be announced.
- **A bar that is its own alert, mounted already filled:** whether it is read depends on the
  screen reader (decision 6).
- **Keeping the key on a rejection with no status:** when the server had answered a 2xx whose body
  failed its schema, the same key replays that same answer, and the visitor is held on "failed"
  with no way on.
- **Telling the visitor how long to wait:** the servers' number is for machines (ADR-0058).

## Consequences

**Positive**

- No request waits for ever, and a wait longer than 5 s says so, inside the dialog it belongs to
  when there is one; decision 5 names the few that have no hint.
- An outage reads the same on every surface and never names a session or a time; a money send says
  whether money moved, or how to find out with the same key.
- A read's retry waits what the server asked, so a database the API cannot reach for about a
  minute comes back without an error (a hung one can still show a bar), and a second failure is
  announced again.
- An outage is never a sign-out, at start-up or from the expiry dialog's "Sign out now", and a
  sign-out that failed says the visitor is still signed in.
- A read's failure is a change of an alert that was already on the page; a wait that said it was
  slow says when it has loaded.

**Negative, and what is left open**

- **The key dies with the page** (ADR-0022 decision 1). A visitor who reloads during a stuck send,
  or opens a new tab, sends a new key, and if the first request lands they pay twice: the outcome
  the FCA's 2022 Final Notice to TSB records (¶4.24(e)), payments made twice after error messages
  that followed successful ones. Every money send asks the browser to ask before a reload or a
  tab close while it holds a key (`useIdempotentMutation`), and from 20 s the wait asks to keep
  the page open; nothing stops a reload. A warning at the review step about the same payment
  made a few minutes before, from the server's data, is not built.
- **Nothing answers "did key X land?" without sending it again:** "Check again" re-sends the same
  key, which executes if nothing was recorded. A read-only status by key is not built.
- ~~**`RESULT_UNKNOWN` also covers a commit that is proven** (a stale `Executed` claim, ADR-0009),
  and the check view still offers "It didn't go through — start over" for it. Saying that it went
  through needs the server to say so first: a small change of its own, after this one.~~
  *(Closed 2026-10-01 for the stale claim. Before, measured that day on `5b50681`, on the compose
  stack in headless Chromium: five payments were committed while their answer was neither stored
  nor delivered (a deposit, a withdrawal, a move between the visitor's own accounts and two
  transfers). The same key, sent once the record was 126.18 to 126.22 s old, got 409
  `IDEMPOTENCY_RESULT_UNKNOWN` with no `applied` member each time, and the page said "We
  couldn't confirm your transfer" (or deposit, or withdrawal) over "Check recent transactions"
  and "It didn't go through — start over" ("— try again" in the two dialogs). On the deposit
  that second button was pressed, and then "Deposit €8.47" again: a new key, a 201, and a second
  deposit of €8.47 in the database. Since this date that answer says `applied: true` (ADR-0009),
  and the flow draws the receipt's title, "Your deposit went through, but we couldn't show its
  receipt." (or transfer, or withdrawal), "View History", and nothing that sends; the tests and
  the measurements are under Validation. **Not closed:** for the first two minutes the same
  commit is answered `IDEMPOTENCY_IN_FLIGHT` (measured on a transfer, at 15.5 and 60.6 s), and
  the visitor learns that it went through only by checking again after that. In that run the
  transfer page said "Still processing — check again to see whether it went through."; the page
  for a move between the visitor's own accounts has the same words, and the two dialogs have
  "Still processing — tap Deposit again to check." and its Withdraw twin (those three read in
  the code, not measured in that run). None of the four says how long that is.)*
- **Opening an account carries no key** (ADR-0058 residual risk 2): an outage can hide an account
  that was opened, which is why its words say to check first and the list is read again; a second
  attempt can still open a second, empty account. An optional key on that request is not built.
- **Nothing bounds retries across visitors.** Each visitor's read is sent at most twice, and the
  API tries each database operation up to five times (ADR-0058's four EF retries), so one read can
  reach the database up to ten times per operation it makes, and a write up to five, before
  anybody presses anything. With ADR-0058's NeverBlock (its residual risk 5) a busy outage
  multiplies the logins the database receives. The spread of decision 2 keeps readers from coming
  back in step; it bounds nothing. A budget shared by the process is not built.
- **Each page of an infinite query has its own 120 s.** RTK refetches the cached pages one after
  another, and no endpoint turns that off, so a History refetch of k pages during an outage can
  take k × 120 s. Accepted: during such a refetch the page shows the rows it has, and a first-page
  Retry has one page.
- **RTK's `timeout` leaves a 65 s timer behind every request,** which later aborts the controller
  of a request long finished (harmless). The timeout covers reading the body too: a body still
  streaming at 65 s surfaces as an unreadable answer, not as the outage.
- **Two small things are read twice or side by side:** after a failed authorisation, focusing the
  first PIN box reads the alert again through the PIN group's description (as a wrong PIN already
  did); and in withdraw, after a PIN lock and Back, a slow funds check can run beside the lock
  countdown, two polite regions at once.
- **Locally, over HTTP/1.1,** Chrome queues past six connections to one host and RTK's clock
  starts before the socket, so a second tab in an outage can make a queued write give up early.
  The key is kept. Reasoned, not measured.
- The 5 s and 20 s are not from measured response times.

**Neutral**

- The servers' own sentences stay as they are, for other clients; no backend behaviour changes.
  Two backend comments that described the SPA and the tripwire's failed write were corrected.

## Validation

Test files are under `frontend/src/`, browser specs under `frontend/e2e/`.

- `api/outage.test.tsx`: a 503 from the API, from the BFF's renewal and without JSON reads as the
  SPA's sentence; a failed fetch never shows the runtime's error; a 500 keeps its detail;
  `applied: false` reaches the caller; a request with no answer ends at 65 s and is not retried.
- `api/policies.test.tsx`: a read's 503 is retried once after its `retryAfterSeconds`, a
  mutation's never; the 5 s default after a gateway's answer and the 1 s after a failed
  connection; the spread, never below the wait asked for, and the 120 s budget checked with it; a
  `Retry-After` date in each of its three forms, read as GMT and rounded up, and a value that is
  neither; the 5 s floor; a stop during the wait; a wait that ends late; a money send with no
  answer keeps its key; one rejected with no status asks for a check.
- `api/timeoutChain.test.ts`: the abort and the budget against the BFF's two settings, read from
  the files that set them.
- `components/feedback/WaitHint.test.tsx`: the phases and their restart, the empty and atomic
  region, Stop only on a read and at the medium size, a money send's words, "Loaded." and when it
  is not said, `readWait`, and when `useWaitLanding` moves focus.
- `pages/slow-reads.test.tsx`, `pages/transfer-outage.test.tsx`, `pages/slow-sign-in.test.tsx`,
  `components/dialogs/outage.test.tsx` and `features/auth/outage-session.test.tsx`: each host's
  hint, Stop and landing, "Loaded.", the alert that is there before a failure and the Dashboard's
  one alert, the money and keyless words, the start-up page and its title, the expiry dialog's
  focus and description, and the sign-outs that fail.
- `slowService.spec.ts`, and the slow-read scans in `accessibility.spec.ts` (light, dark and
  375 px), on the served build under its CSP with Playwright's clock.
- **In a browser on the compose stack**, on 2026-10-01, on this change as committed just before
  this section was written: headless Chromium 151 through the SPA the BFF serves, signed in, with
  outages made by `docker compose stop` or `pause`, or by an exclusive lock on `Transactions`,
  from a script kept outside this repository. One run per item unless the item names several;
  times are seconds after the visitor's action unless the item says otherwise. A stopped database
  could not be reached at all: the API logged SQL error 35, its name no longer resolving. For a
  send to meet an outage, the browser held it until the outage had begun and then let it go
  unchanged.
  - Database stopped 60 s, the dashboard reloaded: the session check got 200 in 4.99 s, and its
    region left the page at 5.14 s without a word. The dashboard said "Taking longer than usual…"
    at 10.15 s and "Still trying…" with "Stop waiting" at 25.15 s. Each of its three reads got the
    API's 503 after 40.00 to 40.01 s and was sent again 10.29 to 11.18 s later; the retries got
    200 at 65.05 to 65.12 s, and "Loaded." came at 65.11 s and went at 67.12 s. Two requests per
    read, and no error at any time.
  - The same outage, with "Stop waiting" pressed at 30.01 s: the three reads ended in the browser
    at 30.08 to 30.09 s, "Could not load your accounts." came at 30.07 s with focus on its Retry,
    and nothing was sent until that Retry, pressed after the database was back: three 200s and
    the data.
  - Database paused 60 s, the dashboard reloaded. A hung database answers sooner than a stopped
    one: the API answered the three reads 503 after 9.99, 19.98 and 29.98 s, and the accounts
    read's retry after 19.65 s, so the accounts bar, with a support code, came at 45.13 s. Focus
    stayed where it was, since nothing had been stopped or retried. Its Retry, pressed at 46.23 s,
    brought the hint back, "Taking longer than usual…" 5.07 s after the press, and then "Could not
    load recent activity." at 55.13 s, whose Retry was pressed at 56.28 s. Once the database was
    unpaused (the command issued at 59.00 s) the reads got 200, "Loaded." came at 59.33 s, and no
    error was left.
  - Database paused 150 s, Retry pressed about a second after each bar: seven failures, one after
    another, in the dashboard's one alert. The accounts bar came twice, at 74.11 and 134.11 s,
    each time with focus on its Retry, and after each of those Retries the hint's first word came
    5.03 s after the press. The five section bars all came before the accounts read had ended,
    and focus stayed where it was. Across both paused runs the API answered each failed attempt
    after 9.99 to 35.0 s.
  - API paused 150 s, the dashboard reloaded: the session check got 200 in 4.99 s, and its region
    left at 5.09 s without a word. The reads got the BFF's 503 after 54.98 to 55.01 s and were
    sent again 10.07 to 11.10 s later; the browser ended the retries at 125.11 to 125.13 s,
    120.00 s after each read's first attempt, and the outage sentence came, alone, at 125.10 s.
    Retry, once the API was back: the data.
  - BFF paused 90 s, "Accounts" clicked: one request, never answered, which the SPA ended after
    65.00 s; the outage sentence at 65.07 s, and no retry.
  - API paused, "Sign in" (150 s) and the step-up PIN dialog of a reveal (90 s): "Taking longer
    than usual…" at 5.04 and 5.03 s and "Still trying…" at 20.04 and 20.03 s, with no Stop; the
    BFF's 503 after 55.07 and 55.01 s, then the outage sentence. The sign-in issued no refresh
    token during the pause, and both worked once the API was back.
  - Database stopped 60 s, the PIN's sixth digit on a transfer, and "Withdraw" pressed with the
    PIN already entered on a withdrawal: "Taking longer
    than usual…" at 5.03 s, and "Still trying…" without "Keep this page open." at 20.02 and
    20.04 s, with no Stop. The PIN check got the API's 503 after 40.01 and 40.02 s, then "The
    service is temporarily unavailable, and no money was moved. Please try again later." No send
    left the browser during the outage. The transfer page emptied the PIN boxes and put focus in
    the first; withdraw kept the six digits. Afterwards each moved the money once.
  - Database stopped 60 s, a send that met it, on a transfer, a move between the visitor's own
    accounts, a withdrawal and two deposits: the hint's first word 5.02 to 5.07 s after the
    action, "Still trying… Keep this page open." 20.02 to 20.07 s after the action, in one region
    from the action to the answer, and the API's 503 without `applied` 40.01 to 40.02 s after the
    send was let go. Then:
    - the transfer: the outage sentence and "We couldn't reach the bank. Your transfer may or may
      not have gone through — check again. Retrying won't charge you twice." with "Check again",
      and between the visitor's own accounts the same with "Retrying won't move the money
      twice."; "Check again" sent the same key with the same authorisation: 201, one movement;
    - the withdrawal: "The service is temporarily unavailable, and we can't tell yet whether your
      withdrawal went through. Tap Withdraw again to check. Retrying won't charge you twice." in
      its dialog, which stayed; Withdraw sent the same key with the same authorisation: 201, one
      withdrawal;
    - the deposits: "…whether your deposit went through. Tap Deposit again to check.", with no
      "charge" anywhere on the screen; Deposit sent the same key: 201, one credit. In the other
      run the amount was edited instead: "We couldn't confirm your deposit", one request in all,
      and no credit.
  - An exclusive lock on `Transactions` for 60 s, a transfer's send let go into it: the API's 503
    with `applied: false` 30.07 s after the send was let go, then "The service is temporarily
    unavailable, and nothing was changed. Please try again later.", "Retrying won't charge you
    twice." and "Try again",
    which after the lock sent the same key with the same authorisation: 201, one movement.
  - Database stopped 60 s, the step-up PIN dialog of a reveal: its hint at 5.02 and 20.03 s, the
    API's 503 after 40.01 s and the outage sentence in the dialog's alert, never "Couldn't verify
    right now"; after the outage the PIN showed the number.
  - One transfer run was repeated, because its script, by a fault of its own, pressed "Check
    again" 60.27 s after the database was back instead of 3 s, 130.4 s after the authorisation was
    issued. The answer was 401, "This authorisation has expired."; the page said "For your
    security, your confirmation expired. Your transfer details are still here — enter your PIN
    again to confirm.", and the PIN sent the same key with a new authorisation: 201, one
    movement. The transfer of the row above is the repeat.
  - In none of these runs did the page say "Your session has expired", "An unexpected error
    occurred", "shortly", "We couldn't sign you out" or "Couldn't verify right now", or send the
    visitor to the sign-in page, and in each outage the BFF and the API logged no ended session
    and no 401. Control runs made each of those five checks fire: a session ended at the BFF, and
    three 500s and a 503 that the browser answered itself.
  - The runs measured only the precondition of an announcement, with a MutationObserver: the
    element was in the page, empty, before its text. Each of the 21 hints' first words came 5.000
    to 5.015 s after its region was put in the page empty, and the words changed in place after
    that. Each of the 14 texts of a read page's alert, two of them in the control runs, came into
    the alert while it was in the page and empty, for 0.055 to 120.01 s before; so did the 19
    route announcements, and the sign-out sentence in the toast region (0.265 s). The session
    check's region at start-up was in the page 20 times and never had a word. The other alerts, a
    money flow's, the resend bars, the sign-in page's and the step-up dialog's, 18 in all, were
    put in the page with their words in them, as decision 6 leaves them, and so was the amount
    field's "Minimum … is €0.01." alert, 10 times, for 1 to 3 ms while the script filled the
    amount.
  - A first pass earlier the same day measured the start-up word of decision 4. It also saw, once,
    the dashboard's one alert take two texts 5 ms apart, the recent-activity bar and then the
    accounts bar, when the 120 s ended its three reads together; this pass saw one. Nothing was
    changed for it.
- **Heard with a screen reader**, on 2026-10-01: NVDA 2026.2 with Chromium on Windows, its speech
  logged and matched to the moment each text appeared, against the development build answered by
  the repository's mock handlers, with the requests held or answered 503 by the browser. NVDA
  spoke all 22 expected texts, each 0.01 to 0.18 s after it appeared: "Taking longer than
  usual…", "Still trying…" and "Loaded." on a slow read; the outage sentence in a read's alert;
  "Stop waiting", then "Could not load your accounts." and focus on its Retry; "Still trying…
  Keep this page open." and the deposit's and the withdrawal's outage words, with "Retrying won't
  charge you twice."; "We couldn't sign you out. You're still signed in." from the sidebar and
  from the expiry dialog, whose title and countdown were read with focus on "Stay signed in", and
  focus back on "Sign out now" after it failed. Two things heard that are not this record's to
  change: a toast's title and body are spoken run together ("Service UnavailableWe couldn't…"),
  and a read's support code is spoken digit by digit. Not heard: the transfer pages' outage bars,
  `applied: false`, a failed PIN check, the start-up page, the PIN dialog and sign-in; JAWS,
  Narrator, VoiceOver on macOS and iOS, and TalkBack.
- **Added 2026-10-01: the went-through view** (decision 3's note, and the residual closed above).
  - Tests. The API's answer and its two arms: `IdempotencyServiceTests`,
    `ConcurrencyRetryIdempotentAttemptTests` and `IdempotencyEndpointTests`, and on SQL Server
    `RequestDeadlineSqlServerTests`, `TransferTransientRetrySqlServerTests` and
    `WithdrawalStepUpSqlServerTests`; the document, `PublishedErrorContractTests`; what the flag
    stands on, `ProvenCommitSourceTests` and the SQL Server tests ADR-0009's note names. The SPA,
    under `frontend/src`: `api/policies.test.tsx` (the flag read as the boolean `true` only, the
    latch, `resetIntent`, a 409 that names no code, the cache), `api/outage.test.tsx`,
    `hooks/useMoneyWizard.blocker.test.tsx`, `components/shared/WentThroughView.test.tsx`,
    `pages/transfer-went-through.test.tsx`, and `components/dialogs/deposit.test.tsx` and
    `withdraw.test.tsx`.
  - In a browser, `frontend/e2e/wentThrough.spec.ts`: Chromium on the build the BFF serves under
    its CSP, each send answered by the browser with the 409 and `applied: true`, so no money
    moved; the authorisations of the withdrawal and the transfer were minted for real. A deposit,
    a withdrawal and a transfer each showed the receipt's title and the sentence with focus on
    it, "didn't go through" nowhere on the page, "View History" one Tab away and leading to
    `/history`, and one key sent. The dialogs' buttons were Close and "View History"; the
    transfer page's, "View History" and "Done". A second press where "Deposit" had been, which
    with the view up is on the backdrop, left the dialog and its sentence on screen with focus
    on the sentence, and Escape then closed it. The whole browser suite, 44 tests with these,
    passed on the compose stack and on a stack run from source the way CI's job runs it.
  - Measured in that run, on the deposit dialog and on the transfer page, each in the light
    theme, in the dark theme and at 375 × 812: the sentence took 2 lines in all six, centred, and
    it and "View History" were whole in the viewport with no sideways scroll; axe, on the rules
    tagged WCAG 2.0 A and AA, 2.1 AA and 2.2 AA, reported 0 violations in each; the sentence's
    contrast, computed from its colours, was 13.34:1 on the page and 14.68:1 in the dialog in the
    light theme and at 375, and 15.45:1 and 12.2:1 in the dark theme. axe's own figure agreed on
    the page (13.33 and 15.45); in the dialog it could not decide the sentence's background,
    since it looks through the modal to the page behind.
  - The focus ring, in the same run: after a key press (Enter on Deposit; Enter on Withdraw; a
    transfer's sixth PIN digit, which is its send) the sentence had the app's ring, 2 px solid at
    a 2 px offset; after a click on Deposit or Withdraw it had none, with focus on it. On the
    transfer page the ring was whole in the viewport, at 375 too; in the dialog that was not
    measured.
  - Not heard: the sentence itself. Focus is on it when the view appears, in jsdom and in
    Chromium; no screen reader has been listened to on this view. Nor the check view, which
    lands no focus (on `5b50681`, measured, focus was on `body` in every one), and to which one
    more answer is routed since this date, a 409 that names no code; nor the view arriving while
    another dialog holds focus, or after the visitor moved focus during the wait, when the
    landing does not run; nor JAWS, Narrator, VoiceOver and TalkBack.
  - Not held by a test: that the page's own copies of the PIN and of the authorisation are let go
    when that answer arrives (decision 3's note). Nothing on the view shows them; read in the
    code.
  - On the compose stack, in headless Chromium at 1280 × 900, the sequence the "before" above
    measured was run on this change twice that day: a payment committed while its answer is
    neither stored nor delivered, then the page's own control pressed once the record is past
    the stale age. First on the change as it stood before the two fixes named below: a transfer,
    two deposits, a withdrawal, a move between the visitor's own accounts, a transfer with two
    early presses, and a double click on a deposit and on a transfer. Then on the change as it
    is, built again from its head: a transfer, a withdrawal, a deposit, and the double click on
    a deposit. Twelve payments in all.
    - The answer, all twelve times: the same key, sent when the record was 126.2 to 128.6 s
      old, got 409 `IDEMPOTENCY_RESULT_UNKNOWN` with `"applied":true` and the sentence ADR-0009's
      note quotes; eight members in the order type, title, status, detail, instance, errorCode,
      traceId, applied (the "before" had seven); `application/json; charset=utf-8` and
      `no-cache,no-store`; read at the browser, through the BFF. Once its `traceId` is replaced
      it is byte for byte the body `wentThrough.spec.ts` answers with. The two early presses, at
      15.6 and 61.1 s, got `IDEMPOTENCY_IN_FLIGHT` with no `applied`, and the page said "Still
      processing — check again to see whether it went through.", as in the "before".
    - The page: a transfer page showed `<h1>` "Transfer Complete", the sentence, and two controls
      on the whole page, "View History" and "Done"; a dialog was named "Deposit Complete" or
      "Withdrawal Complete" and held the sentence, Close and "View History", the only controls
      on the page neither disabled nor inert. No alert, no button matching "didn't go through",
      no PIN box. Focus was on the sentence, a `<p>` with `tabindex="-1"` in no alert, status or
      live region; in the "before" it was on `body`. Behind a dialog the balance was the
      database's (the "before" still showed the one from before the payment). "Done" and the
      navigation's "Home" led to the dashboard, whose balances equalled the database's; "View
      History" led to `/history` with one read of its first page, whose first row was the
      movement.
    - The money: one movement per payment in the database after the loss, after the 409 and at
      the end; one key in every send; no request to a money path after the view appeared. The
      deposit that ended as two deposits in the "before" ended as one.
    - The double click, and the first fix. In the first run the second press of a double click
      on "Deposit", 143 ms after the first, landed on the dialog's backdrop: the view is shorter
      than the form, so the place the button had been was outside the dialog. The dialog was
      gone at most 109 ms after its sentence had appeared, and the visitor was left on the
      dashboard with focus on `body`, beside the refreshed balance and the new row of "Recent
      activity", told nothing. On a transfer page the second press landed on the sentence and
      the view stayed. Since then a press on the backdrop closes nothing while that view is up
      and leaves focus where it was (`MoneyDialogShell`'s `keepOnOutsidePress`); the X and Escape
      close the dialog. In the second run the same double click, 178 ms apart with the second
      press at the same point, left the dialog "Deposit Complete" on screen with focus on the
      sentence: one request, one key, one deposit. In that run's other deposit, Close closed
      the dialog with no request, and the Deposit tile then opened a new, empty dialog whose
      submit was disabled. Not measured: a double click on "Withdraw", whose dialog has the
      same shell and its own test; and the success receipt, which is not changed.
    - The second fix is the API's: a retried attempt compares the request hash after its reload
      (ADR-0009's note). No browser run stages it; the second run shows a transfer and a
      withdrawal, which pass through it, answering as in the first.
    - Not staged in a browser: the two answers a retried attempt gives, for a record reloaded as
      committed and for a record that is gone. They are the SQL Server tests'. The loss is made
      by holding the record's row from a second database session past the 3 s its store is
      given, and by dropping the page's answer in the browser; neither is an outage. The images
      are the Production ones under compose, on one machine.
- Not measured in a browser: anything on Azure; any browser but Chromium; a database stopped for
  longer than a minute, and a write against a stuck BFF (the second row of "The numbers" and the
  last row's write); the start-up page of decision 10, since each session check got its 200; the
  expiry dialog; the sign-out in Settings, and the shell's against a real outage; a reload during
  a send; and, during an outage, History, a transaction's details, the recipient and funds checks,
  opening, closing and renaming accounts, the PIN change, registration and PIN set-up.

## What would change this

- **Measured response times** (p50, p99 per kind of request): the 5 s and 20 s move with them.
- **`BackendApi:TimeoutSeconds` or the renewal's wait growing:** `timeoutChain.test.ts` turns red
  once the chain no longer holds, and the 65 s and 120 s are derived again.
- **A read-only status by key:** "Check again" would read first, and send only when nothing was
  recorded.
- **More than a demo's traffic, or a drill that shows retries piling up:** a retry budget shared by
  the process, with ADR-0058's NeverBlock.

## Related

- [ADR-0022](0022-client-money-mutation-protocol.md) — which outcomes keep a key; decision 9 here
  adds one that latches the check
- [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) — the renewal's 503
  and why a 5xx keeps the session at the BFF
- [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) — the 503, its
  `retryAfterSeconds` and `applied`, and the 5 + 55 s this client waits past
- `frontend/CONVENTIONS.md` — where a wait's hint goes, as a rule for new pages
