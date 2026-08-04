# ADR-0033: A root error boundary, so a render error is not a blank page

**Status**: Accepted. Closes the gap ADR-0028 decision 6 recorded and deferred.

**Date**: 2026-08-04

**Decision Makers**: Vladislav Aleshaev

---

## Context

The app had **no React error boundary at all**. `main.tsx` rendered `<StrictMode><App /></StrictMode>`
with nothing in between, and no component in `src/` implemented `componentDidCatch` or
`getDerivedStateFromError`.

`RouteError` (ADR-0028) is not one. It is a ROUTE boundary: React Router hands it errors thrown
inside the route tree and nothing else. `App` renders four components — the toaster, the auth
bootstrap, the session-expiry warning and the step-up modal — as SIBLINGS of `RouterProvider`, with
`Provider` and `ThemeProvider` above all of them. A throw anywhere in that chrome went past the
route boundary to the root, React unmounted the entire tree, and the user was left looking at white
nothing with no route forward but a manual reload.

ADR-0028 decision 6 recorded this precisely and deferred it, on the grounds that closing it did not
belong in a routing change. It also left a test asserting the blanking, with a note that the day a
boundary existed the test should be replaced. This is that day.

## Decision

**1. One boundary, `AppErrorBoundary`, as the OUTERMOST element of `App`.** Above `Provider` and
`ThemeProvider`, not merely beside the chrome. Placed lower it would leave the providers uncovered,
and the providers are exactly where a store or theme failure comes from.

**2. It is a class component, because there is still no alternative.** `getDerivedStateFromError`
and `componentDidCatch` exist only on classes, in React 19 as before. React 19's `onUncaughtError` /
`onCaughtError` root options are for observing, not for rendering a fallback, so they change nothing
here and are not used.

**3. The fallback depends on NOTHING.** Plain elements and inline styles — no Fluent components, no
Griffel, no theme tokens, no store reads. When this boundary catches, React has already unmounted
everything below it, including the `FluentProvider` that supplies the page's background and text
colours. A fallback built from those would be asking the broken thing to render the apology. Because
`body` in `index.css` sets no colours of its own, the styles are absolute rather than inherited: the
screen has to be legible on a bare document.

**4. It recovers with a full document load, not a router navigation.** The router is inside the
subtree that just failed. `window.location.assign('/')` — the same recovery idiom and the same
wording `RouteError` already uses, so the app has one answer to this rather than two.

**5. The error is logged, never rendered.** The same rule `RouteError` follows: a stack can carry
request detail and this screen is reachable by anyone, so the user gets a sentence they can act on.

**6. The two boundaries stay distinct, and the tests say which is which.** The copy differs on
purpose — "This page could not be displayed" for a route failure, "The application could not be
displayed" for a root one — so a test can assert WHICH boundary handled a throw rather than merely
that something did.

## Consequences

**ADR-0028's negative control is inverted, not deleted.** It asserted that a throw above
`RouterProvider` propagated out of `render`. It now asserts that the same throw is caught and that
the ROOT fallback is what appears, with an explicit check that the route fallback is not. The scope
claim it was really about — `errorElement` does not see the chrome — is unchanged and still pinned.

**Wiring is asserted at the source, and both mutations were checked.** The boundary's own tests
render it directly, so they would all pass with `App.tsx` never using it. A source assertion covers
that, the way this repo already covers `errorElement`, `brandFillUsage` and `iconProvenance`.
Verified by mutation: deleting `<AppErrorBoundary>` from `App.tsx` fails it, and — the subtler case —
so does moving it INSIDE `Provider`, which is the placement that would quietly leave store and theme
failures uncovered.

**The no-dependency rule is asserted too, because it cannot be observed from the output.** A
fallback importing Fluent renders identically in a test where the theme is healthy; it fails only in
the real scenario, which is the one that cannot be reproduced. So the constraint is checked against
the source: `AppErrorBoundary.tsx` must not import `@fluentui/react-components` and must not use
`makeStyles` or theme tokens.

**What this does NOT do.**

- **No retry-in-place.** The fallback offers a full load, not a "try again" that re-renders the same
  broken tree. A boundary that resets its own state without the cause being fixed loops.
- **No error reporting service.** Nothing is sent anywhere; `console.error` is the whole of it. Wiring
  telemetry is a separate decision with its own privacy questions for a banking UI.
- **Not covered by E2E.** Triggering a real root throw from a browser needs a production code path
  that deliberately fails, which is a worse trade than the unit coverage here.
- **`main.tsx` is still uncovered**, strictly speaking — a throw in `createRoot` or in the mock-worker
  bootstrap happens before `App` renders. That is a three-line surface with no app logic in it.
