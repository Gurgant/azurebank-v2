# ADR-0033: A root error boundary, so a render error is not a blank page

**Status:** Accepted (closes the gap ADR-0028 decision 6 recorded and deferred) ·
**Date:** 2026-08-04 · **Decision Makers:** Vladislav Aleshaev

## Context

`RouteError` (ADR-0028) is a route boundary: React Router hands it the errors thrown inside the
route tree and nothing else. `App` renders the toaster, the auth bootstrap, the session-expiry
warning and the step-up modal as siblings of `RouterProvider`, with `Provider` and `ThemeProvider`
above all of them. Without a root boundary a throw anywhere in that chrome goes past the route
boundary, React unmounts the whole tree, and the user is left with a blank page and no way forward
but a manual reload.

## Decision

1. **One boundary, `AppErrorBoundary`, is the outermost element of `App`**, above `Provider` and
   `ThemeProvider`, because placed lower it leaves the providers uncovered, and the providers are
   where a store or theme failure comes from.
2. **It is a class component**, because `getDerivedStateFromError` and `componentDidCatch` exist
   only on classes, in React 19 as before.
3. **The fallback depends on nothing**: plain elements and inline styles with absolute colours, no
   Fluent components, no Griffel, no theme tokens and no store reads, because when the boundary
   catches React has already unmounted everything below it, the `FluentProvider` that supplies the
   page's colours included, and `body` in `index.css` sets no colours of its own.
4. **It recovers with a full document load, `window.location.assign('/')`, not a router
   navigation**, because the router is inside the subtree that failed. `RouteError` recovers the
   same way, so the app has one answer and not two.
5. **The error is logged, never rendered**, because a stack can carry request detail and this
   screen is reachable by anyone.
6. **The two boundaries stay distinct**: "This page could not be displayed" for a route failure and
   "The application could not be displayed" for a root one, so that a test asserts which boundary
   handled a throw and not merely that one did.

## Rejected

- Rejected: the boundary inside `Provider`, because store and theme failures stay uncovered.
- Rejected: React 19's `onUncaughtError` and `onCaughtError`, because they only observe an error.
- Rejected: a retry in place, because a boundary that resets itself with the cause unfixed loops.
- Rejected: an error reporting service, because telemetry in a banking UI is a decision of its own.
- Rejected: an end-to-end test, because it needs a production code path that fails on purpose.

## Consequences

- A throw in the chrome or in a provider renders a recovery screen with one action, a full load.
- ADR-0028's test of the gap is inverted: a throw above `RouterProvider` ends in the root fallback
  and not the route one. `errorElement` still does not see the chrome.
- The wiring and the no-dependency rule are asserted against the source, because the output cannot
  show either: the boundary's own tests render it directly, and a fallback that imports Fluent
  renders the same wherever the theme is healthy.
- Not covered: `main.tsx`. A throw in `createRoot` or in the mock-worker bootstrap happens before
  `App` renders.
- Not covered: nothing is reported anywhere; `console.error` is the whole of it.

## Verified by

- `frontend/src/components/layout/AppErrorBoundary.test.tsx`: the fallback, the full load, no Fluent
  import and no token in the source, and the boundary above `Provider` in `App.tsx`.
- `frontend/src/components/layout/RouteError.test.tsx`: which boundary takes a throw in the chrome.

## Related

ADR-0028.
