# AzureBank.Functions.NoticeRelay

**The second notice runner** — a timer-triggered Azure Function that delivers owed subscriber
notices, rehearsed locally against Azurite and never deployed

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![Azure Functions](https://img.shields.io/badge/Azure_Functions-v4_isolated-0062AD?style=flat-square)](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide)
[![ADR](https://img.shields.io/badge/ADR--0051-Accepted-2ea44f?style=flat-square)](../../../docs/adr/0051-the-relay-runs-as-an-azure-function-against-azurite.md)

---

## Overview

When a transfer PIN is enrolled or changed, the API records — in the same save as the action — that
the account holder is **owed a notice** (ADR-0045, ADR-0047). Something then has to render that row
into a message. Three things can: the operator tool's `notify` verb, a hosted loop inside the API
(ADR-0048), and this Function (ADR-0051).

`Notices:Runner` chooses between the two **hosted** runners — the API's loop and this Function — or
names neither. It does **not** gate the verb: `notify` is run by a person and delivers whenever they
run it, including when `Notices:Runner` is `None`. What keeps the verb from colliding with a live
host is not the flag but the lease: ADR-0048 D5 has it claim rows under its own name and leave alone
what another runner holds.

**Parent solution**: [AzureBank Backend](../../README.md)

**What this project is NOT.** It is not a second implementation of anything. The claim protocol, the
sweep, the per-row delivery unit, the renderer, the transport and the configuration rules all live
in `AzureBank.Infrastructure` and are the same code the API runs. What is here is a *host*: a
trigger, a composition root, a flag check, and the two facts a per-invocation class cannot hold.

```
AzureBank.Functions.NoticeRelay/
├── Program.cs                     the composition root — DbContext, transport, validated options
├── DeliverOwedNotices.cs          the timer trigger: one NoticeSweep per tick
├── NoticeRelayHostState.cs        this host's runner name and its previous tick
├── host.json                      Functions host configuration
└── local.settings.sample.json     the shape of local.settings.json, which is gitignored
```

---

## Running it

It needs the Azure Functions Core Tools and Azurite. The Functions **host** wants a storage account
for its own bookkeeping — that is the whole of what Azurite is doing here. No queue carries a notice:
the row is the obligation, and ADR-0048 declined a queue for that reason.

Once, to install:

```powershell
winget install --id Microsoft.Azure.FunctionsCoreTools
npm install -g azurite
```

Then:

```powershell
azurite --silent --location $env:TEMP\azurite-azurebank

copy local.settings.sample.json local.settings.json
# edit ConnectionStrings:DefaultConnection and Notices:PickupDirectory

func start
```

`local.settings.json` is **gitignored**: it is this host's connection string and pickup directory —
the Function's equivalent of the API's user-secrets.

**When `Notices:Runner=Function`**, the pickup directory must **exist** and must be **outside any
git repository**, or the host refuses to start with a message naming the key — the same rule the verb
and the API apply, and since ADR-0051 the same code, asked about whichever host the flag names. With
`Api` or `None` this host starts without it and steps aside (ADR-0051 D3).

---

## Configuration

| Key | What it does |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | The store the notices live in |
| `Notices:Runner` | `Function` for this host to deliver; `Api` or `None` and it steps aside. Gates the hosted runners only — never the `notify` verb |
| `Notices:Schedule` | The trigger's CRON or TimeSpan expression, bound as `%Notices:Schedule%`. Required whatever the runner |
| `Notices:PickupDirectory` | An existing directory outside any git tree; one `.eml` per notice |
| `Notices:Contact` | How a recipient repudiates the event — mandatory content of every notice |
| `Notices:LeaseSeconds` | How long a claimed row stays this runner's |
| `Notices:BatchSize` | How many free rows one sweep claims |

`Notices:PeriodSeconds` is **not** read here. It is the API's cadence; this host's is
`Notices:Schedule`, and the lease is checked against the interval actually observed between ticks
rather than against a number nothing uses (ADR-0051 D5).

`Notices:Schedule` is required WHATEVER `Notices:Runner` says — the only key whose PRESENCE is
demanded of a host the flag does not name (the `[Range]` checks are unconditional too, but they
judge a value rather than require one), because the binding is resolved during indexing, before the
flag is read.
The trigger's `%setting%` is resolved by the Functions runtime during indexing, so a missing value
USED to leave the host up with the function disabled — exit code zero, delivering nothing, which is
the worst shape a misconfiguration can take. `ValidateOnStart` runs as the worker PROCESS starts,
and that is before the worker reports its functions for indexing, so the worker refuses first:

```
OptionsValidationException: Notices:Schedule must be set: it is the timer trigger's cadence, …
WHATEVER Notices:Runner says. …
Failed to start language worker process for runtime: dotnet-isolated.
```

Measured, and `func start` exits 1. ⚠️ The host's own "Job host started" line still appears, because
the host comes up and keeps retrying a worker it cannot get — read the exit code, not that line.

**No NOTICE secret lives here** — which is not the same as none. `ConnectionStrings:DefaultConnection`
reaches the whole store and is private configuration; what this host does not hold is a signing key.
`AddInfrastructure` registers a DbContext and validates nothing, the DbContext has no
`SaveChangesInterceptor`, and a delivered notice writes no audit row — so none of
the six validated secrets is needed. The `notify` verb holds the chain key because it walks the
chain; this host walks nothing.

---

## What one tick looks like

```
Notice relay: delivered notice 01a08089ed98788bbb3c029cddc29adf (PinChanged) as ….eml
Notice relay: sweep as func/GURGANT/28584/3658b5a3 claimed 2, delivered 2, left 0 owed, into C:\azurebank-pickup
```

`func/…` is the kind prefix that lands in `SubscriberNotices.LeasedBy`, and is how a person reading a
held row knows which runner holds it: `api`, `verb`, or `func`.

The recipient's address is in the `.eml` and in **no log line**, here or anywhere — the rule lives in
`NoticeDeliveryRun`, which all three runners share.

---

## What the rehearsal is worth

It exercises the *shape* of a deployment — a second deployable with its own lifetime and
configuration surface, a trigger, a host that elects a singleton in blob storage — on one machine,
started by hand, against a storage emulator. It says nothing about availability, scaling, cold start
under load, managed identity or a consumption plan, and ADR-0051 says so in those words.

The last hop is unchanged: a file on this machine, seen by nobody. What "delivered" would need is in
[`docs/deferred/relaying-the-enrolment-notice.md`](../../../docs/deferred/relaying-the-enrolment-notice.md).

---

## Tests

CI builds this project with the solution; CI does **not** run `func start`. The unit tests construct
the Function class directly — which is what the isolated worker does — so they cover the flag check,
the runner name, the tick measurement and the lease warning, and they cannot cover the trigger
binding or `%Notices:Schedule%` resolving. That division is stated in the tests themselves.

- `AzureBank.Tests/Unit/Functions/DeliverOwedNoticesTests.cs`
- `AzureBank.Tests/Integration/NoticeFunctionStartupTests.cs`
