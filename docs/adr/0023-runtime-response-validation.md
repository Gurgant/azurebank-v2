# ADR-0023: Runtime response validation — spec-generated Zod, fail-closed on money

**Status**: Accepted

**Date**: 2026-07-25

**Decision Makers**: Vladislav Aleshaev

---

## Context

Everything on `/api/*` is guarded by three layers already: `openapi-typescript` types, the
`schema.d.ts` drift gate in CI, and Schemathesis contract tests. The BFF surface is different —
`/bff/auth/*` is **not in the OpenAPI spec at all**, so its response types were hand-written
mirrors of `BffResponses.cs`. That made the auth boundary the weakest one in the system: a silent
FE/BFF drift would flow into the auth slice with nothing to catch it.

This ADR also exists to correct a recorded decision that has since become false. **ADR-0019
Decision 6** states that `src/api/bffTypes.ts` is a hand-written mirror and calls that "the
accepted cost until the BFF surface joins the spec". The direction of truth has inverted:
`bffTypes.ts` now begins `import type { z } from 'zod'` and its own header says the Zod schemas are
the single source of truth. A contributor reading only ADR-0019 would "restore" hand-written types
and silently delete a live runtime guard. **A wrong ADR is more dangerous than a missing one.**

## Decision

The two surfaces get different treatment, and items 1 and 4–5 below must be read as scoped to one
each: **the BFF surface** (`/bff/auth/*`) is fail-closed in its entirety, because it is small and
every response on it gates authentication; **the API surface** (`/api/*`) is fail-closed only where
money is displayed. Confusing the two produces either a needless production crash surface or a
missing guard.

1. **On the BFF surface, schemas are the source of truth and validation is fail-closed —
   everywhere, production included.** `src/api/bffSchemas.ts` holds the Zod schemas and
   `src/api/bffTypes.ts` is `z.infer` of them, so the type and the validator cannot disagree. All
   five responses — `getMe`, `login`, `register`, `session-status`, `verify-pin` — parse through
   the `unwrap(envelope, schema?)` seam with no environment gate. This is the boundary with no
   spec behind it, so it gets the strictest treatment. `location.state` and error bodies are the
   exception and use `safeParse` with a fallback — soft on purpose, because a garbled error
   payload should not replace the error it was carrying.

2. **Zod for `/api/*` is generated, never hand-written.** `typed-openapi` with
   `--runtime zod --schemas-only`, output **committed** to `src/api/generated/`, behind
   `npm run generate:zod` sitting next to `generate:api`. Hand-writing validators for a surface that
   already has a machine-readable contract duplicates the contract and guarantees eventual drift.

3. **The generators that were rejected, and why.** This is the part that is expensive to re-derive,
   so all four are recorded. **`openapi-zod-client` is a dead end** — stale for roughly 17 months,
   wired to Zod 3, its v4 pull request abandoned — and it is the obvious first search result, which
   is precisely why the rejection has to be written down. `kubb` carries three-package configuration
   overhead. `orval`'s Zod-4 migration was still settling. `hey-api` is an acceptable fallback if
   schema-filtering needs ever grow.

4. **On the API surface, fail-closed in production is limited to the money surfaces**: the four
   mutation receipts (deposit, withdraw, transfer, internal transfer), the accounts list, and the
   transaction summary — about six schemas. These are the responses where silent contract drift
   means *wrong money on the screen*.

5. **Every other `/api/*` response validates in development and test only**, gated on
   `import.meta.env` inside the same seam. This catches MSW mock drift in vitest and local integration drift with **zero
   production crash surface**. The asymmetry is deliberate: someone who sees a conditional and
   "simplifies" it, or who decides validation ought to be uniform, breaks either the money guarantee
   or the production safety margin depending on which way they go.

6. **Two CI gates hold it.** `generate:zod` followed by `git diff --exit-code` proves the committed
   schemas are byte-reproducible from the spec; a 12-way `AssertExtends` tuple proves the generated
   `z.infer` types and the `openapi-typescript` types agree **in both directions** under `tsc`. A
   red gate means real drift: fix the source or revert. Never regenerate-and-commit to quiet it.

7. **MSW mocks must satisfy the real contract, not merely satisfy the tests.** The dev/test tier
   enforces this automatically, and its first run proved the point: 23 failures, every one a
   legitimate mock-contract violation — non-hex thirteen-character fake UUIDs, partial receipt
   stubs, `'x'`/`'y'` placeholder account ids. Tests that pass against invalid mocks are testing
   nothing.

8. **One shared `amountSchema`.** `makeAmountSchema` / `amountIsValid` is the declarative seam for
   money-form amount validity, replacing checks that were previously scattered and imperative.

**Amendment to ADR-0019.** Decision 6 of ADR-0019 is superseded by items 1 and 2 above: BFF
response types are inferred from Zod schemas, not hand-mirrored. ADR-0019 carries the reciprocal
marker.

## Alternatives considered

**Hand-writing Zod for the whole `/api/*` surface.** Rejected: it duplicates a contract that is
already machine-readable and already gated, at a per-response maintenance cost, and it would drift
from the spec the first time someone was in a hurry.

**No runtime validation at all, trusting the types.** This was the status quo, and it is what left
the BFF boundary unguarded — TypeScript types are erased at runtime and assert nothing about what
the server actually sent.

**Uniform fail-closed validation everywhere, production included.** Rejected: it converts every
non-money contract wobble into a user-facing crash. The money surfaces are worth that trade; a
transaction-list field is not.

## Residuals (accepted, documented)

- **The BFF surface is still outside the OpenAPI spec.** Its Zod schemas are hand-written, so they
  are a *mirror* like the types were — the improvement is that there is now exactly one mirror
  instead of two that could disagree. Bringing the BFF into the spec remains backlog.
- **Fail-closed means a contract drift on a money surface takes the feature down** rather than
  showing a wrong number. That is the intended trade and it is worth restating: the failure is
  loud on purpose.
- **The dev/test tier can only catch drift the mocks or the local stack exercise.** A production
  contract change that no test covers still reaches users unvalidated on non-money surfaces.
- The 23-failure result is n=1 on one repository — illustrative of the mechanism working, not
  evidence of a general effect size.

## Consequences

**Positive** — the auth boundary is validated instead of assumed; the money receipts cannot render
a shape the server did not promise; mock drift surfaces as a red test rather than as confidence in
a test that proves nothing; the generated schemas are reproducible from the spec by construction.

**Negative** — adding a money-bearing response now means adding it to the fail-closed set, which is
a step that is easy to forget; `unwrap` is load-bearing and must not be casually refactored;
`mocks/handlers.ts` and `mocks/state.ts` are frozen files partly because of item 7.

This ADR also formally retires three surviving instructions in archived planning documents that
told an implementer to decline a correct review suggestion about adopting runtime validation. Those
documents are superseded on this point.

## Verification

`frontend/src/api/bffSchemas.test.ts` pins the BFF schema contract. The full suite exercises the
dev/test validation tier on every run — a mock that violates the contract fails the suite rather
than passing quietly. In CI, the `generate:zod` drift gate and the `AssertExtends` tuple both fail
the build on divergence.

## Related

- **ADR-0019** — this ADR amends its Decision 6.
- **ADR-0007** — FluentValidation, the server-side contract these schemas mirror.
- **ADR-0009** — the money protocol whose receipts are the fail-closed set.
- **ADR-0022** — the client money-mutation protocol that consumes these validated responses.
