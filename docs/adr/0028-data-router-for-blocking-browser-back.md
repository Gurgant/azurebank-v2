# ADR-0028: A data router, bought for one hook — blocking browser Back on a live idempotency key

**Status**: Accepted

**Date**: 2026-07-31

**Decision Makers**: Vladislav Aleshaev

---

## Context

ADR-0022 says a live idempotency key must block dismissal, and the money wizards implement that four
times over: `PageHeader` gets `backDisabled={keyLive} closeDisabled={keyLive}`, `requestLeave`
refuses, `toForm` refuses, `onBodyEdit` refuses. Every one of those guards a control **the app
draws**. The browser's Back button is not one of them, and `useMoneyWizard` has carried a comment
saying so since it was extracted: _"(In-app popstate needs a data-router useBlocker — deferred with
the router migration.)"_ That comment named a gap, named the mechanism, and deferred it to a
migration nobody had costed. This records the cost.

The gap is sharper than "Back ignores the guard", and the sharp version is what decides the design.
`shouldKeepKey` retains the key on **any >= 500**, on `NETWORK` and on `PARSE`, while
`verifyRequired` is only the in-flight case. So after a 502: `keyLive` is true, `verifyRequired` is
false, and the verify view — the one screen offering "check my transactions" and "start over" — does
not render. Back and Close are disabled. `requestLeave` no-ops. The error banner carries no action.
**Browser Back is the only exit from that state short of closing the tab** — and before this ADR it
took that exit silently, which is the whole problem.

Mechanically the situation was settled against the installed package rather than the docs site.
`react-router` and `react-router-dom` are both **7.18.1**. The availability table in
`node_modules/react-router/docs/start/modes.md` ticks `useBlocker` for Framework and Data and leaves
**Declarative blank**; `useBeforeUnload` is ticked for all three. Under `<BrowserRouter>` the hook
does not degrade — it throws `useBlocker must be used within a data router`, which is exactly how
this migration announced itself: thirty existing tests failed at once the moment the wizard gained a
blocker, because the test helper was still on `MemoryRouter`.

No userland code replicates POP interception. A `popstate` listener sees the navigation _after_ it
has happened and can only push a new entry on top, which is a different history than the one the
user was in. The router undoes the pop and replays it on `proceed()`.

Three adjacent questions arrived with this one — replace RTK Query with TanStack Query, replace
`fetch` with axios, and a general "is this stack current" review. All three resolve to **change
nothing**, and they are recorded here because there will be no code to read later explaining why.

## Decision

**1. Migrate to a data router: `createBrowserRouter(createRoutesFromElements(…))` + `RouterProvider`,
built once at module scope.** No `loader`, no `action`, no `fetcher`, now or as a consequence. This
buys exactly one thing — `useBlocker` — and should be read as paying a known price for a named hook,
not as adopting a data-loading architecture. The route JSX converts verbatim: the tree is flat
`<Route path element>` with no index routes and no descendant `<Routes>`, and both `<Navigate>` uses
are available in every mode. The four root chrome components — toaster, auth bootstrap, session
warning, step-up modal — already render _above_ the router and import nothing from
`react-router-dom`, so the "RouterProvider takes no children" problem never arises.

**2. The blocker warns; it does not veto.** This is the decision the rest of the wizard argues
against, and refusing was the tempting wrong answer because it matches every in-app guard. It is
wrong for two reasons. The narrow one is the 502 trace above: a blocker with no `proceed()` converts
the last remaining exit into a locked door and makes "close the tab" the app's error-recovery path.
The broad one is that the app has no standing to veto browser chrome — it can disable a button it
drew; it cannot own the one the browser drew. `beforeunload` already concedes exactly this for
tab-close, because the browser enforces the concession. Extending the same warn-then-defer contract
to POP is consistency, not a weakening.

Say plainly what this buys: **it does not close the double-spend window.** ADR-0022 already accepts
that an abandoned key is lost until TTL. What changes is that the abandonment becomes deliberate
instead of silent — the same thing `beforeunload` buys, applied to the one exit that had no warning
at all.

**3. The predicate is a function with a `/login` exemption. A bare `useBlocker(keyLive)` is a defect
on this app specifically.** `ProtectedRoute` renders `<Navigate to="/login" … replace />` the moment
auth stops being `authenticated`, the router consults the blocker on REPLACE too, and both money
routes sit inside `ProtectedRoute`. A boolean blocker would put "are you sure you want to leave?" in
front of a **forced session-expiry logout**, holding a user on a page whose credentials are dead.

**4. Keep the `beforeunload` effect.** The two are complementary by explicit design. `useBlocker`'s
own JSDoc: _"This does not handle hard-reloads or cross-origin navigations."_ Deleting the unload
guard after adding the blocker would remove the guard on tab-close, reload, and Back out of the
document — the case where the user landed on `/transfer` directly rather than in-app.

**5. Reset the blocker when its condition clears.** Leave the dialog open, press Send again, let it
succeed: the key clears while `blocker.state` is still `blocked`, leaving "you have unsent money"
standing over a completed transfer.

**6. Supply an `errorElement`, and be exact about how little it covers.** A data router intercepts a
render error thrown inside the route tree, and with no `errorElement` it renders React Router's own
default page — unstyled, "Unexpected Application Error", belonging to a library rather than to a
bank. Nothing throws today, which is precisely why a wrong wiring would have shipped unnoticed.

The scope claim needs stating plainly, because the obvious reading is too generous. `errorElement` is
a ROUTE boundary: it covers the route tree and nothing else. The four chrome components — toaster,
auth bootstrap, session warning, step-up modal — render as SIBLINGS above `RouterProvider`, so a
throw in any of them goes straight past it. And it lands nowhere, because **this app has no React
error boundary anywhere**: `main.tsx` renders `<StrictMode><App /></StrictMode>` with nothing in
between, and no component in `src/` implements `componentDidCatch` or `getDerivedStateFromError`.
So an earlier draft of this ADR was wrong to say a data router catches errors "instead of letting
them reach a React error boundary" — there was never one to reach. Before the migration such a throw
blanked the screen; after it, a throw in the route tree is handled and a throw in the chrome still
blanks the screen.

That is a real gap, but a PRE-EXISTING one that this migration narrows rather than causes, so
closing it is deliberately left out of a routing change. Both halves are pinned by test: a throwing
route renders `RouteError`, and a sibling above `RouterProvider` is proven NOT to be covered.

> **CLOSED, 2026-08-04 (ADR-0033).** The scope claim above still holds exactly — `errorElement` is
> still a ROUTE boundary and still does not see the chrome. What no longer holds is the consequence:
> `AppErrorBoundary` now wraps the whole tree in `App.tsx`, above `Provider` and `ThemeProvider`, so
> a throw in the chrome renders a recovery screen instead of blanking the page. The paragraph above
> is left standing because it was true when written and explains why the gap existed; the test that
> pinned the blanking has been inverted to pin the catch, and to assert WHICH of the two boundaries
> handled it.

**7. Keep RTK Query. Do not adopt TanStack Query.** What the app uses is not the part TanStack does
better: a custom `baseQuery` doing RFC7807 unwrapping, Zod validation at the money boundary, a
step-up/PIN interceptor that re-runs the original request, tag invalidation across six endpoint
groups, and an infinite query. The step-up interceptor is the decisive one — it lives _inside_ the
base query, and TanStack has no equivalent seam; it would move into the fetch wrapper and stop being
a data-layer concern. A migration would be a rewrite of the interesting parts and a like-for-like
copy of the boring ones, with the risk landing entirely on the money paths.

**8. Keep `fetch`. Do not add axios.** `problemBaseQuery` already solves what axios is usually
reached for: interceptors, error normalisation, and retry. Nothing in the app needs upload progress
or XSRF token handling, and cancellation already comes from RTK Query's `AbortSignal`. Adding axios
would mean a second HTTP stack behind an `axiosBaseQuery` shim, for no capability the app uses.

## Consequences

**The test helper had to move too, and that is the useful part.** `renderWithProviders` now builds a
`createMemoryRouter` rather than a `MemoryRouter`. Thirty tests failed the moment the blocker landed,
which is the right failure: a helper that renders in a different router MODE than the app is testing
a different application.

**The tests mount the real hook, and the first draft did not.** That draft reimplemented the
predicate in the test file and justified it on the grounds that reaching a live key means driving
RTK Query, an idempotency key and a step-up interceptor. That justification was wrong, and the
recorded version of it is more useful than a clean claim would be: `useMoneyWizard` takes its
`trigger` as a PARAMETER, and `keyLive` is `isSubmitting || keyRetained`, so a trigger whose promise
never settles holds a key live with no infrastructure at all. What the draft actually tested was
react-router — it would have stayed green if the hook never registered a blocker.

So the suite is bound to production by mutation. Across eleven tests: falsifying the predicate to
`false` fails seven; removing the `/login` exemption fails two; swapping `proceed()` for `reset()`
fails three; gutting the reset effect fails the prompt-closes-itself test; and reducing `keyLive` to
`isSubmitting` alone fails the retryable-5xx test.

**REPLACE was asserted here before it was tested, and the assertion was load-bearing.** Decision 3
claims the router consults blockers on REPLACE — if it did not, `ProtectedRoute`'s
`<Navigate to="/login" replace />` could never have been held, the exemption would be dead code, and
the decision would be wrong. Every test drove a `<Link>`, which is a PUSH. It is now checked on a
NON-exempt path, because a REPLACE to `/login` passing through is equally consistent with "REPLACE is
never consulted at all". Measured: a REPLACE to a non-exempt path IS held.

**One mutation is still not caught, and the reason recorded here previously was wrong.** Reducing
`keyLive` to `keyRetained` passes every test. The earlier claim was that both flags "rise and fall in
the same render on every path a test can construct" — that is false, and a render trace shows it.
After `verifyRequired` latches, a second Send throws inside `submit()` *before* `setKeyRetained(true)`
runs, committing exactly one render with `isSubmitting: true, keyRetained: false`, where
`isSubmitting ||` is the only thing holding the guard.

That window is real but **not reachable by a navigation**, which was also measured rather than
assumed: firing a synchronous Send and navigating immediately still goes through *with the correct
code*, because the rejection microtask restores `isSubmitting: false` before the blocker is
consulted. So the half is genuinely load-bearing for one render, and no BEHAVIOURAL test can
distinguish it. A render-trace assertion would catch the mutation, and is deliberately not added: it
would pin an internal sequence inside a window no user can act within, which is a test that breaks on
React's scheduling rather than on this app's behaviour.

**The retryable-5xx state is now entered rather than described.** Before this change every test held
the key with a PENDING request, which raises `isSubmitting` and `keyRetained` together — so the trap
this ADR argues from was the one state the suite never reached. It is reached by letting a send FAIL retryably, and
the failure is shaped from the REAL stack rather than invented: with the API down, the BFF answers a
proxied POST with `502 Bad Gateway` and `Content-Length: 0`, and that empty body is not a parse
failure — RTK's json handler returns `null`, so `toApiProblem` keeps the numeric status and
synthesizes the code. End to end the normalization is `{ status: 502, errorCode: 'HTTP_502' }`, with
no title, detail or traceId, because a gateway 5xx never reaches `GlobalExceptionHandler` — the only
thing that writes an `errorCode` extension. The first draft asserted `errorCode: 'SERVER_ERROR'`,
which nothing in this system emits. Worth pinning exactly, since `shouldKeepKey` retains on
`status >= 500`: had the empty body normalized to `'PARSE'`, retention would come from a different
branch and the test would prove the wrong one.

**What is NOT closed.** Deposit and withdraw are dialogs, not routes, so Back leaves the whole page
rather than the flow; they are covered by `beforeunload` only. And the window ADR-0022 accepts stays
open — this makes abandonment visible, not impossible.
