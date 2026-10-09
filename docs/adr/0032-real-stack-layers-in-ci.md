# ADR-0032: Running the real-backend layers in CI, on a stack the job owns

**Status:** Accepted (Phase 4, the last, of ADR-0029's plan) · **Date:** 2026-08-04 · **Amended:**
2026-09-11 (ADR-0054), 2026-09-15 (ADR-0053), 2026-09-24 (Bruno on each pull request) ·
**Decision Makers:** Vladislav Aleshaev

## Context

Three suites need a live backend: the contract gate's `real` target (ADR-0029), the integration
layer (ADR-0030) and the Playwright e2e suite (ADR-0031). Run only on a developer's machine, each is
green only when someone remembers to run it, and each carries the two caveats of sharing one backend
between runs and people: money accumulates in the shared dev database, and the BFF's auth budget is
per IP. CI already stands up SQL Server as a service container (`backend-sql`,
`contract-tests.yml`). What it never starts is the BFF, and all three suites talk to the BFF, not to
the API.

## Decision

1. **One job, one stack, owned end to end.** `real-stack` in `ci.yml` creates its own SQL Server,
   its own database (`AzureBankE2E`) and its own API and BFF processes, then runs the three suites
   against them in sequence, because nothing may be shared with a developer's machine or another
   job.
2. **That dissolves both caveats.** The database is created and seeded per run, so deposits do not
   accumulate. The auth budget is per IP against one BFF, and every job runs its own BFF on its own
   runner, so concurrent jobs cannot contend: only jobs sharing one deployed backend would.
3. **The contention inside the job is handled by configuration.** Back to back, the three suites
   spend the production auth budget of 10 per 60 s per IP and would rate-limit each other, so the
   job raises `RateLimiting:AuthPermitLimit` (and `RateLimiting:GlobalPermitLimit`) for its own BFF.
   This is environmental, not a weakened assertion: none of the three suites asserts the limiter,
   and `AzureBank.Bff.Tests` proves it unchanged.
4. **The BFF's upstream is redirected with command-line configuration, not environment variables.**
   The BFF reaches the API two ways, the YARP cluster for proxied `/api/*` and the named
   `BackendApi` client, and both default to `https://localhost:7215`. The environment form of the
   cluster key needs a hyphen in the variable name (`ReverseProxy__Clusters__backend-api__…`), which
   is not a portable shell identifier; the command-line provider takes colon-separated keys.
5. **Readiness is polled, never slept.** Both processes expose `/health/ready`, and the BFF's check
   pings the API's `/health/live` through the same named client the app uses, so a ready BFF means
   the hop under test works. A fixed sleep is too short or too long, and never says which process
   failed.
6. **It gates.** `real-stack` runs on the same push and pull-request triggers as the rest of
   `ci.yml`, because a suite that only runs on request rots.

## Consequences

- The overrides of decision 4 are proven by the job itself: the API listens only on
  `http://localhost:5068` and nothing is bound to 7215, so a failed override sends every call to a
  closed port and turns all three suites red. A local probe proves nothing, because there the
  override equals the `appsettings.json` default.
- The API runs over HTTP in CI, on 5068, because a runner has no ASP.NET dev certificate. The suites
  talk to the BFF on 5000.
- The e2e suite runs against the built SPA, served by this job's BFF under its CSP (ADR-0054, D7).
- The checkout token is not persisted, in any job. `actions/checkout` writes `GITHUB_TOKEN` into
  `.git/config` by default, and every job then runs third-party code that could read it (NuGet
  restore, `dotnet run`, `npm ci` with its postinstall scripts). No job performs an authenticated
  git operation, and `zizmor` flags every job that lacks the setting.
- Failure is debuggable: the Playwright report and traces upload on failure, and the last 200 lines
  of both backend logs are printed, because the likeliest first failure is a process that did not
  start.
- Schemathesis is not a step of this job. It is the `conformance` job in `ci.yml`, on its own SQL
  Server because it mints users and accounts the three suites reason about, and it blocks a merge
  (ADR-0053, D6). Bruno runs in `contract-tests.yml` on every pull request, on every push to `main`,
  and by hand.
- Not covered: order dependence. The suites run once; they are serial by construction and slow
  enough that a second pass costs more than it finds.
- Not covered: browsers other than Chromium, as in ADR-0031.
- Not covered: seeding per suite. All three share one seeded database within the job, in declaration
  order, which is the topology they were designed for. A suite that mutates the seed destructively
  needs its own job, not another step.

## Verified by

- The `real-stack` job of `.github/workflows/ci.yml`, on every push and pull request.
- `RateLimiterTests` in `AzureBank.Bff.Tests`: the limiter the job relaxes for itself.

## Related

ADR-0029, ADR-0030, ADR-0031, ADR-0053, ADR-0054.
