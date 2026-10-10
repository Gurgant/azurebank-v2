# AzureBank — frontend

The single-page app: React 19, TypeScript, Vite, Fluent UI v9, and Redux Toolkit with RTK Query. It
never calls the API itself. Every request goes to the BFF, which holds the session server-side, so
the cookie stays first-party and the JWT never reaches the browser (ADR-0038).

| Path | What is there |
| --- | --- |
| `src/pages/`, `src/components/` | The screens, and what they are built from |
| `src/features/` | The RTK Query API slice, sign-in and the session, the public demo |
| `src/api/` | The envelope, errors and outages; the types and Zod schemas generated from the contract |
| `src/mocks/` | The MSW mock backend, for the tests and for `dev:mock` |
| `src/contract/`, `src/integration/` | The contract suite, and the data layer against the running stack |
| `src/theme/`, `src/test/` | The theme, the one place a colour is spelled out; the tests' setup and helpers |
| `e2e/`, `e2e-demo/`, `screenshots/` | Playwright: the e2e suite, the demo's own run, the capture script |
| `scripts/`, `public/` | The icon generator, and the icons it writes ([brand assets](../docs/brand-assets.md)) |

## Run it

```bash
npm ci
npm run dev        # http://localhost:5173 — /api and /bff are proxied to the BFF on :5000
npm run dev:mock   # the same app against MSW in the browser — no BFF, API or database
```

`npm run dev` needs the BFF and the API running: the
[local setup](../docs/engineering-practices.md#local-setup) has the commands. Under `dev:mock`, sign
in as `demo@azurebank.dev` / `Password1!`, PIN `123456`. The mock's state resets on every page
reload.

Either loop shows the public demo's screens when it is started with `AZUREBANK_DEMO=true` in its
environment: `AZUREBANK_DEMO=true npm run dev:mock` in bash, `$env:AZUREBANK_DEMO = 'true'` and
then the command in PowerShell. The value is exactly `true`, and an env file does not set it.
Under `dev:mock` the mock then hands out demo copies, three to a page load, and no longer signs
its own user in; after a reload it no longer knows the copy the browser kept, and the page offers
"Try the demo" again. [`CONVENTIONS.md`](CONVENTIONS.md#demo-mode) has the rest.

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

- **`npm test` is not the whole suite.** It runs neither the contract suite nor the integration
  suite. The three commands that need the running stack want the API on `https://localhost:7215`:
  [`CONVENTIONS.md`](CONVENTIONS.md) has what that stack must be.
- **`npm run build` is the type check that counts.** `tsc --noEmit` skips the project references
  this tsconfig is built from.
- **`npm run test:e2e:demo` is part of no other run and of no CI job.** It wants the stack of
  `compose.yaml` with `compose.demo.yaml` and nobody else using it: it claims two demo copies and
  restarts the BFF's and the API's containers.
  [`playwright.demo.config.ts`](playwright.demo.config.ts) says how to run it, why it keeps no
  trace, and what it leaves on disk to be deleted.

## Accessibility

axe-core (WCAG 2.0 A/AA, 2.1 AA and 2.2 AA) runs in the e2e step of CI's `real-stack` job:
fifteen scans, over nine pages, the deposit dialog, the Change PIN dialog, the accounts page
while its read is slow (light, dark and 375 px wide) and the open leave prompt of a transfer. It
fails that job on any serious or critical finding except colour contrast, which it only reports.
Fluent's own focus sentinels, which axe flags as `aria-hidden-focus`, are excluded by a selector
the gate proves matches nothing else. Every page carries its own title, and a route change is
announced and moves focus to the new page. The report of each scan is a CI artifact.

The same run holds more by measuring it, each with its note in
[`e2e/accessibility.spec.ts`](e2e/accessibility.spec.ts) and `e2e/deposit.spec.ts`: text that can
be read in both themes, a visible focus on each amount field, focus given back by seven dialogs,
and the layout of the money tiles and of a transaction's row at 375 px.

The public demo's screens are scanned by the same gate only in `npm run test:e2e:demo`, which is
run by hand: no CI job catches a finding on a demo screen. The sign-in page of a browser that
keeps a copy is under no scan.

## Screenshots

The README's pictures, the repository's social preview and a LinkedIn card are taken by a script,
which drives the built app through the BFF as the seeded user John and is never part of a test
run. [`playwright.screenshots.config.ts`](playwright.screenshots.config.ts) has the steps in
order and why the reseed comes first.

```bash
npm run capture:screenshots   # after a reseed, with the API and the BFF running
npm run capture:publish       # the pictures the README uses, into ../docs/images/ (ffmpeg, pngquant)
```

## Generated code

`src/api/schema.d.ts` and `src/api/generated/apiSchemas.ts` are generated from the committed
contract, [`docs/api/openapiv1.json`](../docs/api/openapiv1.json), and never edited by hand.
`npm run generate:api` and `npm run generate:zod` regenerate them; CI fails a pull request whose
committed copies differ from what the contract generates.

## Conventions

The data layer, money and formatting, the UI stack, demo mode, and the traps of testing Fluent
under jsdom: [`CONVENTIONS.md`](CONVENTIONS.md).
