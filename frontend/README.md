# AzureBank — frontend

The single-page app: React 19, TypeScript, Vite, Fluent UI v9, and Redux Toolkit with RTK Query. It
never calls the API itself. Every request goes to the BFF, which holds the session server-side, so
the cookie stays first-party and no token ever reaches the browser (ADR-0038).

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

`npm test` leaves the contract and integration suites out, because they need a running stack;
[`CONVENTIONS.md`](CONVENTIONS.md) has the details. `npm run build` is the type check that counts:
`tsc --noEmit` skips the project references this tsconfig is built from.

On Windows, one test in `test:contract:real` — an oversized body refused at 32 KB — sometimes fails
with `ECONNRESET`: three times in nine local runs on 2026-09-23 and 2026-09-24, main's code
included, each time passing on the next run. CI, on Linux, has not shown it.

## Generated code

`src/api/schema.d.ts` and `src/api/generated/apiSchemas.ts` are generated from the committed
contract, [`docs/api/openapiv1.json`](../docs/api/openapiv1.json), and never edited by hand.
`npm run generate:api` and `npm run generate:zod` regenerate them; CI fails a pull request whose
committed copies differ from what the contract generates.

## Conventions

The data layer, money and formatting, the UI stack, and the traps of testing Fluent under jsdom:
[`CONVENTIONS.md`](CONVENTIONS.md).
