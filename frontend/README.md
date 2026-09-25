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

`npm run dev` needs the BFF and the API running; the root README has the commands. Under
`dev:mock`, sign in as `demo@azurebank.dev` / `Password1!`, PIN `123456`. The mock's state resets on
every page reload.

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
```

`npm test` runs neither the contract suite nor the integration suite. The contract suite has two
targets: against the real one it needs a running stack, while `npm run test:contract:mock` runs it
against the mock with nothing else running. The integration suite has no mock target, so it always
needs the stack.
[`CONVENTIONS.md`](CONVENTIONS.md) has the details. `npm run build` is the type check that counts:
`tsc --noEmit` skips the project references this tsconfig is built from.

Until 2026-09-24 one test in `test:contract:real` — an oversized body refused at 32 KB — sometimes
failed on Windows with `ECONNRESET`. It was not the test: the API answered the 413 without reading
the body and then aborted the connection, which the BFF passed on. The API now reads the body first;
[`docs/engineering-traps.md`](../docs/engineering-traps.md) has the measurements.

## Accessibility

axe-core (WCAG 2.0 A/AA, 2.1 AA and 2.2 AA) runs in CI's e2e job over nine pages, the deposit dialog
and the Change PIN dialog, and fails the job on any serious or critical finding except colour
contrast, which it reports and leaves to the UI/UX phase: on 2026-09-17 that was 25 nodes on theme
tokens (muted secondary text, the sidebar avatar, a button group and the danger-zone button) on
seven pages and the deposit dialog, a count that moves with the data a page shows. Fluent's own
focus sentinels, which axe flags as `aria-hidden-focus` two per page, are excluded by a selector the
spec proves matches nothing else. Every page carries its own title, and a route change is announced
and moves focus to the new page. The per-scan JSON reports are a CI artifact.

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
