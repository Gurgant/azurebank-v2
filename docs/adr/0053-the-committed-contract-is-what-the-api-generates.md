# ADR-0053: The committed contract is what the API generates

**Status:** Accepted · **Date:** 2026-09-10 · Closes the half of the gap
[ADR-0043](0043-the-document-declares-the-error-body.md) recorded in its Context — *"proves
generated code matches the document and never that the document matches the server"* — that concerns
the document, and says plainly that it leaves the other half open. One test, no endpoint, no
production change, no new secret.

## Context

`docs/api/openapiv1.json` is generated from the API and committed, and everything on the frontend is
generated from it: `schema.d.ts`, the Zod runtime validators, the contract tests. Three claims hang
on that chain, and they are easy to run together:

1. the generated client code matches the committed document;
2. the committed document is what the code generates **today**;
3. what the code generates tells the truth about what the server **does**.

CI proved the first. Its two "up to date" steps regenerate the frontend artefacts FROM the committed
document and fail on a difference. Nothing in CI proved the second: a document that fell behind the
code regenerates to a matching stale output, and every gate stays green. The step's own comment said
the opposite — *"spec drift becomes a red build, never a runtime surprise"* — and it was not
measured.

**Measured 2026-09-10, not argued.** A property was added to `SetPinRequest` and the document left
alone. The `schema.d.ts` step and the Zod step, run exactly as `ci.yml` runs them, **both exited
0**.

A check that WOULD have noticed already existed, and nothing ran it:
`node scripts/openapi-spec.mjs check`, documented in `docs/api/README.md`. It compares the whole
document and exits 1 on any difference. It needs an API started in Development — which refuses to
start until every validated secret is configured — and the README that documented it said, in a
section this change then rewrote: *"It is not wired into CI, so a stale committed spec still passes
every gate."*

ADR-0043 had already named the gap while deciding something else, and that same README had already
said what closing it would take — *"running this `check` against a live API in the pipeline … This
script is what such a job would call;"* (quoted from `git show origin/main:docs/api/README.md`; the
section exists from `4f5c732` until this change). This record takes a different route, and D1 says
why.

## Decision

**D1 — A test in the backend suite, not the script wired into a workflow.**
`CommittedOpenApiDocumentTests` generates the document and compares it with the committed file.

The script was declined on WHERE it runs and on what it can SAY — not on what it compares, and an
earlier draft of this record said the opposite, which was wrong. Its `check` compares the entire
document, schemas included, and fails on any difference; only its REPORT is limited to prose, so for
a schema change it says *"the difference is elsewhere"* where this test names the JSON path. Where
it runs is the larger reason: it needs a running Development API with every secret, so a CI job for
it would have to start the API first — and the one workflow that already does, `contract-tests.yml`,
is `workflow_dispatch` only, so wiring the script there would still gate no pull request. A test
runs in the ordinary `dotnet test` gate, on every developer's machine and in CI's backend job, with
no API and no secrets. The script is kept: it is still the route for checking a server that is
actually running, and its prose report is still the better way to read what a regeneration did to
the words a human wrote.

**D2 — The document comes from the real composition, through `IOpenApiDocumentProvider`, and the
route does not matter.** The interface is public in Microsoft.AspNetCore.OpenApi 10.0.1 and is
registered by `AddOpenApi` in every environment; only `MapOpenApi` — the ROUTE — is gated to
Development. So the Testing host generates the document from the same `Program.cs` composition,
without HTTP, without changing its environment, and with the test keys the fixture already supplies.

This was measured before it was relied on, because a gate built on the wrong document would be green
and false, which is the state it exists to remove. The document the Testing host generates, the one
the API serves over HTTP in Development, and the committed file were **all 140,432 bytes and
byte-identical** once the committed file's line endings were normalised. A second fixture forced
into Development was declined: it changes the environment of a host to test one thing, and the
provider needs no route.

**D3 — Byte-exact, except for two things that belong to the machine rather than the contract.** The
committed file is read as bytes, so a BOM — which a text read would strip — is a difference.
Regeneration is deterministic; `NoServersDocumentTransformer` exists so that it is, because a
`servers` block would carry whichever host served the request. A semantic comparison would pass a
hand-edit that only reordered keys, which is still a document nobody generated. The two exceptions:

- **The file's line endings.** Git stores it as LF (`git ls-files --eol` reads `i/lf w/crlf`) and a
  Windows working tree rewrites all 4,307 of them to CRLF. That is why a local byte count of the
  checked-out file comes out 4,307 bytes larger than what the API serves: the size difference IS the
  line endings, and there is no other.
- **Newlines inside string values.** Twenty-four strings in the document — seven operation summaries
  and seventeen descriptions, 47 line breaks between them — carry the generating machine's newline,
  `\r\n` in the committed file, which was generated on Windows. A Linux runner is expected to
  produce `\n`. **That is predicted, not observed**: converting a controller to LF on Windows did
  NOT change the output, so the newline comes from the platform rather than from the source file,
  and only a Linux run can show which. Normalising both sides makes the gate hold either way —
  which is also why this pull request's CI run cannot settle it: a green backend job on Linux shows
  the gate HOLDS there, and says nothing about which newline Linux emitted, because the comparison
  normalises that away. The prediction stays a prediction until something reads the generated
  document on a Linux runner.

When the two differ, the failure names the first JSON paths that disagree, committed → generated,
and reports a pure formatting difference as one rather than blaming the file for it.

**D4 — The writer's culture is fixed, and it is the StringWriter that holds it.** Measured
2026-09-10: written through a `StringWriter` that formats in the current culture, under it-IT, de-DE
or fr-FR the six `multipleOf` constraints serialise as `0,01` — a bare JSON number with a comma,
which is invalid JSON. The transformers set a `decimal`; the library formats it through the
`TextWriter`'s format provider. The test builds its writer with `InvariantCulture`, and swapping
each in turn showed that this is the line that holds: pinning `CultureInfo.CurrentCulture` alone did
not. The pin stays as a second line of defence, for any transformer that formats text while the
document is being generated rather than written. If the generator ever does produce invalid JSON,
the test says so rather than surfacing a parser exception from line 3,347 of a document nobody is
looking at.

**D5 — Regeneration is the same test with a flag, and it always fails.**
`AZUREBANK_REGENERATE_OPENAPI=1` makes the test write the file instead of comparing it, and then
fail. A mode that wrote and passed would turn the gate into a no-op the moment the variable leaked
into a CI environment: the build would regenerate the document it was meant to check and report
agreement with itself. It writes the RAW generated text, under the fixed culture, so that on the
machine that has always regenerated this file it reproduces it byte for byte and never mixes a
newline change into a real one. It needs no running API and no secrets. The other route —
`node scripts/openapi-spec.mjs regen` against a running Development API — is kept and writes the
same bytes.

**D6 — What this does NOT prove, stated where the gate is defined.** It proves claim 2. It says
nothing about claim 3. ADR-0043's own defects were of that third kind — a transformer declaring 400s
on routes the router answers with 404, empty 401/403/404 bodies where the server writes JSON — and
this gate would have passed on every one of them, because the code generated the same error it
described. Truth about runtime behaviour is Schemathesis's job and the real-stack suites', and
Schemathesis is doubly not a gate: it ends its step with `|| true  # report, don't gate`, in a
workflow that only runs when somebody starts it by hand. Neither is changed here.

## Consequences

**Falsified before it was trusted, and every case re-run on the final code**, each read for the
reason it gave rather than its exit code:

- a property added to a DTO → red, naming
  `$.components.schemas.SetPinRequest.properties.zzFalsificationSentinel: only in the generated document`,
  while both frontend gates stayed green on the same tree;
- the committed title hand-edited → red, naming `$.info.title`, committed → generated;
- the committed file re-indented, same JSON → red, reported as a formatting difference;
- a BOM added to the committed file → red;
- the committed file's 47 in-string `\r\n` rewritten to `\n`, as Linux is expected to produce →
  green;
- the writer's culture switched to it-IT → red, with the readable invalid-JSON message;
- regeneration mode → failed as designed, wrote exactly the committed content (140,432 bytes, empty
  `git diff`), and the gate then passed without the flag.

⚠️ **Two of the first runs used a stale binary, and one of them failed for the wrong reason.** The
DTO was restored with `mv` from a backup taken before the mutation, which gave the source file the
backup's older timestamp — older than the DLL built with the mutation in it. MSBuild's incremental
check saw a source older than its output and did not recompile, so the next two runs still generated
the sentinel property, and the hand-edit falsification went red because of THAT. It was caught only
because the failure message was read. Recorded in `docs/engineering-traps.md`.

**A guard on the guard.** The test asserts the generated document carries at least 20 operations (27
today), so a provider resolved for the wrong name, or a host composed without its controllers,
cannot produce an empty document that agrees with an empty regeneration.

**Two defects found on the way, recorded and deliberately NOT fixed here**, because each changes the
product rather than the gate — ⚠️ *and the first of them is withdrawn, see the note under it:*

- ~~**The API serves invalid JSON on a server with a comma-decimal locale.** D4's bug is not the
  test's: `/openapi/v1.json` is written through the same library. Production and CI run on Linux
  with the invariant culture, so nothing is broken today; a developer serving it from an Italian,
  German or French Windows profile would get `0,01`.~~ *(WITHDRAWN 2026-09-11 — false. It was
  extrapolated from the TEST's own writer to the API's route, and the route was never measured.
  Measured now, through the real route: the API hosted in Development so `MapOpenApi` is mapped,
  with it-IT and then de-DE as the default culture of every thread, `GET /openapi/v1.json` returned
  200, a 140,392-character body containing `0.01` and never `0,01`, which parsed as JSON — the same
  as under en-US. The measurement is not vacuous: in the same run a thread-pool thread, which is
  where the request is served, formatted `0.01m` as `0,01` under both cultures, so the comma was
  due and the route still did not write one. `MapOpenApi` builds its writer with
  `InvariantCulture`. **D4 still stands**: it is about the test's writer, and a plain
  `StringWriter()` under it-IT does write `0,01` — which is exactly why the test pins its own.)*
- **The target meant to stop the XML-comment generator removes nothing.** `AzureBank.Api.csproj`
  removes the analyzer at `$(PkgMicrosoft_AspNetCore_OpenApi)/analyzers/…`, and that property is
  only defined when the package reference sets `GeneratePathProperty="true"`, which it does not — it
  evaluates empty, and the path matches no analyzer. Measured: `XmlComment` is in the compiled
  assembly, and not one `[EndpointSummary]` string reaches the document, although the project file
  says they "control Scalar/OpenAPI titles". It is why D3's newlines are in the contract at all.
  Fixing it would replace the published summaries across the contract, Scalar and the frontend
  types, which is a decision about content, not about this gate.

**`ci.yml`'s comment is corrected** to say what its steps actually prove and to point here.
**ADR-0043 carries a dated note** separating the half this closes from the half it does not, and the
documents that called themselves the only guard against an unregenerated constant — ADR-0046, and
the two architecture tests that read the committed file — now say they are not, and why they still
matter.

## What would change this

- **A divergence between the Testing and Development compositions.** The gate generates from the
  Testing host, measured identical to Development's. If the two ever differ it goes red — and
  regenerating from here would then copy the TEST host's view into the contract and make the failure
  go quiet while the contract described a host nobody deploys. A red naming a path the change under
  review did not touch is that case: fix the divergence, do not regenerate. The test's message says
  so.
- **A second document.** `v1` is resolved by name. A `v2` would need its own committed file and its
  own case, and the operations floor would stop meaning what it says.
- **Gating Schemathesis.** If runtime conformance were ever made a gate, D6's paragraph would move,
  and claim 3 would stop being the open one.
- ~~**Either defect above being fixed.** The XML-generator fix would remove D3's in-string newlines
  from the contract altogether and make that normalisation dead code; the culture fix in the product
  would make D4's pin redundant but not wrong.~~ **The XML-generator defect above being fixed.** It
  would remove D3's in-string newlines from the contract altogether and make that normalisation dead
  code. *(Corrected 2026-09-11: the struck bullet also named a culture fix in the product, and there
  is none to make — the product defect it assumed is withdrawn, above — so D4's pin stays, guarding
  the test's own writer.)*
