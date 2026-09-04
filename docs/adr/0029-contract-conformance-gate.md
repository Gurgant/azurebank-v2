# ADR-0029: One suite, two backends — making mock drift fail the build

**Status**: Accepted. The hole this ADR was written admitting — live-session authentication on
`/api/*` — is CLOSED. The gate remains a floor rather than full coverage; see “What is NOT covered”.

**Date**: 2026-07-31

**Decision Makers**: Vladislav Aleshaev

---

## Context

The frontend suite had exactly one oracle: MSW. Every assertion about a status code, an `errorCode`,
a body shape or a header was checked against `handlers.ts`, and `handlers.ts` was written by reading
the C# and believing the reading. When those two agree with each other and neither agrees with the
server, the suite is green and wrong — the worst possible state, because it is indistinguishable
from correct.

That is not hypothetical here. Three investigations turned up six confirmed drifts, and every one of
them needed the real stack to see:

- **PR #64** — the mock answered 422 on an inverted date pair; the API answers **400**.
- **PR #66** — a test asserted `{status: 502, errorCode: 'SERVER_ERROR'}`, a shape nothing in this
  system emits. The real gateway sends `502` with `Content-Length: 0`, RTK's json handler maps the
  empty body to `null` (not a parse failure), and `toApiProblem` synthesises `HTTP_502`.
- A static audit then raised **48 more candidates**, of which 4 were adversarially confirmed.

Every one was invisible to the existing suite by construction. The pattern is also clear: the happy
paths are faithful, because they are exercised in a browser; the drift concentrates in error paths,
which nothing exercises.

## Decision

**1. One assertion suite, run twice — against MSW and against the real API+BFF.** Same files, same
expectations. A divergence becomes a failing build instead of a discovery three PRs later.

**2. The URLs are identical for both targets, and that is what makes it cheap.** The mock registers
every handler with a wildcard origin, so a request aimed at the BFF's address is intercepted when
MSW is listening and reaches the real server when it is not. No assertion knows which answered, and
no test may branch on the target — the moment one does, it stops being a contract test and becomes
two different tests sharing a name. Only *fixtures* differ (the mock seeds `demo@azurebank.dev`, the
dev database seeds `admin@azurebank.dev`), and fixtures are not contract.

**3. Every expected value is MEASURED and pasted in, never inferred.** Each assertion carries the
observed response next to it. This is the rule the whole exercise exists to enforce; an assertion
written from the mock would re-create the problem inside the thing meant to detect it.

**4. The target is carried in `test.env` via a second config file, not a shell variable.**
`CONTRACT_TARGET=real vitest` is POSIX-only syntax, and this repo is developed on Windows. An
earlier draft of this decision claimed the prefix form would "quietly run against the mock and
report success" — that is wrong, and measuring it is what showed so:

    PowerShell -> The term 'CONTRACT_TARGET=real' is not recognized as a name of a cmdlet...
    cmd        -> 'CONTRACT_TARGET' is not recognized as an internal or external command

It dies before vitest starts. So the real cost is not a false pass but an **unrunnable** real target
on the platform the work happens on — which is reason enough, without the scarier story. Two configs
work identically on every shell and add no dependency.

**5. `real` FAILS when the stack is down. It never skips.** A skipped suite reports success without
having asked the backend anything.

**6. The contract suite is excluded from the default `npm test`.** CI and a plain unit run must never
depend on a live stack. It runs through `test:contract:mock` and `test:contract:real`.

> **Amendment (2026-08-10).** That sentence described the scripts, not the workflow, and for months
> only one of the two was wired in: `test:contract:real` ran in the real-stack job and
> `test:contract:mock` ran in **no job at all**. So this record's own title — *making mock drift fail
> the build* — was half true. The real target proved the assertions still matched the backend; nothing
> proved the MOCK still matched those same assertions, while every unit test in the suite uses that
> mock as its oracle. The mock target is now a step in the frontend job, where it costs ~2.5s and
> needs no stack.

**7. Positive controls are included deliberately.** A gate made only of known failures cannot
distinguish "the mock was fixed" from "the assertion was written to match whatever the mock did".

## Consequences

**The gate has found ELEVEN drifts so far, and the mock was aligned to the backend in every case.** The
backend was right every time; nothing here is a backend fix. Six came from the first run; the rest
turned up while answering review on the gate itself, which is the behaviour you want from one.

| Drift | Mock said | Backend says |
|---|---|---|
| PIN routes with no session | `200`, and set `authLevel = 2` | `401`, no `errorCode` |
| Same-account internal transfer | `422 SAME_ACCOUNT_TRANSFER` | `400` + `errors.toAccountId`, no code |
| Transactions validation envelope | "Validation Failed" + `detail` | "One or more validation errors occurred.", **no detail** |
| Pagination error keys | `pageSize` | `PageSize` |
| Malformed PIN, no session | `401` (session read first) | `400` — model validation runs BEFORE the action |
| Malformed PIN error key/envelope | `pin`, validator envelope | `Pin`, framework envelope |
| Query key binding | case-SENSITIVE (`pageSize` ignored) | case-insensitive |
| Several bad properties at once | first failure only, rest hidden | ALL reported in one pass |
| Bad-value message | `The value 'x' is not valid.` | `... is not valid for <Name>.` |
| PIN absent vs null vs malformed | one message for all three | `$` / `Pin` required / `Pin` 6-digits |
| No session on `/api/*` | reachable, or the BFF's own 401 | `401` `AUTH_TOKEN_MISSING` (the API's) |

**The API has TWO validation envelopes, and that is the finding with the longest reach.** Endpoints
with a FluentValidation validator get `ValidationExceptionHandler`'s hand-written body (title
"Validation Failed", a `detail`, a bare 32-hex `traceId`); endpoints without one are rejected by
`[ApiController]` model state and rendered by the framework (title "One or more validation errors
occurred.", **no `detail` member**, a W3C traceparent). `TransactionController` injects validators
only for deposit and withdraw, so its list and summary actions take the second path. The mock used
the first for everything — which is why `HistoryPage`, which renders `problem.detail` for exactly
that endpoint, showed a sentence under MSW and its generic fallback in production.

**The PIN drift was the dangerous one, and it is worth being precise about why.** The mock answered
`200` to a caller with no session at all AND elevated itself to level 2 — and ~~the elevation level
is the only thing the money handlers consult~~ (struck 2026-09-04; note below) the level was then
the only thing the money handlers consulted, so a nonexistent session could be walked up to elevated
and used to push a full transfer. **The real BFF is correct**, with one qualification this ADR
should make rather than gloss: for a MODEL-VALID request it reads the session and never consults the
PIN. A malformed body does not get that far at all — validation runs ahead of the action, so it is a
400 whether or not a session exists (see the ordering measured below). Saying "reads the session
first" flatly would contradict that, and this document is about being exact. Either way it was a
mock-permissiveness problem, never a production hole.

*(Correction 2026-09-04: "the only thing the money handlers consult" was true when this was written
(2026-07-31). ADR-0041 (dd84179, 2026-08-13) emptied `PinRequiredPaths`, so the session level now
gates only `GET /api/accounts/{id}/full-number`; a transfer carries its own authorisation in-band
(ADR-0042). The drift and the mock fix stand as written — what the same bug would hand a
session-less caller today is the reveal, not a transfer.)*

**Three tests were pinning the mock's mistakes as if they were the contract** — two in
`fidelity.test.ts`, one in `transferHandler.test.ts`. They are corrected, with the measured response
quoted in each.

**Fourteen more tests were exercising a state the product cannot reach**: they drove the step-up
flow with no session, which only worked because the mock did not check. They now seed one, which is
also more faithful — a user at a PIN prompt is by construction already signed in.

**Two constraints only the real target could teach**, both recorded in the harness:

- The BFF rate-limits auth to **10 requests per 60s per IP**. A `beforeEach` login is eleven logins
  across four files and trips it; login is once per file, and a 429 raises an explicit message
  rather than reading as contract drift.
- ASP.NET binds the model **before** the action runs, so a malformed body is a `400` even with no
  session. The first draft of the mock's session guard sat above the body check, reversing the two.

**One of them was found by accident, which is the point.** ASP.NET's query binding is case-insensitive;
`URLSearchParams.get` is not, so the mock read `PageSize` and missed `pageSize` entirely — falling
back to the default page size and answering 200 where the real stack validated the value and
answered 400. It surfaced because the first draft of a test used camelCase. Unreachable through the
app, since `apiSlice` sends PascalCase per the spec, but reachable by anyone hand-writing a URL, and
not a difference a mock has any business having. The mock now looks parameters up case-insensitively,
and the tests assert the app's own casing so they stay about envelopes rather than about the binder.

**The hole this ADR was written admitting is now CLOSED, and closing it cost what was predicted.**
`/api/accounts/*` and `/api/transactions/*` are gated on a live session, matching the real stack.
The earlier version of this section said the change "belongs in its own PR" because it moves the
default state every page-level test runs in — it did: 159 tests across 16 files, almost exactly the
"twenty-file blast radius" the mock's own docblock had estimated.

The fix was not sixteen patches but a change of DEFAULT. Signed-in is now the baseline test state,
because for routes behind `ProtectedRoute` it is the only reachable one; seeding per file would have
been sixteen chances to forget. The five tests whose subject genuinely IS the signed-out path now say
so explicitly, which is the more interesting claim of the two and used to be implicit in a default.

Measuring it produced an eleventh drift and one ordering fact. The proxy does not answer for itself —
it forwards whatever token the session yields, and the API rejects a request arriving without one, so
anonymous, an unresolvable cookie and a cookie revoked by logout all return the same
`401 AUTH_TOKEN_MISSING`. The mock had been answering the BFF's own 401 ("Session expired or invalid",
no `errorCode`) for the expired case; that shape is real but belongs to `/bff/auth/*`. And an
anonymous money-route call is that same 401, NOT the step-up 403 — authentication precedes the level
check.

**What is still NOT covered.** Seventeen assertions is a floor, not a ceiling:

- the remaining **44 candidates** from the static audit, never adversarially attacked and never
  checked live — leads, not facts;
- the **malformed-body envelope** (`errors` keyed `$` and `request`), measured but unasserted;
- **case-insensitive query binding**: the mock now matches, but the tests assert the app's own
  PascalCase, so the insensitivity itself is unpinned;
- a session that dies by **clock** rather than by revocation. It could not be produced without
  waiting out the inactivity window, so it is modelled identically to the three forms that were
  measured. Named here rather than hidden.

The gate exists so each of these can be added one measured assertion at a time.
