# ADR-0043: The published document declares the error body the API actually sends

**Status:** Accepted · **Date:** 2026-08-18 · **Amended:** 2026-09-10 (ADR-0053), 2026-09-11
(decision 5, ADR-0050), 2026-09-15 (ADR-0053, D6) · **Corrects** the generation pipeline of
ADR-0029 and ADR-0023, which both consume `docs/api/openapiv1.json`; supersedes neither

## Context

`docs/api/openapiv1.json` is generated from the API and committed, and the frontend's `schema.d.ts`
and runtime Zod validators are generated from it: a claim in that file is a type a client compiles
against. Without this decision the document declares 401, 403 and 404 with no body where the API
answers `application/json` with seven keys, declares responses the code cannot produce, and gives
the shared `ProblemDetails` five members and neither of the two the client branches on. The causes:
`errorCode` and `traceId` live in `ProblemDetails.Extensions`, a dictionary that reflection cannot
see into, and response transformers assign over what the endpoints declare. No gate sees it: the
drift gate proves generated code matches the document, never that the document matches the server.

## Decision

The document declares the body, the shared component declares every member the API sends, and a
response nobody can produce is deleted, not described.

1. **`errorCode` and `traceId` go on the shared `ProblemDetails` component**, through a document
   transformer (`ProblemDetailsExtensionsTransformer`), because patching one component corrects
   every response that points at it and a new endpoint inherits the truth.
2. **Both are optional**, because a model-state failure is one of the shapes a 400 on this
   component answers: `POST /api/auth/register` with a malformed body returns
   `{type, title, status, errors, traceId}` and no `errorCode`. Requiring it would publish a
   contract wider than the code.
3. **Transformers fill in, never override**: where an endpoint declares a status itself, its
   declaration stands, because an assignment discards it (`TransferController` names three step-up
   codes on its 401). Only the description is completed, and only when it is the bare HTTP reason
   phrase, which is what ApiExplorer writes when nobody wrote anything.
4. **A response the endpoint cannot produce is removed.** No transformer adds a 400 to a route
   whose only input is its path, because a `{id:guid}` constraint takes part in route matching: a
   non-GUID matches no route and the framework answers 404 before model binding exists to fail.
   The handle lookup declares no 404, because ADR-0014's enumeration-neutral oracle answers 200
   with `exists: false`; the endpoint says so with `[AlwaysFound]`.
5. **An extension member is declared where it rides**, on the operation's own inline schema, and
   the shared component is left alone: the four members of the day's ceiling on the two
   operations that can answer that code (ADR-0050), and `available` and `requested` of
   `INSUFFICIENT_FUNDS` on the inline 422 of the three money moves.

## Rejected

- Rejected: two components, `ProblemDetails` and a `ValidationProblemDetails`, because both shapes
  arrive at the same declared response: seventeen 400s say `typeof(ProblemDetails)` and can answer
  either, so the split becomes a `oneOf` across seventeen call sites and a broad change to the
  generated frontend types, to express what one optional member expresses exactly.
- Rejected: an empty 401 or 403 on the grounds that the JWT Bearer middleware answers with no body,
  because this API's `OnChallenge` calls `context.HandleResponse()` and writes JSON.

## Consequences

- No refusal is published with an empty body, and a response carries the endpoint's own body and
  codes in place of a transformer's prose.
- A generated client can branch on `errorCode`: the `ProblemDetails` of `apiSchemas.ts` carries
  both members. The hand-written `ProblemDetailsBody` in `problemBaseQuery.ts` stays, because it
  also models members that the BFF adds and the API's document cannot describe.
- Two guards hold it. `PublishedErrorContractTests` reads the committed document and refuses an
  empty refusal body, a missing `errorCode` member and a required one.
  `errorContract.contract.test.ts` asserts the same facts against the answering stack, on both
  targets, so a mock that stops sending `errorCode` fails the build.
- Which gate sees what: that the committed document is what the code generates is ADR-0053's
  test; that what the code generates is what the server does is seen only by guards on content,
  the two above, and by Schemathesis, which gates every pull request (ADR-0053, D6).
- Not covered: `node scripts/openapi-spec.mjs check`, which compares the committed document with a
  running API, is run by no job. A stale commit of the document fails the backend suite anyway.

## Verified by

- `PublishedErrorContractTests` and `PublishedRefusalCodesTests` (decision 5).
- `frontend/src/contract/errorContract.contract.test.ts`, against the mock and the real stack.

## Related

ADR-0014, ADR-0023, ADR-0029, ADR-0050, ADR-0053.
