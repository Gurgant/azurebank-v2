# ADR-0028: A data router, bought for one hook — blocking browser Back on a live idempotency key

**Status:** Accepted · **Date:** 2026-07-31 · **Amended:** 2026-08-04 (decision 6, ADR-0033) ·
**Decision Makers:** Vladislav Aleshaev

## Context

ADR-0022 says a live idempotency key blocks dismissal, and the money wizards refuse on every control
the app draws: `PageHeader`'s Back and Close, `requestLeave`, `toForm`, `onBodyEdit`. The browser's
Back button is not one of them. `shouldKeepKey` retains the key on any status >= 500, on `NETWORK`
and on `PARSE`, and those outcomes do not raise the verify view, the one screen offering "check my
transactions" and "start over". So after a 502 the key is live, every in-app exit refuses, and
browser Back is the only exit short of closing the tab. `useBlocker` is the hook that intercepts
it, and in react-router 7 it exists in Framework and Data modes only: under `<BrowserRouter>` it
throws.

## Decision

1. **The app runs on a data router, `createBrowserRouter(createRoutesFromElements(…))` with
   `RouterProvider`, built once at module scope, with no `loader`, no `action` and no `fetcher`**,
   because it pays a known price for one hook, `useBlocker`, and adopts no data-loading
   architecture. The route JSX is unchanged.
2. **The blocker warns and then lets the user leave; it does not veto**, because after a retryable
   5xx a blocker with no `proceed()` locks the last exit, and the app cannot own a button the
   browser drew: `beforeunload` concedes the same for tab-close. It does not close the double-spend
   window; it makes abandoning a key deliberate where it was silent.
3. **The predicate is a function with a `/login` exemption, never a bare `useBlocker(keyLive)`**,
   because `ProtectedRoute` renders `<Navigate to="/login" replace />` when the session ends, the
   router consults the blocker on REPLACE too (measured on a non-exempt path), and a boolean would
   put a prompt in front of a forced logout, on a page whose credentials are dead.
4. **The `beforeunload` guard stays**, because `useBlocker` does not handle hard reloads or
   cross-origin navigations: tab-close, reload, and Back out of the document.
5. **The blocker is reset when its condition clears**, because a Send that succeeds while the
   prompt is open would leave "you have unsent money" standing over a completed transfer.
6. **The route tree has an `errorElement`, `RouteError`, and it is a route boundary only**, because
   with none a data router renders React Router's own unstyled default page. It covers the route
   tree and nothing else: the chrome (toaster, auth bootstrap, session warning, step-up modal)
   renders as siblings above `RouterProvider`, and this decision left a throw there uncovered.
   ADR-0033 closes that gap with `AppErrorBoundary`.
7. **RTK Query stays; TanStack Query is not adopted**, because the step-up interceptor that re-runs
   the original request lives inside the base query, and TanStack has no equivalent seam.
8. **`fetch` stays; axios is not added**, because `problemBaseQuery` already does interceptors,
   error normalisation and retry, and cancellation comes from RTK Query's `AbortSignal`.

## Rejected

- Rejected: a blocker that refuses, because closing the tab would become the error recovery.
- Rejected: a `popstate` listener, because it sees the navigation after it happened and can only
  push a new entry on top, which is a different history.
- Rejected: closing the chrome's error gap inside the routing change, because the gap existed
  before it and the migration narrows it.
- Rejected: TanStack Query, because a migration rewrites the custom `baseQuery` (RFC 7807
  unwrapping, Zod validation at the money boundary, tag invalidation across six endpoint groups, an
  infinite query) and puts the whole risk on the money paths.
- Rejected: axios, because it is a second HTTP stack behind an `axiosBaseQuery` shim, and nothing
  in the app needs upload progress or XSRF token handling.

## Consequences

- `renderWithProviders` builds a `createMemoryRouter`, because a helper in another router mode than
  the app's tests a different application.
- The blocker tests mount the real `useMoneyWizard`, and enter the retryable-5xx state with what
  the real stack gives when the API is down: a `502` with an empty body, normalised to
  `{ status: 502, errorCode: 'HTTP_502' }`, so the key is retained by the `status >= 500` branch.
- Not covered: the double-spend window ADR-0022 accepts: an abandoned key is lost until its TTL.
- Not covered: deposit and withdraw are dialogs, not routes: `beforeunload` is their only guard.
- Not covered: no test fails when `keyLive` is reduced to `keyRetained`. `isSubmitting` alone holds
  the guard for one render that no navigation reaches, and a render-trace assertion would break on
  React's scheduling, not on the app's behaviour.

## Verified by

- `useMoneyWizard.blocker.test.tsx`: warn, leave, stay, the `/login` exemption, REPLACE, POP, the
  retryable 5xx, the prompt that closes itself. `renderWithProviders.test.tsx`: the test helper.
- `RouteError.test.tsx`: a throwing route, and a throw above `RouterProvider`.

## Related

ADR-0022, ADR-0033.
