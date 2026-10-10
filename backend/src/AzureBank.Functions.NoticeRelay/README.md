# AzureBank.Functions.NoticeRelay

A timer-triggered Azure Function (v4, isolated worker, .NET 10) that delivers owed subscriber
notices. It is run locally against Azurite and is not deployed:
[ADR-0051](../../../docs/adr/0051-the-relay-runs-as-an-azure-function-against-azurite.md).

When a transfer PIN is enrolled or changed, the API records in the same save that the account
holder is **owed a notice** (ADR-0045, ADR-0047). Three runners can render that row into a
message: the operator tool's `notify` verb, a hosted loop inside the API (ADR-0048) and this
Function. `Notices:Runner` chooses between the two **hosted** runners, or names neither. It does
**not** gate the verb: `notify` delivers whenever it is run, and the lease keeps it off the rows
another runner holds (ADR-0048 D5).

This project is a host, not a second implementation: the claim protocol, the sweep, the
delivery of one row, the renderer, the transport and the configuration rules live in
`AzureBank.Infrastructure` and are the same code the API runs. `DeliverOwedNotices.cs` is the
timer trigger, one `NoticeSweep` per tick; `Program.cs` is the composition root;
`NoticeRelayHostState.cs` keeps the runner name and the previous tick, which a class constructed
per invocation cannot hold. The other projects: [AzureBank Backend](../../README.md).

## Running it

It needs the Azure Functions Core Tools and Azurite. Azurite holds the Functions host's own
storage and nothing else: no queue carries a notice, because the row is the obligation
(ADR-0051 D2).

Once, to install:

```powershell
winget install --id Microsoft.Azure.FunctionsCoreTools
npm install -g azurite
```

Then, from this folder:

```powershell
azurite --silent --location $env:TEMP\azurite-azurebank

copy local.settings.sample.json local.settings.json
# edit ConnectionStrings:DefaultConnection and Notices:PickupDirectory

func start
```

`local.settings.json` is git-ignored: it holds this host's connection string and pickup
directory, as user-secrets do for the API.

## Configuration

| Key | What it does |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | The store the notices live in |
| `Notices:Runner` | `Function` for this host to deliver; `Api` or `None` and it steps aside. Gates the hosted runners only, never the `notify` verb |
| `Notices:Schedule` | The trigger's CRON or TimeSpan expression, bound as `%Notices:Schedule%`. Required whatever the runner |
| `Notices:PickupDirectory` | An existing directory outside any git tree; one `.eml` per notice |
| `Notices:Contact` | How a recipient repudiates the event: mandatory content of every notice |
| `Notices:LeaseSeconds` | How long a claimed row stays this runner's: 120 unless set, from 30 to 3600 |
| `Notices:BatchSize` | How many free rows one sweep claims: 100 unless set, from 1 to 10000 |

- **The pickup directory must exist and be outside any git repository, and `Notices:Contact`
  must be set,** when the flag says `Function`, or the host refuses to start with a message that
  names the key. With `Api` or `None` this host starts without them and steps aside. A lease or
  a batch size outside its range stops the host whatever the flag says (ADR-0051 D3).
- **`Notices:Schedule` must be set even when the flag names another runner**: the Functions host
  resolves the trigger's `%Notices:Schedule%` during indexing, before the flag is read. Without
  it the worker refuses to start and `func start` exits 1:

  ```
  OptionsValidationException: Notices:Schedule must be set: it is the timer trigger's cadence, …
  WHATEVER Notices:Runner says. …
  Failed to start language worker process for runtime: dotnet-isolated.
  ```

  The host's own "Job host started" line still appears, because the host comes up and keeps
  retrying a worker it cannot get: read the exit code, not that line.
- **`Notices:PeriodSeconds` is not read here**: it is the API's cadence. This host compares the
  lease with the interval it observes between its own ticks, and logs a Warning on each tick
  while the lease is under twice that gap. It warns and keeps running (ADR-0051 D5).
- **The trigger pins `UseMonitor = false`**, so that a restart never sweeps a past-due tick at
  once. Without it a schedule of a minute or more can run the sweep at startup (ADR-0051 D2).
- **No signing key lives here.** A delivered notice writes no audit row, so this host needs the
  connection string and the `Notices` section and none of the API's validated secrets
  (ADR-0051 D9). The connection string still reaches the whole store.

## What one tick looks like

```
Notice relay: delivered notice 01a08089ed98788bbb3c029cddc29adf (PinChanged) as ….eml
Notice relay: sweep as func/GURGANT/28584/3658b5a3 claimed 2, delivered 2, left 0 owed, into C:\azurebank-pickup
```

`func/…` is the kind prefix that lands in `SubscriberNotices.LeasedBy`: `api`, `verb` or `func`
says which runner holds a row (ADR-0051 D7). The recipient's address is in the `.eml` and in
**no log line**: the rule lives in `NoticeDeliveryRun`, which all three runners share.

## What the rehearsal is worth

It exercises the shape of a deployment (a second deployable with its own lifetime and
configuration, a trigger, a host that elects a singleton in blob storage) on one machine,
started by hand, against a storage emulator. It says nothing about availability, scaling, cold
start under load, managed identity or a consumption plan (ADR-0051, Consequences). The last hop
is a file on this machine, seen by nobody: what "delivered" would need is in
[`relaying-the-enrolment-notice.md`](../../../docs/deferred/relaying-the-enrolment-notice.md).

## Tests

CI builds this project with the solution and does not run `func start`.
`DeliverOwedNoticesTests` constructs the Function class directly and covers the flag check, the
runner name, the tick measurement, the lease warning and `UseMonitor = false`.
`NoticeFunctionStartupTests` builds the real composition root and covers the configuration
rules. Neither can cover the trigger binding or `%Notices:Schedule%` resolving. Both are in
`backend/tests/AzureBank.Tests`.
