# ADR-0023: Runtime response validation — spec-generated Zod, fail-closed on money

**Status:** Accepted · **Date:** 2026-07-25 · **Amended:** 2026-09-10 and 2026-09-17 (ADR-0053),
2026-09-15 (ADR-0043) · **Amends:** ADR-0019, decision 6 · **Decision Makers:** Vladislav Aleshaev

## Context

TypeScript types are erased at run time and assert nothing about what a server sent. On `/api/*`
the types are generated from the committed OpenAPI document, a CI gate holds them to it, a backend
test holds the document to the code (ADR-0053) and Schemathesis checks the running API against it
on every pull request, as the `conformance` job. The BFF surface has none of that: `/bff/auth/*` is
not in the OpenAPI document at all, and hand-written types that mirror `BffResponses.cs` check
nothing. That makes the auth boundary the weakest one in the system: a silent drift between the
SPA and the BFF flows into the auth slice with nothing to catch it.

## Decision

The BFF surface (`/bff/auth/*`) is fail-closed in its entirety, because it is small and every
response on it gates authentication; the API surface (`/api/*`) only where money is displayed.

1. **On the BFF surface the schemas are the source of truth and validation is fail-closed
   everywhere, production included**, because this boundary has no document behind it.
   `src/api/bffSchemas.ts` holds the Zod schemas and `src/api/bffTypes.ts` is `z.infer` of them, so
   type and validator cannot disagree; the responses parse through the `unwrap(envelope, schema?)`
   seam with no environment gate. `location.state` and error bodies use `safeParse` with a
   fallback, because a garbled error payload must not replace the error it was carrying.
2. **Zod for `/api/*` is generated, never hand-written**: `typed-openapi` with
   `--runtime zod --schemas-only`, its output committed to `src/api/generated/`, behind
   `npm run generate:zod`, because a hand-written validator for a surface that already has a
   machine-readable contract duplicates the contract and drifts. The generated `ProblemDetails`
   carries `errorCode` since ADR-0043 corrected the document.
3. **Three generators are rejected, by name**, because the reasons are expensive to find again.
   `openapi-zod-client`, the obvious first search result, is a dead end: stale for roughly 17
   months, wired to Zod 3, its v4 pull request abandoned. `kubb` carries three-package overhead in
   configuration. `orval`'s Zod 4 migration was still settling. `hey-api` is an acceptable fallback.
4. **On the API surface, fail-closed in production is limited to the money surfaces**: the four
   mutation receipts (deposit, withdraw, transfer, internal transfer), the accounts list and the
   transaction summary, because there a silent contract drift means wrong money on the screen.
5. **Every other `/api/*` response validates in development and test only**, gated on
   `import.meta.env` inside the same seam, because that catches mock drift in vitest and local
   integration drift with no production crash surface. The asymmetry is deliberate: making it
   uniform breaks either the money guarantee or the production margin.
6. **Two CI gates hold it.** `generate:zod` followed by `git diff --exit-code` proves the committed
   schemas are byte-reproducible from the document; an `AssertExtends` tuple proves under `tsc`
   that the generated `z.infer` types and the `openapi-typescript` types agree in both directions.
   A red gate means real drift: the source is fixed or reverted, never regenerated to quiet it.
7. **MSW mocks satisfy the real contract, not merely the tests**, because a test that passes
   against an invalid mock tests nothing. The development and test tier enforces it on every run.
8. **One shared `amountSchema`**: `makeAmountSchema` and `amountIsValid` are the declarative seam
   for an amount's validity in the money forms, because the checks were scattered and imperative.

## Rejected

- Rejected: hand-written Zod for the whole `/api/*` surface, because it duplicates a contract that
  is already machine-readable and gated, at a maintenance cost per response.
- Rejected: no runtime validation, the types trusted, because it left the BFF boundary unguarded.
- Rejected: uniform fail-closed validation, production included, because it turns every non-money
  contract wobble into a crash in front of the user.

## Consequences

- The auth boundary is validated and not assumed, a money receipt cannot render a shape the server
  did not promise, and mock drift is a red test.
- It costs a step that is easy to forget: a new response that carries money has to be added to
  the fail-closed set. `unwrap` is load-bearing and is not refactored casually.
- A contract drift on a money surface takes the feature down and shows no wrong number, on purpose.
- Not covered: the BFF surface is still outside the OpenAPI document. Its Zod schemas are
  hand-written, one mirror where there were two that could disagree.
- Not covered: the development and test tier catches only the drift that the mocks or the local
  stack exercise; any other contract change reaches users unvalidated on the non-money surfaces.

## Verified by

- `frontend/src/api/bffSchemas.test.ts` pins the BFF schemas.
- In `ci.yml`: `npm run generate:zod` then `git diff --exit-code src/api/generated/apiSchemas.ts`.
- `_GeneratedSchemasMatchSpec` in `frontend/src/api/responseSchemas.ts`, checked by `tsc -b`.

## Related

ADR-0007, ADR-0009, ADR-0019, ADR-0022, ADR-0043, ADR-0053.
