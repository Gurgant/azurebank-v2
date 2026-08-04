# ADR-0032: Running the real-backend layers in CI, on a stack the job owns

**Status**: Accepted. Phase 4 — the last of the plan opened in ADR-0029.

**Date**: 2026-08-04

**Decision Makers**: Vladislav Aleshaev

---

## Context

Three suites now need a live backend: the contract gate's `real` target (ADR-0029), the integration
layer (ADR-0030) and the Playwright E2E suite (ADR-0031). Until this ADR all three ran only on a
developer's machine, which meant the honest status of every one of them was "green when someone
remembered to run it".

Each of those ADRs also closed with the same two caveats, written as limitations rather than
solved: **money accumulates in the shared dev database**, and **the BFF's auth budget is per IP**.
Both are consequences of sharing ONE backend between runs and people.

CI already knew most of the recipe. `contract-tests.yml` stands up SQL Server as a service
container, seeds it, and starts the API with throwaway secrets; `ci.yml`'s `backend-sql` job proves
the same service-container pattern gates on `main`. The missing piece was never the database — it
was that **nothing has ever started the BFF in CI**, and all three of these suites talk to the BFF,
not to the API.

## Decision

**1. One job, one stack, owned end to end.** `real-stack` in `ci.yml` creates its own SQL Server,
its own database (`AzureBankE2E`), and its own API and BFF processes, then runs all three suites
against them in sequence. Nothing is shared with a developer's machine or with another job.

**2. That dissolves both standing caveats rather than restating them.** The database is created and
seeded per run, so the accumulating €1.00 deposits are gone by construction — this job IS the
clean-room the previous three ADRs said belonged to Phase 4. And the auth budget is per IP against
*a* BFF; since every job runs its own BFF on its own runner, concurrent jobs cannot contend. The
earlier ADRs framed that risk as "concurrent CI jobs stealing each other's quota", which was
imprecise: jobs sharing one deployed backend would contend, jobs each owning one do not.

**3. The real contention is INSIDE the job, and it is handled by config, not by hope.** The three
suites together spend about ten auth requests, which is exactly the production limit of 10 per 60s
per IP — back-to-back they would rate-limit each other. The job raises
`RateLimiting:AuthPermitLimit` for its own BFF. This is environmental, **not a weakened assertion**:
none of the three suites asserts the limiter, and the limiter itself is proven by
`AzureBank.Bff.Tests`' own rate-limiter test, which still runs unchanged.

**4. The BFF's upstream is redirected with COMMAND-LINE config, not environment variables.** The BFF
reaches the API two ways and both default to the dev HTTPS address `https://localhost:7215`: the
YARP cluster for proxied `/api/*`, and the named `BackendApi` client the auth controller and
`TokenRefresher` use directly — the latter dereferenced with `!` at `Program.cs:148`, so an unset
value is a startup crash rather than a fallback. The env-var form of the cluster key would need a
HYPHEN in the variable name (`ReverseProxy__Clusters__backend-api__…`), which is not a portable
shell identifier. The command-line provider takes colon-separated keys and has no such restriction.

**5. Readiness is polled, never slept.** Both processes expose `/health/ready`, and the BFF's
readiness check pings the API's `/health/live` through the same named client the app uses — so a
green BFF readiness means the hop under test actually works. A fixed sleep is either too short
(flaky red) or too long (wasted minutes), and it never says which process failed.

**6. It gates.** `real-stack` runs on the same push and pull-request triggers as everything else in
`ci.yml`. A suite that only runs on request is a suite that rots.

## Consequences

**The mechanism was measured before it was committed.** The command-line override is the one thing
in this job that could not be inferred from the existing workflows, so it was proven locally first:
a second BFF started on port 5099 with exactly the arguments the workflow uses, pointed at the
already-running API. Its `/health/ready` returned 200 with the log showing the readiness probe
calling `https://localhost:7215/health/live` (so `BackendApi:BaseUrl` was picked up), and an
anonymous `/api/accounts` returned the API's own
`401 AUTH_TOKEN_MISSING` (so the hyphenated cluster destination was picked up too).

**The API runs over HTTP in CI, on 5068.** There is no ASP.NET dev certificate on a runner, and
`contract-tests.yml` already established that port. Nothing in the three suites cares: every one of
them talks to the BFF on 5000, and the only `7215` references left in `frontend/src/contract`,
`frontend/src/integration` and `frontend/e2e` are inside comments and failure messages telling a
developer which local launch configs to start.

**Failure is made debuggable on purpose.** The Playwright report and traces upload on failure, and
the last 200 lines of both backend logs are printed — because the most likely first failure of this
job is a process that did not start, and the readiness step alone would only say which one.

**What this does NOT do.**

- **It does not run the suites twice.** The unit suite has a ×2 convention locally to catch order
  dependence; these three are serial by construction and slow enough that a second pass would cost
  more than it finds.
- **It does not add browsers.** Chromium only, matching ADR-0031.
- **It does not seed per suite.** All three share one seeded database within the job, in declaration
  order. That is deliberate — a shared session and a shared account is exactly the topology the
  suites were designed for — but it means a future suite that mutates the seed destructively would
  need its own job rather than another step.
- **`contract-tests.yml` is untouched.** Schemathesis and Bruno stay manual and non-gating; folding
  them in is a separate decision about what should block a merge.
