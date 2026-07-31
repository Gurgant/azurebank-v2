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
**Browser Back is currently the only exit from that state short of closing the tab.**

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

**6. Supply an `errorElement`.** A data router catches render errors instead of letting them reach a
React error boundary, and with none supplied it renders React Router's own default page — unstyled,
"Unexpected Application Error", belonging to a library rather than to a bank. Nothing throws today,
which is precisely why this would have shipped unnoticed.

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

**The blocker is exercised directly rather than through the wizard.** Driving RTK Query, an
idempotency key and a step-up interceptor to reach a live key would test those instead. Five tests
pin what is actually new — held, both answers offered, `/login` exempt, and a negative control with
no key live — and two mutations prove the two decisions: removing the `/login` exemption fails the
forced-logout test, and swapping `proceed()` for `reset()` fails the leave-anyway test.

**What is NOT closed.** Deposit and withdraw are dialogs, not routes, so Back leaves the whole page
rather than the flow; they are covered by `beforeunload` only. And the window ADR-0022 accepts stays
open — this makes abandonment visible, not impossible.
