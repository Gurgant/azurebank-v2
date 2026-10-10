# ADR-0051: The relay runs as an Azure Function, rehearsed locally against Azurite

**Status:** Accepted · **Date:** 2026-09-08 · **Amended:** 2026-09-09 (D2, D3)

## Context

ADR-0048 puts the notice relay in the API and reserves `Notices:Runner=Function` for a second
runner: the same claim protocol in an Azure Function, exercised locally with `func start` against
Azurite, so that the deployment shape is rehearsed without a deployment. The flag names which
runner is live, so the two never both send. `backend/Directory.Build.props` imposes `net10.0` on
every project, and a Function that could not target it could not reference
`AzureBank.Infrastructure`: it would need a second copy of the protocol. Before this decision the
API's four runner rules were each written `o.Runner != NoticeRunner.Api || …`, so a section naming
`Function` with no contact, no pickup directory or one inside a git repository started in silence.

## Decision

- **D1 — One sweep, in Infrastructure, and both runners call it.** `NoticeSweep` holds the renew,
  the read of holdings, the capacity, the claim, the re-read, the per-row lease check and the six
  outcome arms, because a second runner that reimplemented them would be a second protocol under
  the first one's name, the reason ADR-0048 D4 gives one level down for the per-row unit.
  `NoticeRelayService` keeps the flag, the `PeriodicTimer` and the scope per sweep; each runner
  passes its own logger, so the categories differ and the sentences stay identical.
- **D2 — A timer trigger, not a queue**, because a queue trigger needs a producer and the only
  honest one is the row: it would be the queue ADR-0048 declines, where the row is the obligation.
  Azurite holds the host's own storage, the blob singleton lease that elects one host among
  instances of one function app; that lease knows nothing of the API's relay or the `notify` verb
  and does not replace the row lease. The schedule is `%Notices:Schedule%`, because a trigger's
  schedule must be a constant the Functions host can bind before any project code runs. The
  trigger pins `UseMonitor = false`, so a restart never sweeps a past-due tick at once and no
  schedule state is persisted. A missing `Notices:Schedule` stops the worker with a message naming
  the key (`ValidateTheScheduleItIsBoundTo`, presence only), because the host would otherwise
  disable the function and still report "Job host started": exit code zero, delivering nothing.
- **D3 — Three configuration rules are shared and asked about the asking host; the fourth is not
  universal.** The contact, the directory's existence and the git-tree guard are
  `NoticeRelayOptionsValidation.ValidateAsRunner(NoticeRunner)`: the API asks about `Api`, the
  Function about `Function`, and each refuses to start only when the flag names it and the section
  cannot support it. Shared and not mirrored, because a hand-kept mirror already failed once
  (`AddVerifierServices` missed `Audit:AnchorKey` for a release). A host the flag does not name
  starts regardless of these three, because refusing over a directory it will never write to
  would take one runner down for the other's misconfiguration. `LeaseSeconds >= 2 * PeriodSeconds`
  is `ValidateThePeriodItSleepsFor` and only the API adds it, because the Function never reads
  `Notices:PeriodSeconds`. Two rules apply whatever the flag says: the `[Range]` annotations,
  because a value out of range is a misconfiguration even in a host that delivers nothing, and the
  schedule's presence, because the host resolves the binding during indexing, before the flag is
  read. Each message names the runner it was asked about, and the root registers
  `ValidateOnStart`, so a bad section stops the host once instead of failing every tick.
- **D4 — `Runner=Function` is an Information line in the API, not a Warning**, because it is a
  correct configuration in which the API is simply not the one delivering, the same fact `None`
  states; a Warning for a correct configuration teaches an operator to ignore warnings.
- **D5 — The lease rule is checked against the interval the Function actually ticks at.** The
  host remembers its previous tick in `NoticeRelayHostState` and logs a Warning on each tick when
  the lease is under twice that gap, because its cadence is a trigger expression and not a number
  the options carry, and `TimerInfo.ScheduleStatus` is never populated with the monitor off (D2).
  The first tick of a process says nothing: there is no previous tick. The interval in the
  message is rounded, not truncated, because it is the number an operator acts on.
- **D6 — The flag is checked in both runners, symmetrically.** The API's loop delivers only when
  the flag says `Api`, the Function only when it says `Function`, so a configuration naming
  neither delivers nothing, because an owed notice waits and a duplicate cannot be recalled.
- **D7 — The runner-kind prefixes are three constants on `NoticeClaim`: `api`, `verb`, `func`.**
  The prefix is the head of what lands in `LeasedBy` and the only part of the name a person reads
  during an incident, so two kinds that shared one would make every log line and every
  held-by-another count ambiguous while nothing failed. A test pins the literal values, because a
  rename would change what a running deployment writes.
- **D8 — The runner name is one per host, not one per invocation.** A Function class is
  constructed per invocation, so the name lives on the singleton `NoticeRelayHostState`, because
  a row whose delivery failed stays held under the name that claimed it and the next sweep finds
  it by reading `LeasedBy` back: a name per invocation would strand every failure until its lease
  lapsed and then claim it as a stranger.
- **D9 — This host carries no notice secret.** It needs a connection string and the `Notices`
  section and none of the API's validated secrets, because a delivered notice writes no audit row
  (ADR-0048 D7): the `notify` verb holds the chain key because it walks the chain, and this host
  walks nothing. `ConnectionStrings:DefaultConnection` still reaches the whole store: it lives in
  the gitignored `local.settings.json`, and `local.settings.sample.json` is committed in its place.

## Alternatives declined

- Rejected: a queue trigger, because a queue is a copy of the obligation to reconcile (ADR-0048).
- Rejected: a second copy of the sweep in the Function, because it would be two implementations of
  a protocol proved once on SQL Server; only a framework that refused `net10.0` would force it.
- Rejected: multi-targeting Infrastructure, because the Functions SDK targets `net10.0` already.
- Rejected: a start refused when the flag names another runner, because a stopped API's
  configuration could then stop the Function, and the reverse (D3).
- Rejected: a `RunOnStartup = true` trigger, because every restart would be a claim, which turns a
  crash-loop into a backlog of leases held by hosts that are gone. `UseMonitor = false` (D2) shuts
  the second door to the same behaviour, the monitor's past-due check at start.

## Consequences

- The address rule (ADR-0017, ADR-0048 D7) holds in the new host with no new code, because it
  lives in `NoticeDeliveryRun`, the unit all runners share.
- One cadence lives in two keys: `Notices:PeriodSeconds` (the API) and `Notices:Schedule`.
- The Function's lease check is weaker at start: a host with too short a lease runs and warns.
- CI needs no second `dotnet-version`: the SDK's `net8.0` shim downloads its own reference pack.
- Not covered: nothing is deployed. `func start` against a storage emulator rehearses the shape (a
  second deployable, its own configuration surface, a trigger, a host that elects a singleton) and
  none of what a deployment is for: no availability, no scaling, no cold-start behaviour under
  real load, no managed identity, no Application Insights, no consumption plan.
- Not covered: the last hop is still a file in a pickup directory; the preconditions for
  "delivered" in `docs/deferred/relaying-the-enrolment-notice.md` all stand.
- Not covered: CI builds the project and runs its unit tests and does not run `func start`; the
  unit tests construct the Function class directly and cannot prove that the trigger binds.

## Revisit when

- A sending transport: registered in three composition roots, it argues for one shared registration.
- A real deployment: `local.settings.json` becomes application settings behind a managed identity.
- A third kind of runner: a fourth constant in D7. A third host that needs the period: one key the
  API can parse and a trigger can bind, a `TimeSpan` string.

## Verified by

- `NoticeFunctionStartupTests`: the Function's real composition root, rule by rule (D2, D3).
- `DeliverOwedNoticesTests`: the flag, the stable name, the lease warning, `UseMonitor = false`.
- `NoticeRelayServiceTests`: the API's Information line (D4) and the three prefixes (D7).

## Related

ADR-0017, ADR-0045, ADR-0048.
