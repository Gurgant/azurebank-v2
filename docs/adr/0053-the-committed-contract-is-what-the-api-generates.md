# ADR-0053: The committed contract is what the API generates

**Status:** Accepted · **Date:** 2026-09-10 · Closes the gap
[ADR-0043](0043-the-document-declares-the-error-body.md) recorded in its Context — *"proves
generated code matches the document and never that the document matches the server"* — for the half
of that sentence that concerns the document, and says plainly that it leaves the other half open.
One test, no endpoint, no production change, no new secret.

## Context

`docs/api/openapiv1.json` is generated from the API and committed, and everything on the frontend is
generated from it: `schema.d.ts`, the Zod runtime validators, the contract tests. Three claims hang
on that chain, and they are easy to run together:

1. the generated client code matches the committed document;
2. the committed document is what the code generates **today**;
3. what the code generates tells the truth about what the server **does**.

CI proved the first. Its two "up to date" steps regenerate the frontend artefacts FROM the committed
document and fail on a difference. Nothing proved the second: a document that fell behind the code
regenerates to a matching stale output, and every gate stays green. The step's own comment said the
opposite — *"spec drift becomes a red build, never a runtime surprise"* — and it was not measured.

**Measured 2026-09-10, not argued.** A property was added to `SetPinRequest` and the document left
alone. The `schema.d.ts` step and the Zod step, run exactly as `ci.yml` runs them, **both exited
0**. The only thing that would have noticed was a manual step: `node scripts/openapi-spec.mjs
check`, documented in `docs/api/README.md` and run by hand against an API started in Development —
which refuses to start until every validated secret is configured. That README was candid about it,
in a section this record's own change then rewrote: *"It is not wired into CI, so a stale committed
spec still passes every gate."* (The sentence is quoted from the file as it stood before this
change; `git show` on any earlier commit has it.)

ADR-0043 had already named this, while deciding something else, and `docs/api/README.md` had already
said what closing it would take — in the same section, since rewritten: *"running this `check`
against a live API in the pipeline … This script is what such a job would call."* This record takes
a different route, and D1 says why.

## Decision

**D1 — A test in the backend suite, not a step in a workflow.** `CommittedOpenApiDocumentTests`
generates the document and compares it with the committed file. A test runs in the ordinary
`dotnet test` gate, on every developer's machine as well as in CI, and it fails with a message the
developer can act on. A workflow step would have run only in CI, and only in a job that already
starts the API with secrets. The obvious place for one was sitting there: `contract-tests.yml`
already runs the API in Development, so `/openapi/v1.json` was already being served in CI, and
nothing fetched it.

**The route the README anticipated — wiring `scripts/openapi-spec.mjs check` into that job — was
declined on what the script compares, not on where it runs.** Its `check` reports hand-written
PROSE: summaries, operation descriptions and response descriptions. By its own account it *"does not
compare schemas, parameters, examples, tags or security"*, and reports a change confined to those as
"the difference is elsewhere". A property added to a DTO — the falsification below — is exactly such
a change: the script would have said something moved and not what. This test compares every byte and
names the path. The script is kept, not deleted: it is still the route for checking against a server
that is actually running, and its prose report is still the better way to read what a regeneration
did to the words a human wrote.

**D2 — The document comes from the real composition, through `IOpenApiDocumentProvider`, and the
route does not matter.** The interface is public in Microsoft.AspNetCore.OpenApi 10.0.1 and is
registered by `AddOpenApi` in every environment; only `MapOpenApi` — the ROUTE — is gated to
Development. So the Testing host generates the document from the same `Program.cs` composition,
without HTTP, without changing its environment, and with the test keys the fixture already supplies.

This was measured before it was relied on, because a gate built on the wrong document would be green
and false, which is the state it exists to remove. The document the Testing host generates, the one
the API serves over HTTP in Development, and the committed file were **all 140,432 bytes and
byte-identical** once the committed file's line endings were normalised. The alternative — a second
fixture forced into Development — was declined: it changes the environment of a host to test one
thing, and the provider needs no route.

**D3 — Byte-exact, with only the line endings normalised.** Regeneration is deterministic and was
measured to reproduce the file byte for byte; `NoServersDocumentTransformer` exists precisely so
that it does, because a `servers` block would carry whichever host served the request. A semantic
comparison would pass a hand-edit that only reordered keys, which is still a document nobody
generated. The line endings are the exception because they belong to the checkout, not the contract:
git stores this file as LF (`git ls-files --eol` reads `i/lf w/crlf`) and a Windows working tree
rewrites all 4,307 of them to CRLF — which is the entire 4,307-byte difference a naive local `cmp`
reports.

When the two differ, the failure names the first JSON paths that disagree, committed → generated,
and distinguishes a reformat (the two parse to the same JSON) from a real change. A 140 KB string
printed twice tells nobody where to look.

**D4 — Regeneration is the same test with a flag, and it always fails.**
`AZUREBANK_REGENERATE_OPENAPI=1` makes the test write the file instead of comparing it, and then
fail. A mode that wrote and passed would turn the gate into a no-op the moment the variable leaked
into a CI environment: the build would regenerate the document it was meant to check and report
agreement with itself. Failing after the write means the flag can only ever make a build red. It
replaces the manual curl, which needed a running API and every secret; this needs neither.

**D5 — What this does NOT prove, stated where the gate is defined.** It proves claim 2. It says
nothing about claim 3. ADR-0043's own defects were of that third kind — a transformer declaring 400s
on routes the router answers with 404, a response type the endpoint never writes — and this gate
would have passed on every one of them, because the code generated the same error it described.
Truth about runtime behaviour is Schemathesis's job and the real-stack suites', and Schemathesis
still ends its step with `|| true  # report, don't gate`. That is not changed here: whether to gate
it is a separate decision with its own cost in flakiness, and folding it in would make this record
claim more than it measured.

## Consequences

**Falsified four ways before it was trusted**, each read for the reason it gave rather than its exit
code:

- a property added to a DTO → red, naming
  `$.components.schemas.SetPinRequest.properties.zzFalsificationSentinel: only in the generated
  document`, while both frontend gates stayed green on the same tree;
- the committed title hand-edited → red, naming `$.info.title`, committed → generated;
- the committed file re-indented, same JSON → red, reporting a reformat rather than a change;
- regeneration mode → failed as designed, wrote exactly the committed content (140,432 bytes, empty
  `git diff`), and the gate then passed without the flag.

⚠️ **One of those four was first run against a stale binary, and it failed for the wrong reason.**
The DTO was restored with `mv` from a backup taken before the mutation, which gave the source file
the backup's older timestamp — older than the DLL built with the mutation in it. MSBuild's
incremental check saw a source older than its output and did not recompile, so the next two runs
still generated the sentinel property, and the hand-edit falsification went red because of THAT. It
was caught only because the failure message was read. Recorded in `docs/engineering-traps.md`.

**A guard on the guard.** The test asserts the generated document carries at least 20 operations (27
today), so a provider resolved for the wrong name, or a host composed without its controllers,
cannot produce an empty document that agrees with an empty regeneration.

**`ci.yml`'s comment is corrected** to say what its steps actually prove and to point here, and
**ADR-0043 carries a dated note** separating the half this closes from the half it does not.

## What would change this

- **A Testing-only endpoint or transformer.** The gate generates from the Testing composition, which
  was measured identical to Development's. If the two ever diverge it goes red — loud, and pointing
  at the divergence, which is the safe direction but not a silent one.
- **A second document.** `v1` is resolved by name. A `v2` would need its own committed file and its
  own case, and the operations floor would stop meaning what it says.
- **Gating Schemathesis.** If runtime conformance were ever made a gate, D5's paragraph would move,
  and claim 3 would stop being the open one.
