# ADR-0029: One suite, two backends — making mock drift fail the build

**Status**: Accepted — the gate is deliberately INCOMPLETE; see “What is NOT covered”

**Date**: 2026-07-31

**Decision Makers**: Vladislav Aleshaev

---

## Context

The frontend suite had exactly one oracle: MSW. Every assertion about a status code, an `errorCode`,
a body shape or a header was checked against `handlers.ts`, and `handlers.ts` was written by reading
the C# and believing the reading. When those two agree with each other and neither agrees with the
server, the suite is green and wrong — the worst possible state, because it is indistinguishable
from correct.

That is not hypothetical here. Three drifts were found by hand, each only by running the real stack:

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

**7. Positive controls are included deliberately.** A gate made only of known failures cannot
distinguish "the mock was fixed" from "the assertion was written to match whatever the mock did".

## Consequences

**The gate found six real drifts on its first run, and the mock was aligned to the backend in every
case.** The backend was right every time; nothing here is a backend fix.

| Drift | Mock said | Backend says |
|---|---|---|
| PIN routes with no session | `200`, and set `authLevel = 2` | `401`, no `errorCode` |
| Same-account internal transfer | `422 SAME_ACCOUNT_TRANSFER` | `400` + `errors.toAccountId`, no code |
| Transactions validation envelope | "Validation Failed" + `detail` | "One or more validation errors occurred.", **no detail** |
| Pagination error keys | `pageSize` | `PageSize` |
| Malformed PIN, no session | `401` (session read first) | `400` — model validation runs BEFORE the action |
| Malformed PIN error key/envelope | `pin`, validator envelope | `Pin`, framework envelope |

**The API has TWO validation envelopes, and that is the finding with the longest reach.** Endpoints
with a FluentValidation validator get `ValidationExceptionHandler`'s hand-written body (title
"Validation Failed", a `detail`, a bare 32-hex `traceId`); endpoints without one are rejected by
`[ApiController]` model state and rendered by the framework (title "One or more validation errors
occurred.", **no `detail` member**, a W3C traceparent). `TransactionController` injects validators
only for deposit and withdraw, so its list and summary actions take the second path. The mock used
the first for everything — which is why `HistoryPage`, which renders `problem.detail` for exactly
that endpoint, showed a sentence under MSW and its generic fallback in production.

**The PIN drift was the dangerous one, and it is worth being precise about why.** The mock answered
`200` to a caller with no session at all AND elevated itself to level 2 — and the elevation level is
the only thing the money handlers consult, so a nonexistent session could be walked up to elevated
and used to push a full transfer. **The real BFF is correct**: it reads the session first and never
looks at the PIN. This was a mock-permissiveness problem, never a production hole.

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
  session. The first draft of the mock's session guard sat above the body check and had the two the
  wrong way round.

**A seventh drift, found by accident and then fixed.** ASP.NET's query binding is case-insensitive;
`URLSearchParams.get` is not, so the mock read `PageSize` and missed `pageSize` entirely — falling
back to the default page size and answering 200 where the real stack validated the value and
answered 400. It surfaced because the first draft of a test used camelCase. Unreachable through the
app, since `apiSlice` sends PascalCase per the spec, but reachable by anyone hand-writing a URL, and
not a difference a mock has any business having. The mock now looks parameters up case-insensitively,
and the tests assert the app's own casing so they stay about envelopes rather than about the binder.

**What is NOT covered — and this gate should not be called complete.** Twelve assertions is a floor,
not a ceiling, and one hole is big enough that the ADR's status says so out loud.

The largest hole is **authentication on the protected resource routes**. `/api/accounts/*` and
`/api/transactions/*` are gated in the mock by nothing at all: no session read, no expiry check. The
money handlers consult `authLevel` but never ask whether the session behind it is still alive, so an
expired session still spends. The real stack rejects all of that. It is left open here deliberately
rather than quietly — closing it changes the default state every page-level test runs in, which is a
change of a different size and belongs in its own PR, with the same measure-then-assert discipline.
Until then, no test in this repo proves those routes are protected.

Also untested: the remaining 44 candidates from the static audit, the malformed-body envelope
(`errors` keyed `$` and `request`), and the case-insensitive query binding recorded above. The gate
exists so each can be added one measured assertion at a time.
