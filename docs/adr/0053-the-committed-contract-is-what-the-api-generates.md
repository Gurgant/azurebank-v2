# ADR-0053: The committed contract is what the API generates

**Status:** Accepted · **Date:** 2026-09-10 · **Amended:** 2026-09-11, 2026-09-14, 2026-09-15 (D6),
2026-09-21 (ADR-0056), 2026-09-24 (D6), 2026-09-28 (ADR-0057), 2026-10-04 (ADR-0063)

## Context

`docs/api/openapiv1.json` is generated from the API and committed, and the frontend is generated
from it: `schema.d.ts`, the Zod runtime validators, the contract tests. Three claims hang on that
chain: (1) the generated client code matches the committed document; (2) the committed document is
what the code generates today; (3) what the code generates is what the server does. CI's two "up to
date" steps prove the first, by regenerating the frontend artefacts from the committed document. A
document that fell behind the code regenerates to a matching stale output, so those steps stay green
and nothing proves the second. ADR-0043 names the gap; this record closes its document half.

## Decision

- **D1: A test in the backend suite, not the script wired into a workflow.**
  `CommittedOpenApiDocumentTests` generates the document and compares it with the committed file,
  because a test runs in the ordinary `dotnet test` gate, locally and in CI, with no API to start.
- **D2: The document comes from the real composition, through `IOpenApiDocumentProvider`**, because
  `AddOpenApi` registers it in every environment (public in Microsoft.AspNetCore.OpenApi 10.0.1) and
  only the route, `MapOpenApi`, is gated to Development: the Testing host generates without HTTP.
- **D3: Byte-exact, except for two things that belong to the machine and not to the contract**: the
  file's line endings (Git stores LF, a Windows working tree writes CRLF) and the newlines inside
  string values (`\r\n` in the committed file, `\n` expected from a Linux runner). The file is read
  as bytes, so a BOM is a difference; `NoServersDocumentTransformer` keeps regeneration
  deterministic. A failure names the first JSON paths that differ, committed → generated.
- **D4: The writer's culture is fixed, and it is the `StringWriter` that holds it**: the test builds
  it with `InvariantCulture`, because under it-IT, de-DE or fr-FR a plain writer serialises the
  `multipleOf` constraints as `0,01`, which is invalid JSON. Pinning `CultureInfo.CurrentCulture`
  alone does not hold; it stays as a second line of defence.
- **D5: Regeneration is the same test with a flag, and it always fails.**
  `AZUREBANK_REGENERATE_OPENAPI=1` writes the raw generated text and then fails, because a mode that
  wrote and passed would make the gate a no-op the moment the variable leaked into CI.
- **D6: This proves claim 2 and nothing about claim 3, whose guard is Schemathesis**, because a
  generator that describes an answer the server never sends agrees with its own document. The
  `conformance` job in `ci.yml` runs every Schemathesis check against the running API on every pull
  request, through `tests/contract/schemathesis.toml` and its hooks, with its own SQL Server, a
  bearer token from the real login, the version pinned at 4.27.1 and no `|| true`. A floor of 31
  operations in its JUnit report fails a run that tested nothing. Bruno runs on each pull request.

## Rejected

- Rejected: `node scripts/openapi-spec.mjs check` in a workflow, because it needs a running
  Development API with every secret and names no JSON path. The script is kept for a running server.
- Rejected: a second test fixture forced into Development, because it changes a host's environment
  to test one thing, and the provider needs no route.
- Rejected: a semantic comparison, because it passes a hand-edit that only reorders keys.

## Consequences

- A stale committed document fails the backend suite on every run, local and CI alike.
- Guard on the guard: the test asserts at least 20 operations, so an empty document cannot agree
  with an empty regeneration; 20 is below the real count on purpose, so that a deletion passes.
- The served document has no culture defect (the one once recorded here is withdrawn): `MapOpenApi`
  builds its own writer with `InvariantCulture`. D4 is about the test's writer only.
- Operation titles: each action's `<summary>` is a short title, held by
  `PublishedOperationTitlesTests` to one line of at most fifty characters, and the prose is in
  `<remarks>`. The `[EndpointSummary]` attributes never reached the document and are deleted.
- What D6's `conformance` job catches and this test cannot: its first run found `415` on every
  body-taking operation and a route-miss `404` as `application/problem+json`, neither declared, and
  a `500` from `PATCH /api/accounts/{id}/set-primary` on SQL Server. Its configuration declares the
  two decided 404s (ADR-0056 D4, ADR-0042) and the transaction list's unknown query parameter.
- Not covered: the newline a Linux runner emits is predicted, not observed: the comparison hides it.

## Revisit when

- The Testing and Development compositions, measured identical, diverge: the gate goes red on a path
  the change did not touch. The divergence is fixed, not regenerated into the contract.
- A second document: `v1` is resolved by name, and a `v2` needs its own file, case and floor.

## Verified by

- `CommittedOpenApiDocumentTests`, `PublishedOperationTitlesTests`, `SetPrimarySqlServerTests`.
- The `conformance` job in `.github/workflows/ci.yml`.

## Related

ADR-0042, ADR-0043, ADR-0046, ADR-0056, ADR-0057, ADR-0063.
