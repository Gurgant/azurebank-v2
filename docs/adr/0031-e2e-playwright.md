# ADR-0031: The app in a real browser, against the real stack

**Status**: Accepted. Phase 3 of the test-layer plan opened in ADR-0029 and continued in ADR-0030.

**Date**: 2026-08-04

**Decision Makers**: Vladislav Aleshaev

---

## Context

Three layers exist. The unit suite (519 tests) runs React against MSW. The contract suite
(ADR-0029) reads the raw wire from both the mock and the real backend. The integration suite
(ADR-0030) drives the app's own data layer — real store, real `apiSlice`, real Zod — against the
real backend.

None of them renders a page. ADR-0030 said so in as many words under "What is NOT covered": *no
React*. Two consequences follow, and both are the kind of defect a user notices first:

- **A correct response can still be rendered wrongly**, or not at all. Nothing below this layer
  would see it.
- **The step-up modal was never tested at all.** ADR-0030's harness deliberately STANDS IN for
  `<StepUpModal/>`, because the modal is the one genuinely visual piece of the flow. So the protocol
  was proven and the component was not.

## Decision

**1. Playwright, driving the dev topology unchanged.** vite on 5173 proxies `/api` and `/bff` to the
BFF on 5000, which proxies to the API on 7215 over SQL Server LocalDB — exactly what a developer's
browser does. Playwright owns the vite server (`webServer`) and does NOT own the backend.

**2. One login per run, reused via `storageState`.** Not an optimisation: the BFF allows 10 auth
requests per 60s per IP, so a suite that signed in per test would rate-limit itself into red before
testing anything. The saved state is gitignored and regenerated every run — it holds a live session,
and the dev session is 10 minutes' inactivity / 20 absolute, so it is perishable by design.

**3. Serial, `workers: 1`.** Three independent reasons, each sufficient: the auth budget above; one
seeded account, so a balance read would race a deposit; and step-up elevation is server-side session
state, so one test elevating changes what a concurrent test sees.

**4. No retries.** A retry would hide exactly the flakiness this layer is most likely to introduce
(portals, animation, refetch-on-invalidate). A suite that is green only on the second attempt is the
"green and wrong" state the whole strategy exists to avoid. If a spec needs a retry, that is a
finding.

**5. `npm run dev`, never `dev:mock` — and the suite proves it.** The mock validates credentials
against `demo@azurebank.dev`, so signing in with the real fixture returns `401 INVALID_CREDENTIALS`
there and `200` here. Pointing this suite at the mock therefore fails in the setup project rather
than quietly testing the mock for the rest of the run.

**6. The setup project FAILS rather than skips when the backend is down**, mirroring
`src/integration/setup.ts`. A skipped suite reports success without having asked anything.

**7. Locators come from the page's own accessibility snapshot, never from reading source.** Every
name in these specs was measured off the rendered tree.

## Consequences

**Seven tests: the guard, the signed-in shell, a deposit end to end, and three on step-up.** The
step-up ones are the point of the layer — a real 403 from the real BFF, the real modal, six real
digits, a real replay.

**Both claims were falsified before being trusted.** Removing `invalidatesTags` from the deposit
endpoint left the money moved in SQL (€50,027) and the dashboard showing the old figure (€50,026) —
the spec caught a stale balance no lower layer can see. Forcing `baseQueryWithStepUp` to skip its
interceptor made the modal never appear, and the step-up specs went red.

**Four things the rendered app does that source-reading would have got wrong**, each found by
running:

| Assumption | What the app actually does |
|---|---|
| Label is "Email" | It is **"Email address"** |
| `getByLabel(/password/i)` is unambiguous | It also matches the **"Show password"** toggle |
| Six digits then click "Verify" | The **sixth digit IS the submit** — `PinInput` fires `onComplete`, which `StepUpModal` wires to `verify`; the button is gone by the time you look for it |
| The deposit dialog closes on success | It becomes a **"Deposit Complete" receipt**, modal, with the page behind it inert |

Also measured: the account number is masked with **bullets** in the UI (`AB-••••-••••-01`) while the
API sends asterisks — the component substitutes. And the deposit submit button **renames itself** to
"Deposit €1.00" once the amount is valid, which is a real anti-fat-finger property and is now
pinned.

**A gap closed on the way past: `e2e/` and `playwright.config.ts` were typechecked by nothing.**
`tsc -b` covered only `src` and `vite.config.ts`, and Playwright transpiles specs without checking
types, so a bad locator signature would have surfaced at runtime or not at all. They are in
`tsconfig.node.json` now, and a deliberate type error was confirmed to fail `npm run build`.

**React 19 renders correctly under headless Chromium.** Worth recording because a known trap in this
project is a HIDDEN tab never firing `requestAnimationFrame`, which freezes React 19 mid-update.
Playwright's browser does not have that problem; the earlier finding is specific to a backgrounded
tab driven over CDP.

**What is NOT covered.** An honest floor:

- **Only Chromium.** No Firefox or WebKit project, and no mobile viewport — the responsive work has
  its own audit (U8) and belongs there.
- **Transfers and withdrawals**, for the same reason as ADR-0030: the dev database seeds one account
  and one user, so both need a counterparty that does not exist.
- **Session expiry in the UI** — the warning dialog and the forced sign-out. Reaching it means
  waiting out a 10-minute inactivity window; it needs a clock the suite can control, which is its
  own piece of work.
- **Money still accumulates.** Each run deposits €1.00 into the shared dev database. Every assertion
  is relative to a figure read moments earlier, so nothing depends on a fixed starting balance, but
  it is not a clean-room. A database the suite owns belongs with Phase 4.
- **Not wired into CI.** Phase 4. It needs a backend in the runner, and the auth budget is per IP
  rather than per process, so concurrent jobs would steal each other's quota.
