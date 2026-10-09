# ADR-0031: The app in a real browser, against the real stack

**Status:** Accepted · **Date:** 2026-08-04 · **Amended:** 2026-09-04 (ADR-0041), 2026-09-11 and
2026-09-15 (decision 1, ADR-0054), 2026-10-05 (ADR-0063) · **Decision Makers:** Vladislav Aleshaev

## Context

Three test layers sit below this one. The unit suite runs React against MSW, the contract suite
(ADR-0029) reads the raw wire from the mock and from the real backend, and the integration suite
(ADR-0030) drives the app's own data layer against the real backend with no React. None of them
renders a page: a correct response can be rendered wrongly or not at all, and the step-up modal,
which the integration harness stands in for, is never rendered against the real backend.

## Decision

1. **Playwright drives the dev topology unchanged**: vite on 5173 proxies `/api` and `/bff` to the
   BFF on 5000, which proxies to the API on 7215 over SQL Server, because that is what a
   developer's browser talks to. Playwright owns the vite server and none of the backend. In CI,
   with `E2E_BASE_URL` set, it drives the build the BFF serves under its real CSP (ADR-0054).
2. **One sign-in per run, reused through `storageState`**, because the BFF allows 10 auth requests
   per 60 s per IP and a suite that signed in per test would rate-limit itself. The saved state
   holds a live session, so it is gitignored and made again on every run.
3. **Serial, `workers: 1`**, for three reasons, each sufficient: the auth budget; one seeded
   account, so a balance read would race a deposit; and step-up elevation is server-side session
   state, so one test elevating changes what a concurrent test sees.
4. **No retries**, because a retry hides the flakiness this layer is most likely to introduce
   (portals, animation, refetch on invalidation). A spec that needs a retry is a finding.
5. **`npm run dev`, never `dev:mock`**, because a run against the mock must not pass: the mock knows
   `demo@azurebank.dev` only, so the real fixture's sign-in gets `401 INVALID_CREDENTIALS` in setup.
6. **The setup project fails, and does not skip, when the backend is down**, as
   `src/integration/setup.ts` does, because a skipped suite reports success without having asked.
7. **Locators come from the page's accessibility tree, never from reading the source**, because
   the rendered names are not the ones the source suggests: the label is "Email address", and
   `getByLabel(/password/i)` also matches the "Show password" toggle.

## Rejected

- Rejected: an assertion, after the redirect settles, that the protected text has count 0, because
  it passes against a guard that renders its children while the boot probe is in flight.
- Rejected: the URL as proof of a session, because an address does not prove the cookie was set:
  the setup project also waits for the `Main navigation` landmark, shown to a signed-in user only.
- Rejected: `"DOM"` added to `tsconfig.node.json` in place of a project for the specs, because it
  hands `vite.config.ts` browser globals it cannot use at run time.

## Consequences

- The step-up modal is exercised whole: a real 403 from the real BFF, the real modal, six real
  digits (the sixth is the submit), a real replay.
- A stale balance fails here and in no lower layer: with `invalidatesTags` removed from the deposit
  endpoint the money moves in SQL, the dashboard keeps the old figure and the deposit spec fails.
- That no protected content is shown without a session is held by a detector: a `MutationObserver`
  installed through `addInitScript`, before any page script, latches the moment the text appears.
  The signed-in test is its positive control.
- `e2e/` and `playwright.config.ts` are type-checked by a project of their own, `tsconfig.e2e.json`,
  in `npm run build`, because Playwright transpiles specs without checking types. The callbacks of
  `addInitScript` and `evaluate` run inside the page, so the specs need the DOM types as well.
- Suite order is a correctness property, because elevation cannot be undone: `SetPinVerified` is the
  only mutator and the level drops only when `PinValidityMinutes` pass (10 in development, 5 in the
  base configuration). In `e2e/stepUp.spec.ts` cancel runs before verify, and nothing that elevates
  sorts ahead of that file: only `/full-number` is gated since ADR-0041, and a transfer carries its
  authorisation in-band (ADR-0042). Each run starts at level 1: the setup project signs in fresh.
- In CI the BFF runs with `--RateLimiting:AuthPermitLimit=1000`, because the auth budget is per IP
  and not per process; the workflow marks it as environmental, not a weakened assertion.
- Not covered: a transfer or a withdrawal completed through the page against the real stack:
  `e2e/wentThrough.spec.ts` drives both to the send with a real PIN, and `page.route` answers the
  send in the browser, so no money moves. The demo's own run, `npm run test:e2e:demo`
  (ADR-0063), sends one transfer and restarts two containers; it is started by hand, in no CI job.
- Not covered: Firefox and WebKit (the one browser project is Chromium), and session expiry in
  the UI, which needs a clock the suite controls.
- Not covered: a clean room: each run deposits €1.00 into the shared dev database, and every
  assertion is relative to a figure read moments earlier.

## Verified by

- `npm run test:e2e` in `frontend/`: `playwright.config.ts` and the specs in `e2e/`, among them
  `auth.setup.ts`, `auth.spec.ts`, `deposit.spec.ts` and `stepUp.spec.ts`.
- `npm run build`, which type-checks the specs through `tsconfig.e2e.json`.

## Related

ADR-0029, ADR-0030, ADR-0032, ADR-0041, ADR-0042, ADR-0054, ADR-0063.
