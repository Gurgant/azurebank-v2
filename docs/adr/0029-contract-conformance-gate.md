# ADR-0029: One suite, two backends — making mock drift fail the build

**Status:** Accepted · **Date:** 2026-07-31 · **Amended:** 2026-08-10 (decision 6), 2026-08-18
(decision 1, ADR-0043) · **Decision Makers:** Vladislav Aleshaev

## Context

The frontend's unit tests have one oracle: MSW. Every assertion about a status code, an
`errorCode`, a body shape or a header is checked against `handlers.ts`, which is written by reading
the C#. When test and mock agree with each other and neither agrees with the server, the suite is
green and wrong, and that state looks exactly like correct. It happened: the mock answered 422 on
an inverted date pair where the API answers 400, and a test asserted `errorCode: 'SERVER_ERROR'`
where the stack emits `HTTP_502`. The happy paths are faithful, because a browser exercises them;
the drift concentrates in error paths, which nothing exercises.

## Decision

1. **One assertion suite runs twice, against MSW and against the real API and BFF**, because a
   divergence then fails the build. A third party joins both runs (ADR-0043):
   `errorContract.contract.test.ts` holds the committed `docs/api/openapiv1.json` to the same
   answers.
2. **The URLs are identical for both targets, and no test branches on the target**, because the
   mock registers every handler with a wildcard origin: a request aimed at the BFF's address is
   intercepted when MSW listens and reaches the real server when it does not. Only fixtures differ
   (the mock seeds `demo@azurebank.dev`, the dev database `admin@azurebank.dev`).
3. **Every expected value is measured on the real stack and pasted beside its assertion, never
   inferred**, because an assertion written from the mock re-creates the problem inside the thing
   meant to detect it.
4. **The target is carried in `test.env` by a second config file, not by a shell variable**,
   because `CONTRACT_TARGET=real vitest` is POSIX-only: PowerShell and cmd fail before vitest
   starts, so the real target would be unrunnable on Windows.
5. **`real` fails when the stack is down; it never skips**, because a skipped suite reports success
   without having asked the backend anything.
6. **The contract suite is excluded from the default `npm test` and runs through
   `test:contract:mock` and `test:contract:real`**, because a unit run must never depend on a live
   stack. Both run in CI: the real target proves the assertions match the backend, and the mock
   target, a step of the frontend job that needs no stack, proves the mock still matches them.
7. **Positive controls are included: the success envelopes, on which mock and backend already
   agreed**, because a gate made only of known failures cannot tell "the mock was fixed" from
   "the assertion was written to match whatever the mock did".

## Rejected

- Rejected: assertions written from the mock or from reading the C#, because that is the oracle
  that drifted.
- Rejected: skipping the real target when nothing answers, because green would then mean nothing.
- Rejected: signing in before each test, because the BFF limits auth to 10 requests per 60 s per
  IP: the suite signs in once per file, and a 429 raises an explicit message.
- Rejected: seeding a session in each page test, because every file is a chance to forget:
  signed-in is the default test state, the only one reachable behind `ProtectedRoute`.

## Consequences

- Every drift the gate has found was fixed in the mock; the backend was right each time.
- The mock gates `/api/*` on a live session, as the real stack does: with no session the answer is
  `401` `AUTH_TOKEN_MISSING`, and not the step-up 403, because authentication precedes the level
  check; the `401` with no `errorCode` belongs to `/bff/auth/*`. Tests whose subject is the
  signed-out path say so explicitly.
- The API has two validation envelopes, and the mock follows both. An endpoint with a
  FluentValidation validator answers `ValidationExceptionHandler`'s body (title "Validation
  Failed", with a `detail`); one without is rejected by `[ApiController]` model state (title "One
  or more validation errors occurred.", no `detail`).
- On the BFF's PIN routes ASP.NET binds and validates the model before the action reads the
  session, so a malformed PIN is a `400` with or without one; and ASP.NET's query binding is
  case-insensitive. The mock does both.
- Not covered: the gate is a floor, not full coverage. What a static audit of the mock flagged
  and no live run checked is a lead, not a fact.
- Not covered: case-insensitive query binding itself: the tests assert the app's own PascalCase.
- Not covered: a session that dies by clock and not by revocation: it is modelled like the forms
  that were measured.

## Verified by

- `npm run test:contract:mock` and `npm run test:contract:real` (`frontend/src/contract/`).
- `authGuards.contract.test.ts`, `validation.contract.test.ts`, `errorContract.contract.test.ts`,
  and `envelopes.contract.test.ts` (the positive controls).

## Related

ADR-0030, ADR-0031, ADR-0032, ADR-0043.
