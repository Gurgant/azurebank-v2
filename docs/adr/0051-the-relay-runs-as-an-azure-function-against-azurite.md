# ADR-0051: The relay runs as an Azure Function, rehearsed locally against Azurite

**Status:** Accepted · **Date:** 2026-09-08 · Builds the runner
[ADR-0048](0048-the-api-is-the-runner-that-delivers-owed-notices.md) named under "What would change
this" and reserved a flag value for: *"the same claim protocol in a Function, developed against
Azurite, and `Notices:Runner=Function` telling this loop to step aside."* Adds one project, moves
the sweep and the shared configuration rules into Infrastructure, and changes one log level. No
schema change, no endpoint, no client, no new secret, and nothing is deployed.

## Context

ADR-0048 put a hosted loop in the API and cut the seam for a second runner in the same breath:
`NoticeRunner.Function` has existed as an enum value since `9cc6c4e`, and the API has been logging a
**Warning** when it was set — *"which nothing in this repository implements yet; this process
delivers nothing and notices stay owed until the verb runs"*. That warning was correct and is now
false.

The deferral record ratified on 2026-09-04 states the scope in one sentence: *"Later, as a feature
before the UI/UX train, the same claim protocol runs as an Azure Function developed and exercised
locally against Azurite (`func start`), so the deployment shape is rehearsed without a deployment; a
configuration flag names which runner is live, so the two never both send."* This ADR is that, and
its first job is to say what "rehearsed without a deployment" is allowed to claim.

**What was measured before any of it was written.** The toolchain question decides the whole shape,
because `backend/Directory.Build.props` imposes `net10.0` on every project and a Function that could
not target it could not reference `AzureBank.Infrastructure` — which would have forced a second copy
of the claim protocol across a framework boundary. Read from the packages themselves rather than
from documentation:

```
Microsoft.Azure.Functions.Worker 2.52.0      lib/: net6.0 net7.0 net8.0 net9.0 net10.0 netstandard2.0
Microsoft.Azure.Functions.Worker.Sdk 2.1.0   TargetFrameworkVersion v10.0 -> tooling suffix net10-isolated
                                             (and an Error when the map produces nothing)
```

Then run, on this machine, on a throwaway project outside the repository: Core Tools 4.13.0
(winget) + Azurite 3.37.0 (npm), a `net10.0` isolated project with one timer trigger — `dotnet
build` clean, `func start` reaching *Worker process started*, and the trigger firing twice ten
seconds apart. Two things that run taught that the packages could not: the Functions **host** takes
its own singleton lease in blob storage before running a timer (*"Host lock lease acquired by
instance ID …"*), and the SDK builds its WorkerExtensions shim as `net8.0` inside a `net10.0`
project's `obj/`, which reads as a defect in a build log and is not one.

That shim raised a CI question worth answering rather than hoping about, because `ci.yml` installs
`10.0.x` and nothing else: does a runner with only the .NET 10 targeting packs have what the net8.0
inner build needs? Measured by giving MSBuild a `NetCoreTargetingPackRoot` containing ONLY the 10.0
reference packs — 8.0.30 and 9.0.19 withheld — and building clean:

```
kept:    Microsoft.NETCore.App.Ref/10.0.11 (+ AspNetCore, WindowsDesktop)
dropped: Microsoft.NETCore.App.Ref/8.0.30, /9.0.19 (+ the same two)
NuGet cache for Microsoft.NETCore.App.Ref, before: empty   after: 8.0.30
-> Build succeeded. 0 Error(s)
```

The SDK downloads the reference pack it needs. CI requires no second `dotnet-version`, and this ADR
says so with the experiment rather than with a shrug.

And a defect found while reading, not while running: every rule in the API's `Notices` validation is
written `o.Runner != NoticeRunner.Api || …`, so on `main` a configuration of `Notices:Runner=Function`
with **no contact**, **no pickup directory**, one **inside a git repository**, or a lease shorter
than two periods starts every host without a word. Correct while the API was the only host that
could deliver; an omission the moment a second one could.

## Decision

**D1 — One sweep, in Infrastructure, and the API's own tests are the proof it did not move.** The
claim protocol (`NoticeClaim`), the per-row unit (`NoticeDeliveryRun`), the renderer and the
transport were already shared; the SWEEP — renew, read holdings, compute capacity, claim, re-read,
the per-row lease check, the six outcome arms — was `internal` in `AzureBank.Api`. It is
`NoticeSweep` in `AzureBank.Infrastructure.Notices` now, and both runners call it. ADR-0048 D4 made
this argument one level down ("so the two cannot drift on what 'delivered' costs"); a second runner
that reimplemented the sweep would be a second protocol wearing the first one's name.
`NoticeRelayService` keeps what belongs to its host — the flag, the `PeriodicTimer`, the scope per
sweep — and its `SweepAsync` stays as the thin delegate a test drives. **56 of the 57 existing
notice tests passed through the extracted sweep with no edit at all**; the one red was the level
change of D4 below, which is how the move is known to have been a move.

The logger is passed in and non-generic, so each runner keeps its own category — the API's lines
stay under `NoticeRelayService`, where ADR-0048's transcript quotes them — while the sentences stay
identical, which is the half that matters when two runners' logs are read together.

**D2 — A timer, not a queue, and ADR-0048 already decided it.** Its *Alternatives declined* answered
the queue: *"a queue would carry a copy of the obligation, and the row is the obligation … A lease on
the row keeps that property; a queue would have to be reconciled with it."* A queue trigger needs a
producer, and the only honest producer here is the row — so a queue would be that declined design
wearing a trigger. The backlog entry said "timer- or queue-triggered"; this is the ADR that closes
the *or*.

Which leaves Azurite doing what it actually does, and the ADR says so rather than implying a queue
of notices: it is the **host's own** storage account — the timer's schedule state and the host
singleton lease. That singleton is a second, independent one beside the row lease, at a different
layer: it elects one host among instances of the same function app and knows nothing about the API's
relay or the `notify` verb. It does not replace the row lease and nothing here claims it does.

The schedule is `%Notices:Schedule%`, resolved by the Functions host before any of this code runs,
because a trigger's schedule must be a constant the host can bind — it cannot be the `int`
`Notices:PeriodSeconds` the options carry later. That splits one concept across two keys, and D5
says what is done about it.

**A missing `Notices:Schedule` FAILS SAFE AND FAILS QUIET, and no code here can change that.** Measured by
removing the key: the host prints *"'%Notices:Schedule%' does not resolve to a value"*, then
*"Function 'Functions.DeliverOwedNotices' failed indexing and will be disabled"*, and then **"Job
host started"** — a running host that delivers nothing. Safe, because a runner that delivers nothing
is the recoverable failure this design already prefers (D6); quiet, because the process is up and
the exit code is zero. A worker-side validator was considered and NOT written: the binding is
resolved by the host before the worker's own options are ever consulted, so the guard could not run
earlier than the failure, and the host's message already names the exact key. What the omission
needs is a reader who knows to look, so the runbook and the project README say it instead.

**D3 — THREE configuration rules are shared, asked about the asking host; the fourth is not
universal.** The contact, the directory's existence and the git-tree guard are
`NoticeRelayOptionsValidation.ValidateAsRunner(NoticeRunner)` in Infrastructure; the API asks them
about `Api` and the Function about `Function`, and each refuses to start only when the flag names IT
and the section cannot support it.

The fourth — `LeaseSeconds >= 2 * PeriodSeconds` — is `ValidateThePeriodItSleepsFor`, and only the
API adds it. The first draft of this ADR gave the Function all four, and the pre-review was right to
call it: **this host never reads `Notices:PeriodSeconds`.** A rule guarding a number nothing uses is
not merely pointless — a host that validated a lease against a period and then ticked on
`Notices:Schedule` would be offering an assurance about a cadence it does not have. The Function's
equivalent is D5. A test asserts both sides of the split on identical numbers: refused for `Api`,
accepted for `Function`. A host the flag does not name starts regardless — it is going to
step aside anyway, and refusing to start over a directory it will never write to would take the API
down for the Function's misconfiguration, and the other way round.

Shared rather than mirrored, and the repository has already paid for the alternative:
`AddVerifierServices` mirrors the API's audit-key validation by hand and records, in its own
comment, that the mirror FAILED — `Audit:AnchorKey` was added to the API and not to the tool, *"so
for one release this tool started, read the chain, and would have refused to write an anchor at the
point of use"*. Three copies of a rule is three chances at that. The messages interpolate the
runner they were asked about, so an operator running two hosts learns which one refused. The
`[Range]` annotations stay out of the shared rules on purpose: a period or a lease out of range is a
misconfiguration even in a host that delivers nothing.

**D4 — `Runner=Function` is an Information line now, not a Warning.** The API's step-aside message
was a Warning because the value named a runner nothing implemented and an operator who set it
believed something was delivering. That premise is gone: `Runner=Function` is now a correct
configuration in which the API is simply not the one delivering — the same fact `None` states. A
Warning for a correct configuration is the kind of line that teaches an operator to ignore warnings.
The test asserts both halves: the level, and that the sentence which justified the old level does
not survive it.

**D5 — The lease rule is checked against the interval this host actually ticks at, measured by the
host itself.** ADR-0048 D6's `LeaseSeconds >= 2 * PeriodSeconds` is a start-time refusal in the API,
where the period is an option. The Function has no such number (D2, D3), so it compares the lease
against the gap between its own consecutive ticks, remembered in `NoticeRelayHostState`, and logs a
Warning per tick when the lease is under twice it. Better than the API's in one way, because the
interval is the one that really happened; worse in another, because a misconfigured host runs
instead of refusing to start. The first tick of a process says nothing — there is no previous tick,
and treating that as an interval of zero would warn on every cold start, which is how a real warning
becomes noise.

**The obvious source was tried first and is empty.** `TimerInfo.ScheduleStatus` reports `Last` and
`Next` and would give the interval for free. It arrives null here. Measured 2026-09-08 with a
20-second schedule and a 30-second lease, so a warning was due on every tick: three ticks with no
`timers` section produced none, and three more with `"timers": { "useMonitor": true }` in `host.json`
produced none either. Six consecutive ticks, a warning due on all six, and not one appeared — the
guard had never refused, which this repository calls a wish rather than a guard. `useMonitor` was
then REMOVED rather than kept and justified after the fact with a different reason. With the host's
own tick memory, the same configuration warns on the second and third ticks and reports **19s then
20s** — the interval that happened, not the one configured, which is the whole argument for
measuring it.

**D6 — The flag is checked in BOTH runners, symmetrically.** The API's loop delivers only when the
flag says `Api`; the Function delivers only when it says `Function`. Both step aside otherwise, so a
configuration naming neither delivers NOTHING. That is the recoverable failure: an owed notice waits;
a duplicate cannot be recalled. The alternative shape — each host live unless told otherwise — sends
every notice twice on the day somebody starts both.

**D7 — The runner-kind prefixes are constants, and there are three.** `api`, `verb`, `func`, on
`NoticeClaim` beside `NameWidth` and `RunnerNameFor`. The prefix is the head of what lands in
`LeasedBy` and the only part of the name a person reads during an incident; two kinds that shared one
would make every log line and every held-by-another count ambiguous, and nothing would fail. A test
pins the literal values as well as their distinctness, because a constant makes its own value
invisible and a rename would change what a running deployment writes while every test comparing two
constants stayed green.

**D8 — The runner name is one per host, not one per invocation.** The API gets this free: its relay
is a singleton and the name is a property on it. A Function class is constructed per invocation, so
the name lives on a singleton of its own (`NoticeRelayHostState`, which also remembers the previous tick for D5 — both are facts an invocation cannot keep). It has to be: a row whose delivery
failed stays owed and HELD under the name that claimed it, and the next sweep renews and retries it
by reading `LeasedBy` back. A name generated per invocation would strand every failure until its
lease lapsed and then claim it as a stranger — the duplicate the lease exists to prevent, reached
from the other side.

**D9 — This host carries no secret, and that is checkable rather than asserted.**
`AddInfrastructure` registers a DbContext and validates nothing; the DbContext has no
`SaveChangesInterceptor` (rejected deliberately, `AuditChain`'s remarks say why); ADR-0048 D7 already
decided a delivered notice writes no audit row. So the Function needs a connection string and the
`Notices` section, and **none of the six validated secrets** — unlike the `notify` verb, which holds
the chain key because it walks the chain. `local.settings.json` is this host's equivalent of the
API's user-secrets and is gitignored; `local.settings.sample.json` is committed in its place.

## Alternatives declined

**A queue trigger.** ADR-0048 declined the queue itself (D2 above); a trigger does not change the
argument. What a queue would buy — a runner woken by an event rather than a schedule — is the same
thing the row already offers, and the reconciliation it would need is the cost that ADR declined.

**Duplicating the sweep in the Function.** It would have avoided touching `AzureBank.Api` at all,
which is the only argument for it. Against it: two implementations of a protocol proved once on SQL
Server, and ADR-0048 D4's own reasoning applied one level down. If the framework had refused
`net10.0` this would have been forced, which is why the framework question was measured first rather
than assumed.

**Multi-targeting Infrastructure.** The fallback if a Function could not target `net10.0`. Not
needed, and it is recorded here so that a future reader who finds the Functions SDK dropping a
framework knows the shape that was considered and why it was not taken.

**Making the Function refuse to start when the flag names another runner.** Symmetrical-looking and
wrong: it would mean a stopped API's configuration could stop the Function, and the reverse. D3 says
what is done instead.

**A `RunOnStartup = true` trigger,** so a sweep happens the moment the host starts. Attractive for a
demonstration, and declined: it makes every restart a claim, which is exactly the behaviour that
turns a crash-loop into a backlog of leases held by hosts that are gone. The first tick is one
interval after start, matching the API's `PeriodicTimer` and its stated reason.

## Consequences

Measured after, on the running stack: API `:7215` and BFF `:5000` from this tree against
`AzureBankDev`, the API started with `Notices__Runner=Function`, Azurite on 10000/10001/10002, and
`func start` in the Function project with a 15-second schedule, a 120-second lease and a pickup
directory outside any git tree.

```
12:05:44   API  [INF] Notice relay: runner is Function; this process delivers nothing (Notices:Runner)
                      — Information, and the old sentence about nothing implementing it is gone
10:06:42Z  a PIN CHANGE through the BFF (200) -> SubscriberNotices: owed 15, leased 0
           the API delivered none of them: its loop had returned at start

           func start ->  Functions:  DeliverOwedNotices: timerTrigger
                          The next 5 occurrences of the schedule (Cron: '0,15,30,45 * * * * *')
10:07:31Z  Notice relay: sweep as func/GURGANT/41396/920b6432
           claimed 15, delivered 15, left 0 owed, into C:\Users\Drako\azurebank-pickup
10:07:45Z  the next tick, same name: claimed 0, delivered 0, left 0 owed
           SubscriberNotices: owed 0, leased 0 — the mark cleared the lease
           files in the pickup directory: 0 before, 15 after
           an email address in the Function's log: 0 occurrences; in the API's log: 0
           and in the artefact: To: <the account's address>, as ADR-0045 renders it
```

The address rule (ADR-0017, ADR-0048 D7) holds across the new host **without a line of new code**,
because it lives in `NoticeDeliveryRun`, which is the shared unit — which is D1 paying for itself
the first time it is used.

**D3's "refuses to start" was asserted for a week and measured on the day it was questioned.** The
decision says each host refuses to start when the flag names it and the section cannot support it.
That is a claim about a MOMENT, not only about a rule, and nothing had observed it. Measured by
removing `Notices:Contact` and running `func start`:

```
Failed to start language worker process for runtime: dotnet-isolated.
dotnet.exe exited with code -532462766 (0xE0434352). Unhandled exception.
  Microsoft.Extensions.Options.OptionsValidationException: Notices:Contact must be set when
  Notices:Runner is Function — it is mandatory content of every notice (NIST SP 800-63B-4 §4.6):
  an address or a number a recipient uses to say "this was not me".
func start itself: exit 1
```

So `ValidateOnStart` does work inside the isolated worker, the message reaches the operator once
rather than on every tick, and it names both the key and the runner — the interpolation D3 added.

**And nothing pinned that moment, which is how it was found.** While this PR was under review
`.ValidateOnStart()` was twice removed from `Program.Register` by accident, and the whole suite
stayed green both times: every other test in `NoticeFunctionStartupTests` resolves
`IOptions<T>.Value`, and resolving validates whether or not startup validation was asked for. The
suite pinned the RULES and not the MOMENT, and the difference is a host that refuses to start versus
one that starts and throws on every tick — a configuration error wearing the costume of a recurring
runtime fault. `TheRootRegistersSTARTUPValidation_SoABadSectionStopsTheHostRatherThanTheFirstTick`
now asserts the `IStartupValidator` registration, and is the only one of the ten that goes red when
the call is dropped.

**One line of that transcript was read wrongly, and the pre-review caught it.** The first run
produced no lease warning and it was recorded as the rule passing. It was the rule never running:
the lease was 120s against a 15s tick, so nothing was due, and the guard would have said nothing
either way because `ScheduleStatus` was null. D5 now carries the experiment that separates those two
silences. A silence is not a pass, and the only way to tell was to make the warning DUE and watch.

Falsified, one mutant at a time, each restored byte-exact: the flag check removed turns the
step-aside rows red; the lease warning removed turns its own row red; the runner name generated per
read turns three red; the kind prefix set to the API's turns two red; and the Function's root
validating as `Api` — which is precisely `main`'s behaviour — turns **all three refusal tests red**
plus the asymmetry row, which is how the gap in the Context is known to have been real rather than
read. (That count was four before D3 split the lease rule out, and it was re-measured rather than
carried forward: a number in a record is a measurement, and a measurement outlives the sentence
around it only if somebody re-takes it.) One mutant is worth naming for what it taught: `if (false)` around the flag check would not
COMPILE, because `TreatWarningsAsErrors` is on in Release and CS0162 is unreachable code — a mutant
that will not build proves the build is strict, not that the guard works, so it was rewritten as the
guard removed.

**What this does NOT claim.** Nothing is deployed. `func start` on a developer's machine against a
storage emulator rehearses the SHAPE — a second deployable, its own configuration surface, a trigger,
a host that elects a singleton — and none of the things a deployment is for: no availability, no
scaling, no cold-start behaviour under real load, no managed identity, no Application Insights, no
consumption plan. The last hop is unchanged and still a file on this machine that nobody has seen;
`docs/deferred/relaying-the-enrolment-notice.md`'s preconditions for "delivered" — a provider
credential, addresses the project may write to, a second contact, a remedy behind it — all stand.
CI builds this project with the solution and runs its unit tests; CI does NOT run `func start`, and
the transcript above is the only evidence that the host starts.

The unit tests construct the Function class directly, which is what the isolated worker does, and
say so: they cannot prove the trigger binds or that `%Notices:Schedule%` resolves. That is the
transcript's job, and the division is stated rather than blurred.

## What would change this

- **A sending transport.** Unchanged from ADR-0048: the seventh secret, and D3's at-least-once
  duplicate becomes a mail seen twice. It would now be registered in THREE composition roots, which
  is an argument for a shared registration extension the day it happens, not before.
- **A real deployment.** Everything under "What this does NOT claim" becomes a question with an
  answer, and `local.settings.json` becomes application settings with a managed identity behind the
  connection string.
- **A third runner, or a second instance of this one.** The flag admits one KIND at a time; two
  instances of the Function app are held apart by the host's own singleton lease, and two of anything
  else by the row lease. A third kind needs a fourth constant in D7 and nothing else.
- **`Notices:PeriodSeconds` and `Notices:Schedule` drifting apart.** They express one idea in two
  keys because two hosts read it at two different moments (D2). If a third host ever needs the
  period, the honest fix is one key the API can parse and a trigger can bind — a `TimeSpan` string —
  not a second conversion.
