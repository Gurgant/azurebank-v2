# AzureBank — frontend

The single-page app: React 19, TypeScript, Vite, Fluent UI v9, and Redux Toolkit with RTK Query. It
never calls the API itself. Every request goes to the BFF, which holds the session server-side, so
the cookie stays first-party and the JWT never reaches the browser (ADR-0038). _(Until 2026-09-25
this said "no token"; the browser does hold a one-shot PIN authorisation's id between the PIN
and the operation it authorises.)_

## Run it

```bash
npm ci
npm run dev        # http://localhost:5173 — /api and /bff are proxied to the BFF on :5000
npm run dev:mock   # the same app against MSW in the browser — no BFF, API or database
```

`npm run dev` needs the BFF and the API running; the
[local setup](../docs/engineering-practices.md#local-setup) has the commands _(until 2026-09-25 this
said the root README had them)_. Under `dev:mock`, sign in as `demo@azurebank.dev` / `Password1!`,
PIN `123456`. The mock's state resets on every page reload.

Either loop shows the public demo's screens when it is started with `AZUREBANK_DEMO=true` in its
environment: the dev server then puts the demo's tag on the page, and under `dev:mock` the mock
hands out demo copies and no longer signs its own user in. The console says so there:
`[MSW] Mock backend ON, as the public demo: press "Try the demo" (PIN 123456)`. The copies the
mock hands out live as long as the page, three to a page load. After a reload the browser still
keeps its copy and the mock no longer knows it: "Continue with my copy" is refused as a copy
that is gone, and the page offers "Try the demo" again (seen on 2026-10-05 in Chromium, where
the fourth claim of one page load was refused with every demo copy in use).
[`CONVENTIONS.md`](CONVENTIONS.md#demo-mode) has the tag, the key the demo keeps in the browser and
how a test turns the demo on.

## Check it

```bash
npm run format:check
npm run lint
npm run build               # the type gate: tsc -b, then the bundle
npm test                    # unit and component tests, against MSW
npm run test:contract:mock  # the contract suite against the mock ...
npm run test:contract:real  # ... and against the running BFF and API (ADR-0029)
npm run test:integration    # the data layer against the running stack
npm run test:e2e            # Playwright; starts vite itself, needs the BFF and the API
npm run test:e2e:demo       # the public demo in a browser: by hand, on the compose demo stack
```

`npm test` runs neither the contract suite nor the integration suite. The contract suite has two
targets: against the real one it needs a running stack, while `npm run test:contract:mock` runs it
against the mock with nothing else running. The integration suite has no mock target, so it always
needs the stack.
[`CONVENTIONS.md`](CONVENTIONS.md) has the details. `npm run build` is the type check that counts:
`tsc --noEmit` skips the project references this tsconfig is built from.

`npm run test:e2e:demo` is part of no other run and of no CI job. It wants the stack of
`compose.yaml` with `compose.demo.yaml` and nobody else using it: it claims two demo copies, sends
one transfer inside a copy it claimed, and restarts the BFF's and the API's containers.
[`playwright.demo.config.ts`](playwright.demo.config.ts) says how to run it, why it keeps no
trace, and what it leaves on disk to be deleted.

Until 2026-09-24 one test in `test:contract:real` — an oversized body refused at 32 KB — sometimes
failed on Windows with `ECONNRESET`. It was not the test: the API answered the 413 without reading
the body and then aborted the connection, which the BFF passed on. The API now reads the body first;
[`docs/engineering-traps.md`](../docs/engineering-traps.md) has the measurements.

## Accessibility

axe-core (WCAG 2.0 A/AA, 2.1 AA and 2.2 AA) runs in the e2e step of CI's `real-stack` job over nine
pages, the deposit dialog, the Change PIN dialog, the accounts page while its read is slow (light,
dark and 375 px wide) and the open leave prompt of a transfer: fifteen scans. _(Until 2026-10-05
this named the nine pages and the two dialogs only. The three scans of the slow read were in the
run already; the leave prompt's is new that day, in `e2e/confirmDialog.spec.ts`.)_ It fails that
job on any serious or critical
finding except colour contrast, which it reports and leaves to the UI/UX phase: on 2026-09-17 that
was 25 nodes on theme tokens (muted secondary text, the sidebar avatar, a button group and the
danger-zone button) on seven pages and the deposit dialog, a count that moves with the data a page
shows. Fluent's own
focus sentinels, which axe flags as `aria-hidden-focus` two per page, are excluded by a selector the
spec proves matches nothing else. Every page carries its own title, and a route change is announced
and moves focus to the new page. The per-scan JSON reports are a CI artifact.

The same spec holds six more things by measuring them, where the sweep only reports or cannot
see: what is typed in the app's three plain text fields, and their placeholder, can be read in
both themes; each amount field shows where focus is; seven dialogs give focus back to the control
that opened them; at 375 px the two money tiles share a row and two buttons of Settings keep their
label on one line; and seven texts that measured under 4.5 to 1 stay at or above it in both
themes. Two more need an entry in the ledger, which the suite's user is not seeded with, so
`e2e/deposit.spec.ts` holds them after its deposit: at 375 px a status pill stays in its column,
and the "Completed" badge of a transaction's page can be read in both themes. That is 37 rows, 33
and 4, added on 2026-10-06. That day 24 of them failed before the fix they hold, and all 37 passed
after it, against `npm run dev:mock` with the mock's user signed in at each page load by a seed
kept outside the repository. None was run that day in this suite's own configuration, which needs
the real stack.

The public demo's screens are scanned by the same gate only in `npm run test:e2e:demo`, which is
run by hand: the sign-in page as the demo shows it to a browser that keeps no copy, the
dashboard with its panel, once with the copy's sign-in details closed and once with them open,
and the "Start over" dialog. The sign-in page of a browser that keeps a copy is under no scan.
Until CI has a job for that run, the gate catches a finding on a demo screen nowhere else. On
2026-10-05, against the compose stack with the demo on, in each of the day's three runs none of
the four scans had a finding the gate fails on: the sign-in page and the dialog had no
violation, and the dashboard had colour contrast alone, on two nodes, in both of its scans.

## Screenshots

The README's pictures, the repository's social preview and a LinkedIn card are taken by a script,
so they can be taken again when the UI changes. It drives the built app through the BFF as the
seeded user John, like the e2e suite, and is never part of a test run:

```bash
npm run capture:screenshots   # after a reseed, with the API and the BFF running
npm run capture:publish       # the pictures the README uses, into ../docs/images/ (ffmpeg, pngquant)
```

[`playwright.screenshots.config.ts`](playwright.screenshots.config.ts) has the steps in order and why
the reseed comes first.

## Generated code

`src/api/schema.d.ts` and `src/api/generated/apiSchemas.ts` are generated from the committed
contract, [`docs/api/openapiv1.json`](../docs/api/openapiv1.json), and never edited by hand.
`npm run generate:api` and `npm run generate:zod` regenerate them; CI fails a pull request whose
committed copies differ from what the contract generates.

## Conventions

The data layer, money and formatting, the UI stack, and the traps of testing Fluent under jsdom:
[`CONVENTIONS.md`](CONVENTIONS.md).
