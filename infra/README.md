# AzureBank on Azure Container Apps

How the demo is created, deployed, read, stopped and removed. Everything here is run by hand: the
templates and the scripts from a terminal, the deployment from one workflow. Commands are
PowerShell 7. Why it is built this way, what was weighed and what was left as it is:
[ADR-0061](../docs/adr/0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md).

**State of this document.** The templates compile and the scripts are tested offline against
stand-ins. Until 2026-10-03 nothing in this folder had run on Azure. That day the first session
of the first deployment ran, steps 1 to 10. Steps 1 to 5 ran as written and read back what they
should. Step 6 stopped at the users file's first check, and a read-only look before it ran again
found that a second check of the file would stop it too, each time on something of Azure's own
that the file did not expect. Both checks were narrowed, and the file then made the two users.
Each signed in from the probe job of step 7 with the roles expected, a token for the other
identity was refused, and a SQL sign-in was refused for the reason expected. The what-if of
step 9 could not name the app: the template's own check of the app's values hid it. The check
was moved for that, and the same what-if, run again that day on the changed template, listed the
nine resources expected and nothing it could not work out
([Measured on Azure](#measured-on-azure)). The second session ran the same day, steps 12 to 21:
the images were published, the template created the app, `deploy.py` migrated the database and
moved the app from the owner's terminal, and the workflow did the same twice as the deployment
identity. A later commit, with a 17th migration, was deployed by the workflow that day as well.
The demo is deployed. Five things did not go as this document had them, and each is told at its
step: the three packages were public without the owner's step (step 14); the offer refuses the
test notification (step 15); `--job-log` printed the lines of the next execution too (step 16);
the last-resort migration got no token within the tool's connect timeout (step 19); and the query
of step 20 was refused as written, and once it ran it showed that the alert on the workspace does
not count lines. That alert was deleted, and the template now leaves it out unless it is asked
for.

**On 2026-10-05 this folder gained what turns the public demo on, and none of it has run on
Azure.** The template has a switch, `demo`, off unless it is asked for. With it on, both
containers of the app are told they are the demo, and a second job, `azurebank-pool`, runs
`recycle` every four hours as the app's database identity; the Deny policy lets a job of that
name, and no other, run on a schedule. `deploy.py` reads from the app whether the demo is on,
moves the pool job with the commit when it is, and has three more commands for the owner's
terminal. Why, and what was weighed:
[ADR-0064](../docs/adr/0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md).
The third session, [Turn the demo on](#third-session-turn-the-demo-on), is steps 22 to 33 below.
It has not been run: every value in it is what the code and the records lead to expect, and it
says so. The steps of the first two sessions, and what they read back, are left as they were
measured, with the demo off.

**On 2026-10-06 it gained one thing more, and that has not run on Azure either: the alerts can
also reach the owner's phone.** The action group takes one receiver of the Azure mobile app when
a run names the account that app was set up with, and holds the mailbox alone when none does.
Step 25 is where the account is carried, the road tried once and what arrives read; what is not
known of it is under [Not measured yet](#not-measured-yet).

What ran on Azure before, on 2026-10-02, is a throwaway trial: a resource group in the same
subscription and region, created and deleted that day, in which requests of the shapes this
folder makes were sent by hand, with `az rest` and go-sqlcmd, and not by this folder's template
or scripts. Not every request of this folder was among them. What the trial saw is under
[Measured on Azure](#measured-on-azure) as well. The other facts marked *measured* were read on
those days, from Azure or GitHub with read-only commands, on a local stack of this code, or on a
local SQL Server. What no run has shown yet is listed under
[Not measured yet](#not-measured-yet), every value below that no run has shown is marked as
expected, and what to do when Azure refuses a step is written down before the step runs
([If Azure says no](#if-azure-says-no)).

- [What this creates](#what-this-creates)
- [What it costs, and what bounds it](#what-it-costs-and-what-bounds-it)
- [What you need](#what-you-need)
- [Create it once](#create-it-once)
- [If Azure says no](#if-azure-says-no)
- [Deploy a commit](#deploy-a-commit)
- [Reading the logs](#reading-the-logs)
- [Switching the logs off](#switching-the-logs-off)
- [Stop the app by hand](#stop-the-app-by-hand)
- [When something fails](#when-something-fails)
- [If something was stolen](#if-something-was-stolen)
- [Where each secret lives](#where-each-secret-lives)
- [What each identity can do](#what-each-identity-can-do)
- [Who can reach the database server](#who-can-reach-the-database-server)
- [What only the owner does](#what-only-the-owner-does)
- [Before renaming or transferring the repository](#before-renaming-or-transferring-the-repository)
- [Changing the infrastructure later](#changing-the-infrastructure-later)
- [Turning the demo back](#turning-the-demo-back)
- [Removing everything](#removing-everything)
- [What is not here](#what-is-not-here)
- [Measured on Azure](#measured-on-azure)
- [Not measured yet](#not-measured-yet)
- [Checking these files](#checking-these-files)

## What this creates

One resource group, `azurebank-demo`, in Italy North. `main.bicep` is run with `deployApp=false`
(the foundation, which needs no image) and later with `deployApp=true`.

**The foundation: fourteen things**

| Resource | What it is |
| --- | --- |
| `azurebank-env` | A Container Apps environment in the mode `WorkloadProfiles`, which the template names, with the Consumption profile only. What its containers print goes to Azure Monitor (`destination: 'azure-monitor'`), which holds no workspace key |
| `azurebank-logs` | A Log Analytics workspace: Analytics plan, kept 30 days, a daily cap of 0.05 GB, no access by shared key |
| `to-azurebank-logs` | One diagnostic setting on the environment: console and system logs to that workspace. Not the ingress log, which would record every path with its query string and every caller's address |
| `azurebank-<letters>` | A SQL logical server, TLS 1.2, that takes Microsoft Entra sign-ins only. The template gives it no SQL administrator login and no password: **no database password exists**, in any file, secret or parameter. Its one administrator is the owner's account |
| `AzureBank` | The database: Basic, 5 DTU, 2 GB, locally redundant backups |
| `keep-the-database` | A lock (cannot delete) on the database. On the database and not on the server, so that the temporary firewall rule below can still be removed |
| `AllowAzureServices` | The firewall rule `0.0.0.0`: see [Who can reach the database server](#who-can-reach-the-database-server) |
| `azurebank-deploy` | A managed identity with one federated credential (the next row). Until the app exists it holds no role on anything |
| `github-demo` | The federated credential: GitHub may sign in as `azurebank-deploy` only from the environment `demo` of `Gurgant/azurebank-v2` |
| `azurebank-app` | The managed identity the app signs in to the database as. It has no role on any Azure resource: it is only a user inside the database |
| `azurebank-migrate` | The same for the migration job, with the right to change the schema |
| `AzureBank deploy <letters>` | A custom role of nine actions: read and write the app and a job, start a job, read its executions, read the app's revisions and replicas. It cannot list secrets, delete or stop |
| the policy definition | "AzureBank: one small replica, manual jobs, the pool job scheduled" (`guardrails.bicep`), written at subscription level because a custom definition cannot live in a resource group. It refuses nothing by itself. Until 2026-10-05 its name ended at "manual jobs", and the definition the first deployment created carries that name until a run of the template sends this one (step 23) |
| `azurebank-shape` | That policy assigned to the resource group, effect Deny. It refuses, whoever asks: more than one replica, a minimum above zero, several active revisions, plain HTTP, more than two containers, an init container, a container above half a vCPU; and for a job, a trigger other than Manual, parallel runs, an init container, a container above half a vCPU. One exception, by name: a job named in the parameter `scheduledJobs` may have the trigger `Schedule`. `main.bicep` hands over one name, `azurebank-pool`; every other job is still started by hand, the migrate job among them. Until 2026-10-05 the rule had no exception, and no request under it has been sent to Azure yet |

**Why the template names the environment's mode.** On this subscription a request for an
environment that names no mode is taken as *Express*. Sent by hand on 2026-10-02, such a request
(API version `2025-01-01`, the Consumption profile, logs to Azure Monitor) was refused with HTTP
400 and the code `ExpressEnvironmentFeatureNotSupported`: "'Azure Monitor Log Destination' is not
supported ... on express environments". Express has no Azure Monitor logging, no second container
in an app and no jobs (Microsoft's page "Azure Container Apps express overview", dated 2026-09-21,
read on 2026-10-03), and this design needs all three. That page's FAQ says a template that creates
a new environment creates a standard one by default; that is not what this subscription did. So
`main.bicep` says `environmentMode: 'WorkloadProfiles'`, on API version `2026-07-01`, which has the
property. Sent that way the request was accepted, but neither the request nor its answer is in
the trial's record: it was sent by hand between two of the trial's recorded runs, and its answer
was noted at the time. What the record holds is what came after: an environment in the region,
where there had been none, read back with the logs destination and the Consumption profile, and
two jobs that ran in it. No read in the trial's record shows the property `environmentMode`
coming back. The first deployment's environment, made by this template, read it back as
`WorkloadProfiles` on 2026-10-03 (steps 2 to 4); `Assert-EnvironmentMode` below still does not
rest on the property alone. The Bicep CLI 0.47.16 has no types for that API version: one line of
the template silences its warning (BCP081) for that one resource, and a test reads the compiled
properties instead.

**Inside the database**, created by `sql-principals.ps1`, not by the template: two users, each bound
to one identity, by its client ID, and with no password. `azurebank_app` reads and writes rows
(`db_datareader`, `db_datawriter`); `azurebank_migrator` does the same and may change the schema
(`db_ddladmin`).

**With `deployApp=true`: eight more**

| Resource | What it is |
| --- | --- |
| `azurebank` | The app: the BFF (0.25 vCPU, 0.5 GiB) and the API (0.5 vCPU, 1 GiB) in one replica, zero to one replica, single revision, with the identity `azurebank-app` attached. HTTPS ingress to the BFF's port 8080. The API listens on `127.0.0.1:5068` only: nothing outside the replica can reach it. Three probes, on the BFF. Nine secrets, each reaching a container by reference: eight application keys and the connection string, which holds a server name and a client ID and no password. The eighth key is the demo's, `demo-client-key`: only the `api` container is handed it, and nothing uses it while the demo is off. Both containers carry `Demo__Enabled`, written as `false` unless `demo` is true, and the `api` container `Demo__Claim__MaxPerClientPerDay=1000` (below). The BFF does not keep its one line per request (`Serilog__MinimumLevel__Override__Serilog=Warning`); its warnings and its 5xx lines stay. Until 2026-10-05 this row counted eight secrets, seven of them keys, and no setting of the demo: that is the app the first deployment created, and it stays so until the next run of the template ([Changing the infrastructure later](#changing-the-infrastructure-later)) |
| `azurebank-migrate` | A manual job: the tools image with the argument `migrate`, no retry, 600 s, the identity `azurebank-migrate` attached, one secret (its own connection string, no password) |
| two role assignments | The custom role, to the deployment identity, on the app and on the job and nowhere else. From here the workflow can change the app |
| `azurebank-owner` | An action group with one e-mail receiver, given as a parameter. Since 2026-10-06 a second parameter, `alertPushAccount`, adds one receiver of the Azure mobile app beside it, named `owner-phone`, for the e-mail address that app was set up with on the owner's phone. The parameter is empty by default, and the group is then the one it was, the mailbox and no other receiver. No run has sent that receiver to Azure: the group the first deployment created holds the mailbox alone (step 25) |
| three alert rules | By e-mail, and with the phone's receiver in the group also as a notification of the Azure mobile app; all on the app: more than 66,667 requests in an hour; more than 3.3 GiB sent in a day; the replica running more than about 2.2 hours in a day (an average replica count above 0.093). Until 2026-10-06 this row said "E-mail only" |

**With `demo=true` as well: two more, and the app is told it is the demo.** The switch is a
parameter of the template, `false` by default. `secrets.ps1` writes it: what the deployed app
does now, or `true` with `-DemoOn` (step 25). Added on 2026-10-05; none of it has been sent to
Azure.

| Resource | What it is |
| --- | --- |
| `azurebank-pool` | A scheduled job: the tools image with the argument `recycle`, which deletes the demo copies whose time is over and tops the pool up ([`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md)). The schedule is `0 */4 * * *`, a variable of the template and not a parameter; Azure is expected to read it in UTC. One run at a time, no retry, 600 s (the parameter `poolTimeout`, 60 to 840), 0.25 vCPU and 0.5 GiB. It carries the identity `azurebank-app`, so it signs in to the database as the app does, never as the migration. Two secrets of its own, which the template writes from the two expressions it writes the app's from: the app's connection string and the app's PIN pepper. Four settings: those two by reference, `Demo__Enabled=true` as a plain word, and `Demo__Claim__MaxPerClientPerDay=1000` |
| a third role assignment | The custom role, to the deployment identity, on the pool job: without it the workflow could neither read that job nor move its image |

With `demo=true` both containers of the app carry `Demo__Enabled=true`: the page carries the
demo's tag, a visitor claims a private copy, registration is closed, and only the owner of a
claimed copy signs in (ADR-0063). The job waits for the app in the template. Expected of Azure and
not provoked: when the app's own update fails, a job that waits for it is not created. That does
not cover a new revision that never gets ready, which for Azure is an update that succeeded:
step 25 says what is done then, before the job's next run.

**Why 1,000 claims a day for one client.** `Demo__Claim__MaxPerClientPerDay` is 10 by default and
1,000 is its range's maximum. The BFF counts a client by the address it sees, and no run of this
folder has told it to trust a proxy's forwarded header, so behind the ingress it may see one
address for every visitor: ten copies a day for everybody. What it sees there has not been
measured (step 30), and until it has, the template writes 1,000 on the `api` container and on
the job, from one variable (ADR-0063, decision 14). Every other number of the demo stays at its
default on all three. Until 2026-10-06 this paragraph said "no file in this folder tells it to
trust a proxy's forwarded header": since that day the template can, when a run names the
networks of the ingress (the parameter `proxyNetworks`, empty by default; step 30 says when and
how). No run has named any, and the 1,000 stays until one has and step 30's proof has passed.

**A ninth, only when it is asked for: an alert rule on the workspace.** Until 2026-10-03 the
template built it with the others (nine more, four alert rules), and the first deployment created
it at step 15: more than 50,000 records ingested in an hour, by the metric `Ingestion Volume`.
Step 20 then read that metric against the rows of one hour. The workspace had ingested 446 rows
and the metric had no time series at all, so the rule could not count lines. It was deleted that
day, and the parameter `logVolumeAlert` is now `false` unless a run passes
`@('logVolumeAlert=true')`, which is for whoever measures again
([Measured on Azure](#measured-on-azure)).

**And a check that creates nothing.** A run with `deployApp=true` also deploys `app-inputs.bicep`
as the nested deployment `azurebank-app-inputs`. Its parameters are the values the app needs, each
with the length it must have: the image tag exactly 40 characters, the alerts' address and seven
of the eight secrets at least one character, and the eighth, the demo's client key, at least 32:
the API refuses to start with the demo on and a shorter one (the eight stay secure parameters
there too). The account for the owner's phone is not one of them: left empty it adds no
receiver, and the template checks nothing of it. A value that does not fit fails that
deployment, and the app, the migrate job, the pool job and the action group wait for it, so none
of them is sent without its values: seen offline and on a local engine for the tag, the address
and the seven
([Checking these files](#checking-these-files)), not yet on Azure. The client key's 32 characters
have not been seen refused by any engine: the offline tests read the decorator in the compiled
check. Until 2026-10-05 the check asked for seven secrets, and three resources waited for it.
The check used to be the app's name, through `fail()`. A what-if works out no expression that reads a secure parameter, and on
2026-10-03 it could name neither the app nor the role assignment on it (step 9). Now the name is
the plain `azurebank`, and the same what-if, run again that day, named both.

The environment variables of the two containers are the ones `compose.yaml` sets, plus the one
Serilog setting on the BFF and, since 2026-10-05, the demo's: `Demo__Enabled` on both, and on the
`api` container `Demo__ClientKeySecret` and `Demo__Claim__MaxPerClientPerDay`. No container of
the template carries a key id of the pepper or a previous pepper. A forwarded-headers setting is
carried by one container, the `bff`, and only in a run that names networks of proxies: one
setting a network, `ForwardedHeaders__KnownIPNetworks__0` and on, after its six. With none named,
which is the default, no container carries any; until 2026-10-06 this paragraph said "and none a
forwarded-headers setting" of every run. The framework's own switch for forwarded headers,
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`, is written nowhere, and `--check` refuses a deployed app
that carries it: with it set to `true` the BFF believed whatever a caller wrote (measured on a
local process on 2026-10-06: twelve sign-ins, each naming another address in `X-Forwarded-For`,
were all answered, where without it the eleventh and the twelfth were refused). The connection
limits are the hosts' own defaults (ADR-0058); the template sets none.

Three container images, public in GHCR, tagged with the full commit SHA: `azurebank-api`,
`azurebank-bff`, `azurebank-tools`.

## What it costs, and what bounds it

Prices are from the Azure Retail Prices API for Italy North, in USD, read on 2026-10-02. The free
amounts of Container Apps and of Log Analytics are the ones their pricing pages document.

| Meter | Price | Free each month |
| --- | --- | --- |
| SQL Database Basic | $0.161 a day | none |
| Container Apps, vCPU while active | $0.000034 a vCPU-second | 180,000 vCPU-seconds |
| Container Apps, memory while active | $0.000004 a GiB-second | 360,000 GiB-seconds |
| Container Apps, requests | $0.40 a million | 2 million |
| Data out to the internet | $0.087 a GB | the first 100 GB |
| Log Analytics, Analytics logs ingested | $2.99 a GB | the first 5 GB of the billing account. **Not confirmed for this credit offer** |
| Metric alert rules | $0.10 a month each | the first 10; three are used |
| Environment management, private endpoint, planned maintenance; Dedicated plan | $0.13 an hour each; $0.10 an hour | none: this template uses none of them, and they must read 0 |

**Expected each month:** the database, $4.83 to $4.99. The app costs nothing while it stays inside
the free amounts: 0.75 vCPU and 1.5 GiB use both up together after 66.7 hours of a running replica.
Past that, a replica-hour is $0.1134.

**The pool job, with the demo on: computed, not measured.** Every four hours is 180 runs in 30
days, at 0.25 vCPU and 0.5 GiB. A run of 30 s, which is what a migrate execution took on
2026-10-03 (24 to 37 s), makes 1,350 vCPU-seconds and 2,700 GiB-seconds a month: 0.75 % of each
free amount. If every run lasted its whole 600 s it would be 15 %, and 21 % at the largest
timeout the template allows, 840 s. Expected, and not read on a cost line: that a job's seconds
are billed on the app's two meters and count against the same free amounts. If none of them were
free, a second of a run is $0.0000105 by the two rates above: $0.06 a month at 30 s a run, $1.13
at 600 s, $1.59 at 840 s. No pool run has been timed on this database: the first fill's seconds
(step 26) replace these.

**A notification on the phone: not read.** What Azure bills for a push notification of an action
group, and whether this offer has a free amount of them, was not looked up: the table above has
no row for a notification of either kind, and none was added for a price nobody read. No alert
has been seen firing, so no notification of either kind has been seen billed
([Not measured yet](#not-measured-yet)). A charge would show in the cost by meter
([Afterwards](#afterwards)).

**The logs: $0 expected.** A cap of 0.05 GB a day is 1.50 to 1.55 GB a month, under the free 5 GB.
Three things are not certain:

- **Whether the free 5 GB apply to this offer.** No page read says so. If they do not, the logs cost
  up to 1.55 GB at $2.99, **$4.63 a month**, with the cap filled every day. A cost line settles it
  (the rule is below).
- **The cap is not a hard bound.** The workspace stops taking lines some time after the cap is
  reached. Microsoft's page says the excess "can be particularly large if the workspace is
  receiving high rates of data", and that it is billed. How large: not stated.
- **What a line costs.** In the trial the lines of two jobs were billed 438 bytes each on average
  (45 lines, 19,732 bytes) for 194 characters of text: about 244 bytes a line on top of the text.
  A cap of 0.05 GB is about 114,000 lines of that size a day (computed). What a line of the app
  is billed has not been read: the numbers below are its console bytes, measured locally, with
  that overhead added where it says "computed". What the workspace took on 2026-10-03, the day
  of both sessions, read 0.19 MB, console and system logs together
  ([Measured on Azure](#measured-on-azure)).

**What bounds the spending, meter by meter.** The demo's subscription is a credit offer with a
spending limit: no bill is possible, and when the credit (86 at the time of writing) is used up the
whole subscription is disabled, the database included.

| Meter | What bounds it | The whole credit is gone after |
| --- | --- | --- |
| vCPU and memory | one replica, by the template and by the Deny policy | 36 days of a replica that is never idle |
| Requests | **nothing** | 217 million requests: 25 days at 100 a second, 2.5 days at 1,000 |
| Data out | **nothing** | 1,089 GB: 30 hours at 10 MB a second |
| Logs | the daily cap, **less what gets past it** | not computable: the excess is not stated |

Requests are counted at the ingress whatever the app answers. The BFF's own rate limiter does not
bound them: the page's files are served before it, the two health paths are excluded from it, and a
refused request is still a request. Any request at least every five minutes keeps the replica up.

**What a stranger can write into the log.** Measured locally, with the BFF's request line off as
the template sets it:

- A page or a file of the page, existing or not: nothing (on the built image, three pages, one
  real file and two missing ones wrote 0 lines).
- A request to `/api/...` with no session: one warning of about half a kilobyte, **whether the rate
  limiter lets it through or rejects it**. On a build of the BFF from source, 400 such requests in
  0.9 s wrote 400 lines, 209,200 bytes; on the built image the two warnings are 538 and 510 bytes.
  The rate limiter therefore does not bound the log. Computed, not measured: the two warnings of
  the built image average 524 bytes; with the 244 bytes a line of the trial added, such a warning
  is billed about 768 bytes, and a day's cap is about 65,100 such requests: eleven minutes at 100
  a second. By the 524 console bytes alone it would be about 95,400 and sixteen minutes.
- During a database outage a failed sign-in writes about 28.7 KB of console text: at most about
  1,740 of them fill the day, and fewer once each of its lines carries that overhead (how many
  lines the text is was not kept). The sign-in limiter allows ten a minute, so that takes at most
  about three hours.

Past the free 5 GB, or without them, such a request costs about $2.30 a million on the log meter
(computed from the 768 bytes) on top of $0.40 on the request meter: the credit goes nearly seven
times faster, for whatever gets past the cap. A stranger can also fill the day's cap on purpose:
the log is then dark until its reset, and a migration run on that day leaves a verdict and no text.
So does a pool run: its exit code is read from Azure all the same, and its one summary line is
not there to read.

**What a stranger can make the demo hold, once it is on.** None of it costs money; each is a way
the demo stops being usable. All of it is read from the code and its defaults, not seen on Azure.

- **The pool is the only bound on claims.** While the cap for one client is 1,000 a day (above),
  it stops nobody. What is left are the pool's own numbers, at their defaults: 50 free copies, a
  top-up that builds nothing once the claims of the last 24 hours reach 150, 200 changing
  requests in a copy, 24 hours for a copy
  ([`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md), sections 1 and 9). 50 claims
  leave every other visitor with 429 `DEMO_POOL_EMPTY` until the next run, up to four hours
  later; 150 in a day leave them so until the count lets a run build again.
- **Two of the BFF's three rate limits count by one function of the caller's address**
  (`backend/src/AzureBank.Bff/Program.cs`, `ClientAddress.Of`). Ten a minute, together, for
  sign-in, the claim, registration, re-authentication and the handle's rename
  (`RateLimiting:AuthPermitLimit`, and the five actions of `BffAuthController` that carry that
  policy); 300 a minute for every other request the limiter sees (`RateLimiting:GlobalPermitLimit`).
  The third, 20 recipient lookups a minute (`RateLimiting:LookupPermitLimit`), counts by the
  signed-in user, and by the address only for a caller with no session (`LookupPartitionKey`).
  **If the app sees one address for every visitor, those two are shared by everybody**: one request
  every six seconds to any of the five doors answers everybody else 429 there, and a script at
  five requests a second, or a handful of visitors at once, answers 429 to every call of every
  signed-in visitor. The page itself would still load, since its files are served before the
  limiter: the demo would look up while nothing in it works. Whether the app sees one address is
  what step 30 measures; until then this is the case to expect. If it does, the same step tells
  the app the networks of the ingress, so that it counts each visitor by the visitor's own
  address (added on 2026-10-06, and not run).
- **The database grows and nothing warns.** A run deletes a copy's users and everything of
  theirs, and never an audit row (`docs/runbooks/demo-pool.md`, section 7). The database is Basic,
  2 GB, and no alert watches its size.

**What warns:** the three alert rules, all on the app, by e-mail; and, once a run has given the
action group the account of the Azure mobile app on the owner's phone (step 25), as a
notification on that phone too. The phone is a second road for the same three warnings, not a
fourth warning, and no notification has been seen on it. Until 2026-10-06 this line said "by
e-mail" and named no other road. Nothing warns of the log's volume: the rule that was meant to
did not count lines (step 20). And nothing warns that the cap itself was reached: the alert
Microsoft documents for that is a log search rule, $0.50 a month or more, and is not used.
**What stops the app:** the owner, by hand ([Stop the app by hand](#stop-the-app-by-hand)).
**What stops the logs:** the owner, by rule ([Switching the logs off](#switching-the-logs-off)).
Nothing stops either automatically.

## What you need

- The Azure CLI with no extension (measured here: 2.90.0, `az extension list` empty), the Bicep CLI
  on `PATH` (0.47.16), PowerShell 7, Python 3.12 or later, Docker, the GitHub CLI, the .NET SDK 10.
- Microsoft's `sqlcmd` (go-sqlcmd) 1.10.0 or later, at `C:\Program Files\sqlcmd\sqlcmd.exe` unless
  `-SqlcmdPath` says otherwise. `winget install --id Microsoft.Sqlcmd -e` puts it there. The users
  script starts it by that path and checks it first: the older ODBC `sqlcmd` answers to the same
  name, and a terminal opened before the install still finds only that one.
- Owner of the subscription, and the account that will be the database's Microsoft Entra
  administrator: `secrets.ps1` takes both from `az login`.
- Admin of the repository, for the environment `demo`, its secrets and the three packages.
- The resource providers `Microsoft.App`, `Microsoft.Sql`, `microsoft.insights`,
  `Microsoft.ManagedIdentity`, `Microsoft.Authorization`, `Microsoft.OperationalInsights` registered
  (measured: all six are).
- For the alerts on the owner's phone, which step 25 adds and which a session can go without: the
  Azure mobile app on that phone, signed in, with the phone's settings allowing its
  notifications, and the e-mail address the app was set up with. Whether that address is the
  sign-in name `secrets.ps1` takes from `az login` is not known, so the script never takes one
  for the other.

Two people act below. **The owner** signs in, clicks in GitHub's settings, approves a deployment,
reads the mailbox and, from step 25, looks at his phone. **The operator** types the commands in
a terminal where the owner has run `az login` and `gh auth login`; it can be the owner. Every
step that writes is run on the owner's word, given for that step.

Rules for every command in this file:

- No secret is typed, printed, or put on a command line. The one file that holds them is written by
  `secrets.ps1` outside the repository and removed in a `finally`.
- Never `--debug`, and no deployment debug setting that stores request content.
- No `( ) & |` inside an argument to `az`: on Windows `az` is a `.cmd` file and `cmd.exe` reads
  them. Filter JSON in PowerShell instead of with `--query`.
- `$env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = 'no'` in the terminal: a command that lives in an
  extension is then refused instead of installing the extension. Nothing here needs one.
- Never `az account get-access-token`, and never `az sql server ad-only-auth disable`.

## Create it once

Two sessions. The first needs no image and can run before the workflow is on `main`. The second
needs the workflow on `main`, and the GitHub environment in place before that. A third,
[Turn the demo on](#third-session-turn-the-demo-on), needs the app deployed; it was written on
2026-10-05 and has not been run.

Every step that writes is marked **writes**. Run from the repository root.

```powershell
$env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = 'no'
$group  = 'azurebank-demo'
$folder = Join-Path $env:LOCALAPPDATA 'AzureBank\deploy'   # where secrets.ps1 writes

# What a what-if would change: the change and the type of each resource, and for a resource it
# would modify, the properties that differ. Never a name, a value or the subscription.
function Show-WhatIf([string]$File) {
    (Get-Content -LiteralPath $File -Raw | ConvertFrom-Json).changes | ForEach-Object {
        $parts = ($_.resourceId -split '/providers/')[-1] -split '/'
        $type = @($parts[0]) + @(for ($i = 1; $i -lt $parts.Count; $i += 2) { $parts[$i] })
        $paths = if ($_.changeType -eq 'Modify') { ': ' + (@($_.delta.path) -join ', ') } else { '' }
        '{0,-12} {1}{2}' -f $_.changeType, ($type -join '/'), $paths
    }
}

# One run of the template: compiled by the Bicep CLI on PATH, a what-if into the protected folder,
# its changes, then the deployment. The answer of the deployment is one word; without --query az
# prints the parameters back. $Override takes parameters such as 'denyPolicy=false'.
function Invoke-Template([string]$Name, [string[]]$Override = @()) {
    $template = "$folder\main.json"
    try {
        bicep build infra/main.bicep --outfile $template
        if ($LASTEXITCODE -ne 0) { throw 'The template did not compile.' }
        az deployment group what-if --resource-group $group --template-file $template `
            --parameters "@$folder\parameters.json" @Override --no-pretty-print --only-show-errors > "$folder\what-if.json"
        if ($LASTEXITCODE -ne 0) { throw 'The what-if failed.' }
        Show-WhatIf "$folder\what-if.json"
        if ((Read-Host 'Deploy this? (yes/no)') -ne 'yes') { return }
        az deployment group create --name $Name --resource-group $group --template-file $template `
            --parameters "@$folder\parameters.json" @Override --query properties.provisioningState --output tsv
    } finally {
        Remove-Item -LiteralPath $template -ErrorAction Ignore
    }
}

# Which identity each app and each job in the group carries, read from the resources themselves.
# Names only: an identity's ID holds the subscription, and the entry under it two more IDs.
function Show-Identities {
    $scope = az group show --name $group --query id --output tsv
    foreach ($kind in 'containerApps', 'jobs') {
        (az rest --method get --url "https://management.azure.com$scope/providers/Microsoft.App/${kind}?api-version=2025-01-01" |
            ConvertFrom-Json).value | ForEach-Object {
                $attached = $_.identity.userAssignedIdentities
                $names = if ($attached) { $attached.PSObject.Properties.Name | ForEach-Object { ($_ -split '/')[-1] } }
                '{0}: {1}' -f $_.name, (@($names) -join ', ')
            }
    }
}

# The executions of a job, read with the API version that carries each container's exit code.
function Show-Executions([string]$Job) {
    $id = az containerapp job show --name $Job --resource-group $group --query id --output tsv
    (az rest --method get --url "https://management.azure.com$id/executions?api-version=2026-07-01" |
        ConvertFrom-Json).value | ForEach-Object {
            $codes = @($_.properties.detailedStatus.replicas.containers.code) -join ','
            $both = $_.properties.startTime -and $_.properties.endTime
            $took = if ($both) { [int]($_.properties.endTime - $_.properties.startTime).TotalSeconds } else { '?' }
            '{0}: {1}, {2} s, exit code [{3}]' -f $_.name, $_.properties.status, $took, $codes
        }
}

# The environment's mode, read with the API version that has the property. WorkloadProfiles
# passes and any other mode throws: an Express environment takes neither a second container nor a
# job. The first deployment's reads showed the property (2026-10-03); an answer without it still
# passes only if the logs go to azure-monitor, the destination Express refused in the trial.
function Assert-EnvironmentMode {
    $scope = az group show --name $group --query id --output tsv
    $properties = (az rest --method get --url "https://management.azure.com$scope/providers/Microsoft.App/managedEnvironments/azurebank-env?api-version=2026-07-01" |
        ConvertFrom-Json).properties
    $mode = $properties.environmentMode
    if ($mode -eq 'WorkloadProfiles') { return "The environment's mode: $mode" }
    if ($mode) { throw "The environment's mode reads '$mode', not WorkloadProfiles. Stop: deploy nothing into it." }
    if ($properties.appLogsConfiguration.destination -ne 'azure-monitor') { throw 'The answer names no mode, and the logs do not go to azure-monitor. Stop: deploy nothing into it.' }
    'The answer names no mode; the logs go to azure-monitor, which an Express environment was refused.'
}
```

The template is compiled first and the deployment is given the JSON, because `az` looks for a
Bicep of its own and has none on this machine (measured: `az bicep version` answers that none is
found). What is deployed is then what the tests compiled.

### First session: before any image exists

#### 1. Look before writing (operator)

```powershell
az account show --query name --output tsv
az group exists --name $group
```

Before the first run: the subscription's name, and `false`. Start every later session with these
reads instead:

```powershell
$server = az sql server list --resource-group $group --query '[0].name' --output tsv
az sql server firewall-rule list --resource-group $group --server $server --query '[].name' --output tsv
az sql server ad-only-auth get --resource-group $group --name $server --query azureAdOnlyAuthentication
az monitor log-analytics workspace show --resource-group $group --workspace-name azurebank-logs --query workspaceCapping
az containerapp job list --resource-group $group --query '[].name' --output tsv
Show-Identities
Assert-EnvironmentMode
```

Expected, each time:

- **Firewall rules:** `AllowAzureServices` and nothing else. A rule named
  `owner-while-creating-users` is the leftover of a run that died; the users script deletes it
  first. Any other rule was made by hand and is deleted by hand.
- **Entra-only:** `true`.
- **The log's cap:** `dailyQuotaGb` 0.05, with what it is doing now (`dataIngestionStatus`) and its
  next reset (`quotaNextResetTime`). `OverQuota` means the log has been dark since the cap was
  reached.
- **Jobs:** none before the app exists; afterwards exactly `azurebank-migrate`. A job named
  `azurebank-probe` is the leftover of step 7: delete it. Once the demo has been turned on
  (step 25), two are expected: `azurebank-migrate` and `azurebank-pool`.
- **Identities:** before the app exists, nothing is listed. Afterwards `azurebank: azurebank-app`
  and `azurebank-migrate: azurebank-migrate`, and nothing else: each database identity on exactly
  one resource. Once the demo has been turned on, a third line is expected,
  `azurebank-pool: azurebank-app`: the app's identity on the app and on the pool job, the
  migration's on the migrate job alone.
- **The environment's mode:** `WorkloadProfiles`, or the line that the answer names no mode and
  the logs go to `azure-monitor`; anything else is a stop. Microsoft's FAQ for Container Apps
  express (read on 2026-10-03) says that an environment with no running app or job and no recent
  activity may be archived, and between the two sessions this one has neither.

#### 2. The resource group and the foundation (operator, **writes**; the database's daily charge starts here)

```powershell
az group create --name $group --location italynorth --query properties.provisioningState --output tsv
try {
    ./infra/secrets.ps1 -Action New          # three parameters and no secret
    Invoke-Template 'foundation'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
Test-Path $folder                            # False
```

Expected in the what-if: fourteen resources to create and nothing to change or delete. On
2026-10-03 it showed exactly that, and the deployment answered `Succeeded`
([Measured on Azure](#measured-on-azure)). The file holds no secret; it stays in the protected
folder because the administrator's sign-in name is shaped like an e-mail address. If the
deployment is refused, the next step is in [If Azure says no](#if-azure-says-no).

Then, before anything else is done with the environment, its mode is read back:

```powershell
Assert-EnvironmentMode                       # WorkloadProfiles, or it throws
```

**A mode other than `WorkloadProfiles` is a stop, and so is an answer with no mode whose logs do
not go to `azure-monitor`.** An Express environment takes neither the app's second container nor
the migrate job. Nothing more is deployed into it, and what was read goes to the owner. An answer
with no mode and the logs on `azure-monitor` passes, and the function says so: Express refused
that destination in the trial.

A stop removes nothing. From this step on the database's daily charge ($0.161) runs while the
owner decides; the road to remove it all is [Removing everything](#removing-everything).

#### 3. The same deployment, a second time (operator, **writes** nothing if Azure accepts it)

The template gives the server no SQL administrator login, through a block that this API version
documents for creation only (in the trial the service then named an administrator of its own,
and no password was sent for it). Sent by hand in the trial, the same request was accepted a
second time and the server read back exactly as before. What this step adds is the template's own
second run, with its what-if, seen once while the database is still empty.

```powershell
try {
    ./infra/secrets.ps1 -Action New          # four parameters: keepLogs is now read from the environment
    Invoke-Template 'foundation'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

It passes if the what-if shows nothing to create and nothing to delete, if a `Modify` on
`Microsoft.Sql/servers` names no property under `properties.administrators`, and if the deployment
then answers `Succeeded`. If it does not pass: stop ([If Azure says no](#if-azure-says-no)).
On 2026-10-03 it passed. Its what-if also named properties to modify on four resources; on the
one read before and after, the environment, the three it named had not changed
([Measured on Azure](#measured-on-azure)).

#### 4. Read back what was created (operator)

These are the expected values. On 2026-10-03 every read gave them
([Measured on Azure](#measured-on-azure)).

| Claim | Read | Expected |
| --- | --- | --- |
| Consumption only | `az containerapp env show -n azurebank-env -g $group --query properties.workloadProfiles` | one entry, `Consumption` |
| Not Express | `Assert-EnvironmentMode` | `WorkloadProfiles`, or the line that the answer names no mode and the logs go to `azure-monitor` |
| Logs on | `az containerapp env show -n azurebank-env -g $group --query properties.appLogsConfiguration`; `az monitor log-analytics workspace show -g $group -n azurebank-logs`; `az monitor diagnostic-settings list --resource <the environment's id>` | destination `azure-monitor`; one workspace, cap exactly 0.05, retention 30, `disableLocalAuth` true; one setting with two categories. With the logs off instead: destination `none`, no setting, no workspace. Anything in between is a defect |
| The database | `az sql db show -g $group -s $server -n AzureBank` | `Basic`, capacity 5, 2147483648 bytes, `Local` |
| The server | `az sql server firewall-rule list`; `az sql server ad-admin list`; `az sql server ad-only-auth get`; `az sql server show --query minimalTlsVersion` | one rule; one administrator; `true`; `1.2` |
| Three identities | `az identity list -g $group --query '[].name'`; `Show-Identities` | `azurebank-app`, `azurebank-deploy`, `azurebank-migrate`; attached to nothing yet |
| The same, asked of the identity | `az identity list-resources -g $group -n azurebank-app`, and for `azurebank-migrate`. On 2026-10-03, before the app, it answered `[]` for both. After step 15, read at the end of that day, it named the app `azurebank` for `azurebank-app` and the job `azurebank-migrate` for `azurebank-migrate`, and nothing for `azurebank-deploy` (until then this row said that nobody had seen it answer after step 15) | No resource yet; after step 15, exactly one each. If the call does not answer, it is dropped and `Show-Identities` stands alone |
| One federated credential | `az identity federated-credential list --identity-name azurebank-deploy -g $group` | one: the GitHub issuer, the subject ending `:environment:demo`, the audience `api://AzureADTokenExchange` |
| The role, unassigned | `az role definition list --custom-role-only true -g $group`; `az role assignment list --assignee <principal id> --all` | nine actions, no data action; no assignment yet. The role can be assigned in this resource group only, so it is listed through the group. In the trial a role of this shape was also found, by its ID and in the list of custom roles, when asked at the subscription |
| The lock | `az lock list -g $group` | one, `CanNotDelete`, on the database |
| The policy | `az policy assignment list -g $group` | `azurebank-shape`, enforcement `Default` |

A value that differs is a defect in the template: fix it before going on.

#### 5. A budget, if the offer allows one (operator, **writes**; optional)

`az consumption budget create` cannot set a notification (its help lists no such argument), so a
budget that warns is one REST call with a body file. The body holds the address the e-mails go to,
taken from the variable `AZUREBANK_ALERT_EMAIL`. `secrets.ps1 -Action New` makes the session's
folder again, open to its owner only, before the file is written into it; both go in the
`finally`. The offer accepts a budget: in the trial one of this amount, with these four e-mail
notifications to one mailbox, was created, read back and deleted. The body below is built
to be the one that was accepted then (the same properties, operator, thresholds and a period of
one year), under another name. A refusal here changes nothing else. On 2026-10-03 this block
created the budget, and it read back as sent ([Measured on Azure](#measured-on-azure)).

```powershell
if (-not $env:AZUREBANK_ALERT_EMAIL) { throw 'Set AZUREBANK_ALERT_EMAIL first.' }
try {
    ./infra/secrets.ps1 -Action New          # for the folder: only its owner can open it
    $notify = { param($percent, $kind) @{ enabled = $true; operator = 'GreaterThan'; threshold = $percent; thresholdType = $kind
                                           contactEmails = @($env:AZUREBANK_ALERT_EMAIL); contactGroups = @(); contactRoles = @() } }
    $from = Get-Date -Day 1
    @{ properties = @{ category = 'Cost'; amount = 20; timeGrain = 'Monthly'
        timePeriod = @{ startDate = $from.ToString("yyyy-MM-'01T00:00:00Z'"); endDate = $from.AddYears(1).ToString("yyyy-MM-'01T00:00:00Z'") }
        notifications = @{ actual_30_percent = & $notify 30 'Actual'; actual_50_percent = & $notify 50 'Actual'
                           actual_100_percent = & $notify 100 'Actual'; forecast_30_percent = & $notify 30 'Forecasted' } } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$folder\budget.json"
    $scope = az account show --query id --output tsv
    az rest --method put --body "@$folder\budget.json" --output none `
        --url "https://management.azure.com/subscriptions/$scope/providers/Microsoft.Consumption/budgets/azurebank-monthly?api-version=2023-05-01"
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

#### 6. The two database users (operator, **writes**)

```powershell
./infra/sql-principals.ps1
./infra/sql-principals.ps1                   # again: nothing may change
```

`sql-principals.ps1` holds no token and no password. It checks the tool (its version, on Windows
its Microsoft signature, and that its `-?` names the sign-in method), reads the IDs of the two
identities with `az identity show`, parses each as a GUID, and starts `sqlcmd`, which signs in as
the server's Microsoft Entra administrator through the `az login` session. The server lets in
Azure services only, so the first try is refused; the script allows the address the server named
in that refusal (a firewall rule called `owner-while-creating-users`), tries again until the rule
works, deletes the rule and reads the rule list back. It writes its report on standard error, with
no address and no ID in it.

All the SQL is in `sql-principals.sql`: one transaction that

1. refuses to run in a database that holds a trigger or any other module (the app's migrations
   create none: measured, 16 migrations, 0 triggers, 0 modules). One kind of module is left out:
   an object in the schema `sys` that is marked `is_ms_shipped`, the two together. A new Azure
   SQL database held one, the view `sys.database_firewall_rules`, which the local engine does not
   have, and the first run of this step stopped on it
   ([Measured on Azure](#measured-on-azure));
2. creates each user with `CREATE USER ... WITH SID = <the identity's client ID>, TYPE = E`, the
   one form that asks the directory nothing, and adds the five role memberships. The ID is the
   client ID and no other: it is what Azure SQL itself stores for an identity it looks up, and a
   user made from it signed in ([Measured on Azure](#measured-on-azure)). A user whose identity
   was deleted and made again is replaced;
3. before it commits, compares every user, role, role membership, permission and schema owner with
   what it expects, prints the name of anything else (a permission on an object with the object's
   schema and name), and keeps nothing unless the lists are clean. One grant of Azure's own is
   expected among them: `SELECT` for `public` on an object in `sys` marked `is_ms_shipped`, the
   two together. The same new database granted it on that view, and a read-only look before this
   step ran again found that the permission list would have refused it
   ([Measured on Azure](#measured-on-azure)).

Each run must end with these two lines and with the rule list:

```text
azurebank_app: db_datareader, db_datawriter; ID as asked: 1
azurebank_migrator: db_datareader, db_datawriter, db_ddladmin; ID as asked: 1
Firewall rules now: AllowAzureServices.
```

An exit code of 0 without both lines is not a pass, and the script says so. These are the final
users, not throwaway ones.

On 2026-10-03, once its two checks were narrowed, the file made the two users. Both runs here
ended with exit 0, these two lines and this rule list; its run inside step 8 printed them too,
and step 7 saw each user sign in with its roles ([Measured on Azure](#measured-on-azure)). The
second run printed what the first did. That shows the lists, not that the run changed nothing:
the file prints the same lines when it has replaced a user.

**The rule that goes with this file: run nothing else as administrator in this database.** The
file runs as the owner of the database, and so does a trigger that one of its statements fires.
The migrator may create such a trigger. The first check stops the file before any statement that
could fire one, and the lists before the commit undo what a trigger created in between did. That
is a guard, not a proof that a database is clean: a trigger that ran inside some other statement
of an administrator could have done what no list looks at. After `Msg 50003` (code found) run
nothing else there: the safe repair is a new database, because any statement an administrator runs
can fire a trigger. On a database nobody has touched, the row for it under
[If Azure says no](#if-azure-says-no) applies instead: what was found there is Azure's own.

#### 7. A sign-in as each identity, before any image exists (operator, **writes** a job and deletes it)

Nothing local can show that a managed identity signs in: a workstation has none, and the local
engine refuses `TYPE = E`. The trial saw it on Azure with identities and users of its own
([Measured on Azure](#measured-on-azure)). This step sees it for the two users step 6 made, before
any image exists, with a throwaway manual job, `azurebank-probe`: 0.5 vCPU, the image
`mcr.microsoft.com/dotnet/sdk:10.0`, and the program below, on Microsoft.Data.SqlClient 6.1.1, the
driver the app ships. It is one C# file that names its package itself, which `dotnet run` builds
and starts; it is written outside the repository and removed with the job. What it does:

- it opens the string in the variable `PROBE_CONNECTION`: the one the template gives the app, with
  `Connect Timeout=30` added so that a slow first token is measured and not cut;
- it prints the milliseconds the open took, `USER_NAME()`, and `IS_ROLEMEMBER` for the three roles;
- on a failure it prints the error's number and class and the chain of exception types;
- it exits 0 for the roles expected (reader and writer; `db_ddladmin` too when `PROBE_EXPECTS_DDL`
  is `1`, and not otherwise), 3 for no token, 4 for a refused login, 5 for other roles and 2 for
  any other failure. "No token" is the chain the trial saw, a `SqlException` around Azure.Identity's
  `AuthenticationFailedException`, and not the class alone: a server that does not answer gives
  class 20 too.

```powershell
# The probe's program, written outside the repository. Set-Probe reads it from there.
$program = Join-Path $env:TEMP 'azurebank-probe.cs'
@'
#:package Microsoft.Data.SqlClient@6.1.1
// Step 7's probe: one open of PROBE_CONNECTION, then who it signed in as and with which roles.
// Exit 0: the roles expected; 3: no token; 4: the login refused; 5: other roles; 2: another failure.
using System.Diagnostics;
using Microsoft.Data.SqlClient;

var expectsDdl = Environment.GetEnvironmentVariable("PROBE_EXPECTS_DDL") == "1";
var clock = Stopwatch.StartNew();
try
{
    using var connection = new SqlConnection(Environment.GetEnvironmentVariable("PROBE_CONNECTION"));
    connection.Open();
    Console.WriteLine($"open: {clock.ElapsedMilliseconds} ms");
    using var command = new SqlCommand("SELECT USER_NAME(), IS_ROLEMEMBER('db_datareader'), " +
        "IS_ROLEMEMBER('db_datawriter'), IS_ROLEMEMBER('db_ddladmin')", connection);
    using var reader = command.ExecuteReader();
    reader.Read();
    var (reads, writes, ddl) = (reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    Console.WriteLine($"user {reader.GetString(0)}: db_datareader {reads}, " +
        $"db_datawriter {writes}, db_ddladmin {ddl}");
    return reads == 1 && writes == 1 && ddl == (expectsDdl ? 1 : 0) ? 0 : 5;
}
catch (Exception e)
{
    var types = new List<string>();
    for (var inner = e; inner is not null; inner = inner.InnerException)
    {
        types.Add(inner.GetType().Name);
    }
    var sql = e as SqlException;
    Console.WriteLine($"failed after {clock.ElapsedMilliseconds} ms: number {sql?.Number}, " +
        $"class {sql?.Class}, {string.Join(" > ", types)}");
    // No token, as the trial saw it: a SqlException around Azure.Identity's exception. Class 20
    // alone is not enough: a server that does not answer gives that class too.
    var noToken = types.Contains("AuthenticationFailedException")
        || types.Contains("CredentialUnavailableException");
    return noToken ? 3 : sql?.Number == 18456 ? 4 : 2;
}
'@ | Set-Content -LiteralPath $program
```

| Start | The job carries | It asks a token for | Must end |
| --- | --- | --- | --- |
| 1 | `azurebank-app` only | the app's client ID | exit 0: reader and writer, not `db_ddladmin` |
| 2 | `azurebank-app` only | the migrator's client ID | exit 3, no token: the isolation the design rests on |
| 3 | `azurebank-migrate` only | the migrator's client ID | exit 0: the three roles |

The job is one request, sent again whenever the identity it carries or the one it asks for
changes. Nothing in it is a secret. In the trial a request of this shape, for a job of its own,
was accepted on this API version, and on 2026-10-03 this one was, three times, with the Deny
policy assigned:

```powershell
# Creates the probe job or replaces it: the one identity it carries, and the identity its program
# asks a token for. $Program is the file the block above wrote.
function Set-Probe([string]$Carries, [string]$AsksFor, [string]$Program) {
    $scope = az group show --name $group --query id --output tsv
    $fqdn = az sql server list --resource-group $group --query '[0].fullyQualifiedDomainName' --output tsv
    $carried = az identity show --resource-group $group --name $Carries --query id --output tsv
    $asked = az identity show --resource-group $group --name $AsksFor --query clientId --output tsv
    $connection = "Server=tcp:$fqdn,1433;Database=AzureBank;Authentication=Active Directory Managed Identity;" +
        "User ID=$asked;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30"
    $source = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Program).Path))
    $container = @{
        name = 'probe'; image = 'mcr.microsoft.com/dotnet/sdk:10.0'; resources = @{ cpu = 0.5; memory = '1Gi' }
        command = @('/bin/sh', '-c', 'mkdir /tmp/probe && cd /tmp/probe && printf %s "$PROBE_SOURCE" | base64 -d > probe.cs && dotnet run probe.cs')
        env = @(@{ name = 'PROBE_SOURCE'; value = $source }, @{ name = 'PROBE_CONNECTION'; value = $connection },
                @{ name = 'PROBE_EXPECTS_DDL'; value = if ($AsksFor -eq 'azurebank-migrate') { '1' } else { '0' } })
    }
    $job = @{
        location = 'italynorth'
        identity = @{ type = 'UserAssigned'; userAssignedIdentities = @{ $carried = @{} } }
        properties = @{
            environmentId = "$scope/providers/Microsoft.App/managedEnvironments/azurebank-env"
            workloadProfileName = 'Consumption'
            configuration = @{ triggerType = 'Manual'; replicaTimeout = 900; replicaRetryLimit = 0
                               manualTriggerConfig = @{ parallelism = 1; replicaCompletionCount = 1 } }
            template = @{ containers = @($container) }
        }
    }
    $body = Join-Path $env:TEMP 'probe.json'
    try {
        $job | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $body
        az rest --method put --body "@$body" --output none `
            --url "https://management.azure.com$scope/providers/Microsoft.App/jobs/azurebank-probe?api-version=2025-01-01"
    } finally {
        Remove-Item -LiteralPath $body -ErrorAction Ignore
    }
}

# One start, once the job has taken the request.
function Start-Probe {
    while ((az containerapp job show --name azurebank-probe --resource-group $group --query properties.provisioningState --output tsv) -ne 'Succeeded') {
        Start-Sleep -Seconds 5
    }
    az containerapp job start --name azurebank-probe --resource-group $group --output none
}
```

The three starts, each followed by `Show-Executions azurebank-probe` until the execution has
ended, and then the end of the job:

```powershell
Set-Probe azurebank-app azurebank-app $program;          Start-Probe   # start 1
Set-Probe azurebank-app azurebank-migrate $program;      Start-Probe   # start 2: the same identity, the other one's token
Set-Probe azurebank-migrate azurebank-migrate $program;  Start-Probe   # start 3: the job's identity is changed
az containerapp job delete --name azurebank-probe --resource-group $group --yes
az containerapp job list --resource-group $group --query '[].name' --output tsv   # nothing
Remove-Item -LiteralPath $program
```

`Show-Executions` reads each execution's status, its length and each container's exit code from
Azure itself, with the API version `deploy.py` uses for the migration's verdict. In the trial
those fields were filled in Italy North: `Succeeded` with exit code 0, and `Failed` with exit code
7 for a run made to end that way. The failed one carried no length (a start or an end time was
absent), which is why the function prints `?` there.

On 2026-10-03 the three starts ended as the table asks: `Succeeded`, 39 s, exit code 0; `Failed`,
no length, exit code 3; `Succeeded`, 38 s, exit code 0. Then no job was left and the program file
was gone ([Measured on Azure](#measured-on-azure)).

#### 8. A SQL-password sign-in must be refused (operator, **writes** only the temporary firewall rule)

```powershell
./infra/sql-principals.ps1 -ProveSqlSignInRefused
```

After the users file has run once more (it should change nothing), and while the firewall still
lets this machine in, the script tries one SQL sign-in with a name and a value made up on the
spot, which exist nowhere. The value travels in the tool's own environment variable for that one
start, never on a command line. It passes only if the server's refusal says
`Azure Active Directory only authentication is enabled`:

```text
Proved: a SQL sign-in is refused, and the server says it is because Microsoft Entra-only authentication is on.
```

Any other refusal is "not proven", and the script exits non-zero after saying the users are fine.
If the made-up sign-in is let in, Entra-only is not what the server enforces.

The trial provoked this refusal by hand, on its own server, for a login that does not exist:
go-sqlcmd exited 1 with "Login failed for user ... Reason: Azure Active Directory only
authentication is enabled." This step does it through the script's own switch, and on 2026-10-03
it printed the line above ([Measured on Azure](#measured-on-azure)).

#### 9. What the second session will add (operator; a what-if, answered "no")

```powershell
$sha = git rev-parse origin/main
try {
    ./infra/secrets.ps1 -Action New -DeployApp -ImageTag $sha   # eight "generated", thrown away below
    Invoke-Template 'app'                                       # answer "no"
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

Expected in the what-if: eight resources to create (the app, the job, two role assignments, the
action group, three alerts), the four `Modify` lines of step 3, nothing deleted, nothing
`Unsupported`, and no refusal. The check of the app's values, `azurebank-app-inputs`, is a nested
deployment and creates nothing, so it should have no line, as the policy's module had none at
step 3. When this step ran, on 2026-10-03, the template still built the alert on the workspace
with the others, so nine were expected that day, four alerts among them. The next paragraph is
the record of that day and keeps its numbers. Since 2026-10-05 the script's report names eight
generated secrets where it named seven: the eighth is the demo's client key, which the app holds
whether the demo is on or off. The what-if's eight resources are the same eight: the switch
`demo` is off unless `-DemoOn` is passed, and the pool job and its role assignment are built
only with it on.

On 2026-10-03, on the template as it was then, this what-if listed seven to create (the job, one
role assignment, the action group, the four alerts), the same four `Modify` lines as step 3, and
two lines `Unsupported`: the app, printed as its unworked ID, an expression around `fail(...)`, and
one that `Show-WhatIf` printed as `Microsoft.Authorization/roleDefinitions`. That second one is the
role assignment on the app: its unworked ID holds the app's, and the last `/providers/` in it is
the role definition's, where the function reads a type. The template's check of the app's values
had made the app's name an expression that reads the secure parameters, and a what-if works out
none of those. The check is now in `app-inputs.bicep` and the name is `azurebank`; worked out
offline the same way, the template named all nine ([Checking these files](#checking-these-files)).
Run again on the changed template later that day, and answered "no" again, this what-if listed
what was expected then: nine to create, the same four `Modify` lines, nothing `Unsupported`,
nothing to delete, and no line for the check ([Measured on Azure](#measured-on-azure)).

`secrets.ps1` takes the address the alerts write to from `-AlertEmail`; without it from the
variable `AZUREBANK_ALERT_EMAIL`; without that from the address the deployed alerts already use;
and only then from the signed-in account's own mailbox. Its report names which, never the address.
Example: `-AlertEmail owner@example.invalid`.

Since 2026-10-06 it finds the account of the Azure mobile app the alerts also notify in the same
order, but for the last place: `-AlertPushAccount`; without it the variable
`AZUREBANK_ALERT_PUSH_ACCOUNT`; without that the one such account the deployed alerts already
notify. With none of the three it writes nothing, which is not an error, and its report says
`alertPushAccount: not written, the template's default applies`: the group then holds the mailbox
alone. The signed-in account is never taken for it. Steps 9 and 15 ran before the argument
existed; step 25 is where it is first passed.

#### 10. End of the first session (operator)

When the lines of the probe job are due, look for them: in the portal, the workspace
`azurebank-logs`, Logs, the query
`ContainerAppConsoleLogs | where JobName == 'azurebank-probe' | project TimeGenerated, Log`.
Microsoft's page allows a new diagnostic setting up to 90 minutes before it delivers. In the trial
the first line arrived under nine minutes after the setting was created, and a line could be read
about six and a half minutes after it was written (the median), eight at the most. If the session
ends first, this is the first step of the second one. The probe job restores and compiles, so it
says nothing about a run that lasts seconds: in the trial three such runs each kept their line,
and the first deployments show it for `migrate`: on 2026-10-03 its five executions, 24 to 37 s
each, kept their lines ([Measured on Azure](#measured-on-azure)).

Then the reads of step 1 (one firewall rule, no job, nothing attached), `Test-Path $folder`
(`False`), and:

```powershell
az logout
```

On 2026-10-03 the session's record holds one read of the workspace, at 02:40:28Z: the probe
job's five lines were there, 6 min 52 s to 8 min 51 s after they were written. The reads of
step 1 gave what they should ([Measured on Azure](#measured-on-azure)).

### Between the sessions: GitHub

#### 11. The GitHub environment (owner in the browser, **writes**; then the operator reads it back)

Settings, Environments, New environment, **`demo`** (lower case: Azure matches the name exactly).
Deployment branches and tags: Selected, `main`. Required reviewers: the owner, so that every
deployment waits for a click.

```powershell
gh api repos/Gurgant/azurebank-v2/environments/demo --jq '.name, .protection_rules[].type'
gh api repos/Gurgant/azurebank-v2/environments/demo/deployment-branch-policies --jq '.branch_policies[].name'
```

Expected: `demo`, a `required_reviewers` rule, and exactly `main`.

**This step comes before the workflow file is on `main` and before step 15.** A workflow that names
an environment that does not exist creates it with no rule at all, and step 15 is what gives the
deployment identity its two role assignments. Read the environment back again just before step 15.

#### 12. The three Azure identifiers, as secrets of `demo` (operator, **writes**)

They are identifiers, not secrets. They are stored as secrets of the environment so that the log of
a public repository never prints them and only a job in `demo` can read them. Through a pipe: no
value is displayed, written to a file or put on a command line.

```powershell
az identity show --resource-group $group --name azurebank-deploy --query clientId --output tsv |
    gh secret set AZURE_CLIENT_ID --env demo --repo Gurgant/azurebank-v2
az account show --query tenantId --output tsv | gh secret set AZURE_TENANT_ID --env demo --repo Gurgant/azurebank-v2
az account show --query id --output tsv | gh secret set AZURE_SUBSCRIPTION_ID --env demo --repo Gurgant/azurebank-v2
gh secret list --env demo --repo Gurgant/azurebank-v2
```

The workflow's first step fails if one of the three is missing or is not the shape of an
identifier (a stray space or newline). It prints one error line that names the secret, never its
value. On 2026-10-03 the three were set this way, and the last command listed the three names.

### Second session: with the workflow on `main`

#### 13. Look before writing (operator)

The reads of step 1, `Assert-EnvironmentMode` among them, and the two `gh api` reads of step 11.
On 2026-10-03 each gave what it should ([Measured on Azure](#measured-on-azure)).

#### 14. The images (operator, **writes**)

```powershell
gh workflow run deploy.yml --ref main -f action=build-push
```

If the run stops with "The registry gave no clear answer" on a package that has never been
published, run it once more with `-f first_publication=true` (see
[When something fails](#when-something-fails)). On 2026-10-03 the first run did not stop: it
published the three images with `first_publication` left alone and printed no such line.

Then, with no login at all:

```powershell
$sha = '<the full SHA of the commit that was built>'
$empty = New-Item -ItemType Directory -Path (Join-Path $env:TEMP "docker-$PID")
'api', 'bff', 'tools' | ForEach-Object { docker --config $empty manifest inspect "ghcr.io/gurgant/azurebank-${_}:$sha" > $null; "$_ exit $LASTEXITCODE" }
```

Three times `exit 0`: that is what proves the three images can be pulled with no login.

**The owner has nothing to click, unless this check answers `denied`.** Until 2026-10-03 this
step had the owner open each of the three packages and change its visibility to Public before
the check. That day the three packages were readable anonymously as soon as the workflow had
published them, before anybody had changed a visibility setting: the check gave `exit 0` three
times; with an anonymous token from the registry, a request for each of the three manifests
answered 200, and one for a package that does not exist answered 403 (the control); and each
package's page opened without a login, under the repository. The owner changed nothing. Why they
were public was not looked into: the repository is public and the packages are linked to it, and
that is all that was read.

If the check ever answers `denied` for an image the workflow has published, the owner opens that
package, Package settings, Change visibility, **Public**. That cannot be undone: a package cannot
be made private again; it can only be deleted.

This step also said: "Measured today, before any package exists: the registry answers `denied` to
an anonymous request for a package that is private or absent." For an absent package that was
measured again on 2026-10-03 (the 403 above). None of the three packages has been private, so
what the registry answers for a private one was not seen that day.

#### 15. The app (operator, **writes**)

```powershell
try {
    ./infra/secrets.ps1 -Action New -DeployApp -ImageTag $sha   # eight "generated" on the first run (seven on 2026-10-03)
    Invoke-Template 'app'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
Test-Path $folder                                               # False
```

Expected in the what-if: the eight resources of step 9. No database user is touched and no
password is set: the users of step 6 are the ones the app signs in as. On 2026-10-03, with the
template that still built the alert on the workspace, the what-if was equal line for line to step
9's second one, nine to create with four alerts, and the deployment answered `Succeeded`
([Measured on Azure](#measured-on-azure)).

Then read back (expected values), and make two things happen on purpose. On 2026-10-03 every read
gave the value expected then, which for the alerts was four rules, the fourth on the workspace.

| Claim | Read | Expected |
| --- | --- | --- |
| Zero to one replica, one revision | `az containerapp show -n azurebank -g $group --query properties.template.scale`; `--query properties.configuration.activeRevisionsMode` | 0, 1; `Single` |
| The API is not exposed | `--query properties.configuration.ingress` | external, target port 8080, `allowInsecure` false, no additional port mapping |
| Secrets by name only | `az containerapp secret list -n azurebank -g $group --query '[].name'`; `az containerapp job secret list -n azurebank-migrate -g $group --query '[].name'` | eight names; one name |
| One database identity each | `Show-Identities` | `azurebank: azurebank-app`; `azurebank-migrate: azurebank-migrate` |
| The job | `az containerapp job show -n azurebank-migrate -g $group --query properties.configuration` | `Manual`, retry limit 0, timeout 600, parallelism 1 |
| Roles on the app and the job only | `az role assignment list --assignee <principal id of azurebank-deploy> --all`; the same for the two database identities | exactly two rows, the custom role, scopes ending `/containerApps/azurebank` and `/jobs/azurebank-migrate`; no row for `azurebank-app` or `azurebank-migrate` |
| The alerts | `az monitor metrics alert list -g $group`; `az monitor action-group show -n azurebank-owner -g $group` | three rules, enabled, all on the app; one e-mail receiver |

1. **The policy must refuse.** As the owner, ask for two replicas. The request must fail with
   `RequestDisallowedByPolicy`. If it is accepted, put 1 back at once: the policy does not work.
   On 2026-10-03 it failed with that code, and `maxReplicas` read 1 afterwards.

   ```powershell
   $app = az containerapp show --name azurebank --resource-group $group --query id --output tsv
   '{"properties":{"template":{"scale":{"minReplicas":0,"maxReplicas":2}}}}' | Set-Content "$env:TEMP\scale.json"
   az rest --method patch --url "https://management.azure.com${app}?api-version=2025-01-01" --body "@$env:TEMP\scale.json"
   Remove-Item "$env:TEMP\scale.json"
   ```

2. **The receiver must verify the address, and the two messages must arrive.** When the action
   group is created, Azure Monitor writes to the address it holds: "Action required: Verify your
   email for Azure Monitor action group", with a one-time code that is valid for 30 minutes. The
   owner reads the mailbox and verifies. Until then nothing else arrives at that address: the
   message says "To receive these notifications, please verify your email address". After the
   verification a second message says "You're now in the azurebank action group". On 2026-10-03
   the first arrived at 10:02Z and the second at 10:03Z, both from
   `azure-noreply@microsoft.com`, as the owner read them in the mailbox. On this offer these two
   messages are what proves the e-mail road.

   Until 2026-10-03 this item was "An alert e-mail must arrive": one test notification, sent with
   the command below, and it said that the portal's Test button on the action group does the
   same. On this offer the command is refused: it answered
   `(Conflict) Free subscription not supported`. The portal's Test button was not tried. The
   command stays for an offer that takes it:

   ```powershell
   $to = az monitor action-group show --name azurebank-owner --resource-group $group --query 'emailReceivers[0].emailAddress' --output tsv
   az monitor action-group test-notifications create --action-group-name azurebank-owner --resource-group $group `
       --alert-type metricstaticthreshold --add-action email owner $to usecommonalertschema --output none
   ```

#### 16. The first deployment, as the owner (operator, **writes**: every table is created)

```powershell
$env:AZURE_SUBSCRIPTION_ID = az account show --query id --output tsv
$env:AZURE_RESOURCE_GROUP  = $group
$env:IMAGE_TAG             = $sha
python infra/deploy.py
```

Nobody has to watch the portal. When the migration ends, either way, the run prints its verdict:

```text
Verdict: execution <name>: Succeeded, started <time>, ended <time> (<n> s), exit code 0 (done), reason CompletionsReached.
```

`CompletionsReached` is the reason a run that ended well carried in the trial; one that was made
to fail carried `BackoffLimitExceeded`. What the migration printed is kept in the workspace. Read
it once, when the lines are due (in the trial, about six and a half minutes after they were
written, eight at the most; Microsoft's page allows up to 90 minutes after a diagnostic setting
is new):

```powershell
python infra/deploy.py --job-log
```

On 2026-10-03 this step's execution ended `Succeeded` after 34 s, with exit code 0 and the reason
`CompletionsReached`; the new revision was ready 38 s after the verdict, the one before it went
inactive, and the smoke test passed. The migration's lines were in the workspace when they were
read, 11 minutes after they were written: 16 migrations pending, each applied, and the database
at 16 of 16 after 5.3 s ([Measured on Azure](#measured-on-azure)).

`--job-log` prints the lines of one execution: the latest, or the one named. Until 2026-10-03 it
asked the workspace for the job's lines between two minutes before the execution's start and five
after its end, and that day, asked for this step's execution, it printed 25 lines: its own 20 and
the 5 of the next execution, which had started inside those five minutes. It now also asks for
the lines whose `ContainerGroupName` starts with the execution's name and a hyphen: in the table
every line of the job carried its execution's name that way, followed by a suffix. The period
stays, because it bounds the read. The query with that filter is tested offline and has not been
sent to the workspace yet ([Not measured yet](#not-measured-yet)).

#### 17. The same road as the deployment identity, twice (operator, **writes**; the owner approves each run)

```powershell
gh workflow run deploy.yml --ref main -f action=deploy
```

The second run proves that a migration with nothing to do and a new revision of the same images
both work. In the log of each run, look for "the listing was refused", the verdict line and the
line of the smoke test. Then count, in the raw log, what must not be there:

```powershell
$log = gh run view '<the run id>' --repo Gurgant/azurebank-v2 --log
$fqdn = az sql server list --resource-group $group --query '[0].fullyQualifiedDomainName' --output tsv
$site = az containerapp show --name azurebank --resource-group $group --query properties.configuration.ingress.fqdn --output tsv
$clients = 'azurebank-deploy', 'azurebank-app', 'azurebank-migrate' |
    ForEach-Object { az identity show --resource-group $group --name $_ --query clientId --output tsv }
$account = @(az account show --query tenantId --output tsv) + @(az account show --query id --output tsv)
"the server's name: {0}" -f @($log | Select-String -SimpleMatch $fqdn).Count
"the app's address: {0}" -f @($log | Select-String -SimpleMatch $site).Count
'the three client IDs: {0}' -f @($log | Select-String -SimpleMatch $clients).Count
'the tenant and the subscription: {0}' -f @($log | Select-String -SimpleMatch $account).Count
'IPv4-shaped: {0}' -f @($log | Select-String '\b\d{1,3}(\.\d{1,3}){3}\b').Count
```

Expected: 0 four times, and for the last a number whose every line is then read: a version
number has the same shape. The three identifiers GitHub holds as secrets are the deployment
identity's client ID, the tenant and the subscription; the other two client IDs are the database
identities'. A name, an ID or an address in that log is a defect, and a stop.

On 2026-10-03 both runs succeeded, each approved by the owner. Each log held "the listing was
refused", a verdict of `Succeeded` with exit code 0 after 37 s, and the smoke test's line with the
address masked as `***`; each migration had nothing to do. The five counts were 0 in each raw
log, the last one too ([Measured on Azure](#measured-on-azure)).

If Azure refuses the workflow's change of the job or of the app and names
`userAssignedIdentities/assign/action`, the run stops with one sentence and Azure's words. **No
role is added for it**: see [If Azure says no](#if-azure-says-no). On 2026-10-03 Azure asked for
no such right: in three workflow runs the deployment identity changed the job and the app, each
carrying its identity.

**Do not run `deploy.py` from a terminal while a `deploy` run of the workflow is going, or the
other way round.** The workflow's concurrency group is about the workflow's own runs; it knows
nothing of a terminal. On 2026-10-03 the two overlapped for about 50 s, not on purpose: a
`deploy.py` run from the terminal had moved the app and was waiting for its new revision to be
ready when the workflow's second run read the app, with a latest revision that was not yet the
latest ready one, and went on. Both deployed the same images and both ended well; afterwards one
revision was active, at 100 %, and both containers and the job ran those images. It was not
tried with different images. Since 2026-10-05 this page counts a run of the template as a third
writer that must not overlap either: [Deploy a commit](#deploy-a-commit), "One deployment at a
time".

#### 18. The road back, once (operator, **writes**)

So that it is not first tried on a bad day:

```powershell
python infra/deploy.py --app-only
```

On 2026-10-03: no job was touched, the new revision was ready after 39 s, and the smoke test
passed.

#### 19. The last-resort migration road, once (operator, **writes** only the temporary firewall rule)

[A migration nobody can read](#a-migration-nobody-can-read), with nothing left to migrate.

On 2026-10-03 the road did not work as it was written and works as it is written now. With the
tool's own connect timeout of 10 s the driver had no token in time and never reached the server;
with `Connect Timeout=60` in the connection string it did, and the second try, through the
temporary firewall rule, ended with exit 0 and nothing to migrate. That section has the numbers.

#### 20. What the alert on the workspace counts (operator; **writes** only to switch it off)

**Measured on 2026-10-03: that alert does not count lines here. It was deleted, and the template
now leaves it out.** What the step showed is at its end. The step stays as the way to measure
again, for whoever turns the alert back on with `@('logVolumeAlert=true')`.

The alert reads the metric `Ingestion Volume` with the aggregation `Count`. Microsoft's page
on the workspace's metrics calls it the number of records ingested into a workspace or a table,
and lists `Count` as its default aggregation
(<https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-metrics/microsoft-operationalinsights-workspaces-metrics>,
2026-07-31). It does not say whether one measurement of the metric is one record. If one stands
for several, `Count` counts measurements, and the rule may never see 50,000 in an hour however many
lines arrive. This step reads the metric and a query of the rows over the same hour: an hour in
which a migration ran, started ten minutes or more from either end of it. It must have ended more
than an hour ago.

```powershell
$start = ([datetimeoffset]'<the hour, as 2026-10-10T14:00:00Z>').UtcDateTime
$utc = { param([int]$Minutes) $start.AddMinutes($Minutes).ToString("yyyy-MM-ddTHH:mm:ss'Z'", [cultureinfo]::InvariantCulture) }
$workspace = az monitor log-analytics workspace show --resource-group $group --workspace-name azurebank-logs --query id --output tsv
$customer = az monitor log-analytics workspace show --resource-group $group --workspace-name azurebank-logs --query customerId --output tsv

# What the alert reads: the metric's Count for that hour, one line. No line at all means the
# metric has no time series for the hour.
az monitor metrics list --resource $workspace --metrics 'Ingestion Volume' --aggregation Count --interval PT1H `
    --start-time (& $utc 0) --end-time (& $utc 60) --query 'value[0].timeseries[0].data[].[timeStamp, count]' --output tsv

# What the workspace holds, by table and by the time each row was ingested: in the hour's middle
# forty minutes, in the hour, and in the hour widened by ten minutes at each end.
$between = { param([int]$From, [int]$To) "Ingested >= datetime($(& $utc $From)) and Ingested < datetime($(& $utc $To))" }
@{ query = "union withsource = TableOfRow * | extend Ingested = ingestion_time() | where $(& $between -10 70) " +
    "| summarize Middle = countif($(& $between 10 50)), Hour = countif($(& $between 0 60)), Widened = count() by TableOfRow" } |
    ConvertTo-Json | Set-Content "$env:TEMP\ingested.json"
$rows = @((az rest --method post --url "https://api.loganalytics.io/v1/workspaces/$customer/query" --resource https://api.loganalytics.io `
    --body "@$env:TEMP\ingested.json" | ConvertFrom-Json).tables[0].rows)
Remove-Item "$env:TEMP\ingested.json"
$rows | ForEach-Object { '{0}: {1}, {2}, {3}' -f $_[0], $_[1], $_[2], $_[3] }
'every table: {0}, {1}, {2}' -f @(1, 2, 3 | ForEach-Object { $column = $_; ($rows | ForEach-Object { [long]$_[$column] } | Measure-Object -Sum).Sum })
```

The table of each row goes into a column named `TableOfRow`, a name no table of the workspace
uses. Until 2026-10-03 the query named that column `SourceTable`, and as written it was refused:
`SEM0001`, "union: column named 'SourceTable' already exists". The workspace's schema has 680
tables, and one of them, `LAJobLogs`, already has a column of that name. With `TableOfRow` the
query ran.

The query is sent as `deploy.py --job-log` sends its own. Ten minutes is more than the eight the
trial's lines took to be readable. The metric and the query agree if the metric's count is at
least the first of the three totals and at most the last. If the first total is 0, the hour proves
nothing: take another hour in which a migration ran.

- **They agree:** the alert stays, and every later run of the template passes
  `@('logVolumeAlert=true')`: without it the template does not build the rule. The metric's
  count and the three totals go into [Measured on Azure](#measured-on-azure).
- **They do not:** the alert does not count lines, and it is deleted:
  `az monitor metrics alert delete --name azurebank-log-volume --resource-group $group`. Later
  runs of the template leave it out by themselves. The four numbers and the reason go into
  [Measured on Azure](#measured-on-azure). An alert that does count lines is a log search rule,
  and it is billed: $0.50 a month evaluated every 15 minutes, as this rule is, $1.50 every 5 (the
  Retail Prices API, Italy North). It is the owner's decision, and nothing here creates one.

**What it showed on 2026-10-03**, for the hour from 10:00Z to 11:00Z, read at 12:01Z:

- **The metric: no time series at all.** Not for that hour, not for any hour of that day up to
  12:00Z, with its dimension `Table Name` or without, and not for the last 40 minutes at one
  minute's grain. Its unit is Count, and Count is its only aggregation.
- **The rows:** 395 in the hour's middle forty minutes, 446 in the hour, 446 in the widened hour
  (`ContainerAppConsoleLogs` 182, 199 and 199; `ContainerAppSystemLogs` 213, 247 and 247).
- **The control:** another metric of the same workspace, `Query Count`, had a time series for the
  four hours from 09:00Z (1, 2, 2 and 2). `Ingestion Time` had none.
- **The verdict, by the rule above:** the first total is 395, not 0, so the hour counts; the
  metric counted nothing where the workspace ingested 395 to 446 rows. They do not agree.
- **What was done:** the alert `azurebank-log-volume` was deleted at 16:11Z, on the owner's word,
  and the three alerts on the app read back enabled. The owner decided against a billed log
  search rule for now. The template's default changed with it: `logVolumeAlert` is `false`.
  Why the metric reported nothing was not looked into.

#### 21. From outside, and the end (operator)

```powershell
$site = az containerapp show --name azurebank --resource-group $group --query properties.configuration.ingress.fqdn --output tsv
curl.exe --silent --head "http://$site/"                                    # a redirect to https
curl.exe --silent "https://$site/health/ready"                              # Healthy
curl.exe --silent --output NUL --write-out '%{http_code}' --request POST "https://$site/api/auth/login"   # 404
curl.exe --silent --max-time 10 "https://${site}:5068/"                     # no connection
az logout
```

On 2026-10-03: `http://` answered 301 to `https://` of the same name; `/health/ready` answered
`Healthy`; the POST answered 404; and port 5068 gave no connection within the ten seconds (curl's
exit code 28). The last reads of that day, before `az logout`, are under
[Measured on Azure](#measured-on-azure).

### Third session: turn the demo on

Written on 2026-10-05. **No step of this session has been run.** Every "expected" below is what
the code, the offline tests and the records of the first two sessions lead to expect; where it
is something of Azure's that nobody has seen, the step says so. A read that differs from what
is expected is a stop, unless the step names what is done instead.

**Before it.** `main` holds this folder as it is now and the application's screens for the claim,
and its CI is green. The owner is at the machine: every step that writes waits for the owner's
word, given for that step, as in the first two sessions. For the alerts on his phone the owner
has done three things by his own hands before step 25's clock starts, which that step lists;
without them the step is run with no account, and the alerts go by e-mail alone, as they do now.

**The order, and why.** The policy first, in a run of its own (step 23): how soon a changed
definition is enforced has not been measured. Then a deployment with the demo still off
(step 24), so that the app runs images that read the flag. The images last recorded on the app,
those of `8552f935`, do not: `git grep -n DemoOptions 8552f935 -- backend/src/AzureBank.Api
backend/src/AzureBank.Bff` prints nothing, so a flag set on them would be read by nobody. Then
the demo on (step 25), the first fill by hand (26), a browser (27), a deployment with the demo on
(28), two refusals made due (29), the address the app sees, and the networks of the ingress
if it sees the ingress (30), a run that ends with a signal
(31), the stop and the start (32), and the end (33). Each step is meant to leave a state that
fails closed. Step 25's run is the only one of the session that carries the app, so it is also
the one that carries the phone's account to the action group; the one test of that road waits
until `--check` has passed.

**Not near a run of the pool job.** By its schedule the job is expected to start at minute 0 of
the hours 0, 4, 8, 12, 16 and 20, UTC. Step 25 is started only when the next of those is more
than 60 minutes away and the last one more than ten minutes past, and its first note is that next
time. The margin is for the step and for what is done when it does not end well: a run of the
template, the wait for the revision, `--check` with its waits and, if one of them fails, the
deletion or the stop that has to come before the job's first run. A run of the job beside an app
that is not proved to be the demo is the one moment a registration could commit beside the pool.

**What is kept of a step.** From a terminal the smoke test prints the app's address (the mask is
for GitHub Actions), so notes that hold a step's output stay private. What `--pool-log` and
`--app-log` print is kept in no file and pasted nowhere: what is written down is the count of
lines and the one thing the step reads from them.

The two variables of step 16 are set for every `deploy.py` command below:

```powershell
$env:AZURE_SUBSCRIPTION_ID = az account show --query id --output tsv
$env:AZURE_RESOURCE_GROUP  = $group
```

Three functions more, for the reads of steps 22, 25 and 33. None has been sent to Azure
([Not measured yet](#not-measured-yet) says how far each was tried). Until 2026-10-06 there were
two: the third reads the action group's receivers.

```powershell
# One answer of a list as Azure gives it, read with the API version deploy.py asks with: whether
# it holds a list at all, how many entries, whether a link to a next page came with it, and its
# first and its last entry by $When. Names and times only.
function Show-List([string]$Path, [string]$When) {
    $scope = az group show --name $group --query id --output tsv
    $answer = az rest --method get --url "https://management.azure.com$scope/providers/Microsoft.App/${Path}?api-version=2025-01-01" |
        ConvertFrom-Json
    if (-not $answer) { throw 'No answer.' }
    $entries = @($answer.value | Where-Object { $null -ne $_ })
    'a list: {0}; entries: {1}; a link to a next page: {2}' -f ($null -ne $answer.PSObject.Properties['value']), $entries.Count, [bool]$answer.nextLink
    if ($entries.Count) {
        'first: {0}, {1}; last: {2}, {3}' -f $entries[0].name, $entries[0].properties.$When, $entries[-1].name, $entries[-1].properties.$When
    }
}

# The app's revisions, as deploy.py lists them: each name, whether it is active, and its replicas.
function Show-Revisions {
    $app = az containerapp show --name azurebank --resource-group $group --query id --output tsv
    (az rest --method get --url "https://management.azure.com${app}/revisions?api-version=2025-01-01" | ConvertFrom-Json).value |
        ForEach-Object { '{0}: active {1}, replicas {2}' -f $_.name, $_.properties.active, $_.properties.replicas }
}

# The action group's receivers, by kind: how many, and their names. Never an address. A kind the
# answer does not hold, or gives as null, is none of that kind.
function Show-Receivers {
    $owner = az monitor action-group show --name azurebank-owner --resource-group $group --output json | ConvertFrom-Json
    if (-not $owner) { throw 'No answer.' }
    foreach ($kind in 'emailReceivers', 'azureAppPushReceivers') {
        $found = @($owner.$kind | Where-Object { $null -ne $_ })
        '{0}: {1} ({2})' -f $kind, $found.Count, (@($found | ForEach-Object { $_.name }) -join ', ')
    }
}
```

#### 22. Look before writing (operator; reads, and one sign-in that is refused)

```powershell
python infra/deploy.py --check                # first: the line it leaves in the log is read last
```

Then the reads of step 1, and these:

```powershell
az containerapp show --name azurebank --resource-group $group --query 'properties.template.containers[].image' --output tsv
git merge-base --is-ancestor e5107f0f '<the tag those images carry>'; "exit $LASTEXITCODE"
az containerapp secret list --name azurebank --resource-group $group --query '[].name' --output tsv
$deploy = az identity show --resource-group $group --name azurebank-deploy --query principalId --output tsv
az role assignment list --assignee $deploy --all --output json | ConvertFrom-Json |
    ForEach-Object { '{0}: {1}' -f $_.roleDefinitionName, (($_.scope -split '/')[-2..-1] -join '/') }
az policy assignment show --name azurebank-shape --resource-group $group --output json | ConvertFrom-Json |
    ForEach-Object { $_.displayName; $_.parameters | ConvertTo-Json -Depth 4 -Compress }
az monitor metrics alert list --resource-group $group --query '[].name' --output tsv
Show-Receivers
Show-Executions azurebank-migrate
```

The role assignments and the policy assignment go through `ConvertFrom-Json` and print names
only: as they stand, both answers hold the subscription's ID. `Show-Receivers` prints counts and
names: the answer it reads holds the mailbox.

| Read | Expected |
| --- | --- |
| `--check` | On the app as the second session left it: "The app says the demo is off: the job azurebank-pool is not read and no secret is listed."; one line that the latest revision is the latest ready one and no other is active; since 2026-10-06 one line after it, "The bff container names no network of proxies"; "Smoke passed"; and at the end "nothing was moved". It wakes the replica and sends the one sign-in that is refused, so the API owes the log one line for it. Write down the minute |
| The reads of step 1 | As step 1 has them with the demo off: one firewall rule; Entra-only `true`; the cap 0.05 and `RespectQuota` (`OverQuota` means the log is dark, and the last read of this step is then "not run"); one job; two lines of identities; `WorkloadProfiles` |
| The images, and the `git` command | Two references on one tag. The last one recorded is `8552f935`, for which the command exits 1: the commit that makes the two hosts read the flag, `e5107f0f`, is not among its ancestors |
| The secret names; the role assignments; the policy assignment | Eight names. Two rows, the custom role on `containerApps/azurebank` and on `jobs/azurebank-migrate`. The name the first deployment gave the policy, ending at "manual jobs", with `allowedJobTriggers` holding `Manual` and no `scheduledJobs` |
| The alerts; the action group's receivers; the executions | Three rules; `emailReceivers: 1 (owner)` and `azureAppPushReceivers: 0 ()`, the group as the second session left it; every execution `Succeeded`. Whether the answer for a group with no receiver of the Azure mobile app holds that property at all has not been read: the function prints 0 either way |

The owner, in the portal: Cost analysis for the resource group, by meter, which is the read
[Afterwards](#afterwards) asks for and the second session did not take. Expected: the database's
own row, the three environment meters and the Dedicated one at 0, nothing on log ingestion. "No
rows" is "not run".

**Last, not sooner than eight minutes after `--check`:** the owner, in the workspace's Logs page,
over the last 30 days, one query run twice with one word changed.

```text
ContainerAppConsoleLogs
| where ContainerAppName == 'azurebank' and ContainerName == 'api'
| where Log contains 'auth/login'
| summarize count() by Answered = extract('responded ([0-9]+)', 1, Log)
```

The query is written from the columns step 16 read and from the API's line as a local stack of
this code printed it on 2026-10-05, `HTTP "POST" "/api/auth/register" responded 500 in ...ms`; it
has not been sent to the workspace.

- **With `auth/login`, the control.** Expected: at least this step's own line, answered 401.
  While the workspace still keeps them (30 days), also the lines of the smoke tests of
  2026-10-03, one or more for each of that day's six deployments.
- **With `auth/register`.** Expected: no line, or only lines answered 500. On a database that
  holds a schema and no role a registration cannot commit: it is answered 500 and leaves no row
  ([Not measured yet](#not-measured-yet) has the local measurement). A line answered 201 means a
  user exists, and the first pool run will then exit 13 (step 26).

**The second read counts only if the first showed its line.** "No line" is otherwise no proof: a
line takes minutes to arrive, a capped workspace takes none until its reset, and the shape of the
line is a reading until the control has matched it. If the control shows nothing after a second
read, the read of registrations is "not run", and the owner decides on step 25 knowing that the
count of users is unread: the first pool run's own line (`foreignUsers`) is then its first read.
Even with its control this is a supporting read: it covers 30 days, and a day the log was dark
left no line.

**Stop if** any read differs from the last record in a way nobody can explain.

#### 23. The policy: one named job may run on a schedule (operator, **writes**)

```powershell
try {
    ./infra/secrets.ps1 -Action New          # the foundation's file: no secret, and deployApp is false
    Invoke-Template 'pool-policy'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

Expected in the what-if, before "yes": `Modify` on the policy definition (its name, its
description, its rule, a second parameter) and on the policy assignment (its name, the second
parameter); `Modify` on the role definition, whose description changed on 2026-10-05 and nothing
else of it; the `Modify` lines step 3 saw at each run; nothing to create and nothing to delete.
The app, the migrate job, the action group, the alerts and the two role assignments are not in a
run with `deployApp=false`. Expected: each is not listed, or listed as `Ignore`, which is what
step 3's what-if printed for a database that exists and is not in the template. No what-if of
this folder has been run without the app on a group that holds one.

| Read back | Expected |
| --- | --- |
| The deployment's answer | `Succeeded` |
| The policy assignment, as step 22 reads it | The new name; `allowedJobTriggers` holding `Manual`; `scheduledJobs` holding `azurebank-pool` |
| The definition, found as [Removing everything](#removing-everything) finds it, its `policyRule` | The exception is in the first rule on a job |
| `az containerapp job list`, `Show-Identities`, the app's latest revision | As step 22 read them |

**If the what-if would create, change or delete the app, a job, the action group or an alert:**
answer "no" and stop. **If Azure refuses the definition:** stop, and bring the refusal to the
owner. `allowedJobTriggers` with `Schedule` in it is not the way round: it lets every job of the
group be put on a schedule, the migrate job among them, and the file does not remember it
(ADR-0064).

**The way back:** `Invoke-Template 'pool-policy-back' @('scheduledJobs=[]')` with the same file.
The policy then refuses what it refused before. No file remembers that override: a later run of
the template without it, step 25's among them, writes the exception again.

#### 24. Deploy `main`'s head, with the demo still off (operator, **writes**; the owner approves the run)

```powershell
gh workflow run deploy.yml --ref main -f action=build-push
gh workflow run deploy.yml --ref main -f action=deploy
```

| Read back | Expected |
| --- | --- |
| The summary of `build-push` | Three digests |
| The log of `deploy` | "the listing was refused"; "The app says the demo is off: the job azurebank-pool is not read and not moved."; the migration's verdict, `Succeeded` with exit code 0; "Smoke passed"; the address masked |
| The five counts of step 17, in the raw log | 0 each |
| The app's images; the `git` command of step 22 with their tag; and the same command with, in place of `e5107f0f`, the commit that merged the screens for the claim, written down before the session | The head's tag; exit 0 twice |

After it the app runs code that reads the flag, and no container carries the flag yet, which
the code reads as off: registration is as open as it was, and the page carries no tag.

**The way back:** `python infra/deploy.py --app-only` with `IMAGE_TAG` the tag step 22 read
([When something fails](#when-something-fails), "Going back by hand"). **If the run fails and
puts the app back:** stop. Step 25 is not run on the earlier images.

#### 25. Turn the demo on (operator, **writes**)

Its first note is the UTC time of the job's next run, which must be more than 60 minutes away.
Only if both `git` commands of step 24 exited 0.

**Before the step's clock, by the owner's own hands, for the alerts on his phone.** Added on
2026-10-06, and not run. This step's run is the session's one run of the template with the app,
so it is the one that can carry the account to the action group. Three things, none of which a
command of this page does:

1. The Azure mobile app is installed on the phone.
2. The owner signs in to it with his Azure account. The e-mail address he signs in with there is
   what the step passes: Microsoft's page on action groups asks, for this kind of notification,
   for the address used as the account ID when the app was set up
   (<https://learn.microsoft.com/en-us/azure/azure-monitor/alerts/action-groups>, dated
   2026-07-21, read on 2026-10-06). Whether it is the sign-in name `az login` shows is not known:
   nothing here takes one for the other.
3. The phone's settings allow the app's notifications, which Microsoft's page on the app's
   notifications asks for
   (<https://learn.microsoft.com/en-us/azure/azure-portal/mobile-app/alerts-notifications>, dated
   2026-01-21, read on 2026-10-06).

Without them the step is run as it was written before that day: `$phone` stays empty, the
script's report says `alertPushAccount: not written, the template's default applies`, the action
group is not expected among what the what-if would change, and the part "The phone" below is
left out. The variable `AZUREBANK_ALERT_PUSH_ACCOUNT` is not set in this terminal: the step names
the account by the argument, or not at all.

```powershell
$phone = ''   # between the quotes: the address the app on the owner's phone is signed in with
# First: no deployment may be queued, waiting or running (Deploy a commit, "One deployment at a time").
gh run list --workflow deploy.yml --limit 5 --json status,displayTitle,createdAt --repo Gurgant/azurebank-v2
try {
    ./infra/secrets.ps1 -Action New -DeployApp -DemoOn -AlertPushAccount $phone
    Invoke-Template 'demo-on'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
Test-Path $folder                                              # False
python infra/deploy.py --check
Show-Executions azurebank-pool                                 # nothing
Show-Executions azurebank-migrate                              # the control: its executions
Show-List jobs/azurebank-pool/executions startTime             # what a job that never ran answers
Show-Receivers                                                 # one of each kind, if an account was given
```

`-DemoOn` is passed once. From then on the script reads the switch from the app's two containers
and keeps it, as it keeps the tag and the secrets
([Changing the infrastructure later](#changing-the-infrastructure-later)). So is the phone's
account: from then on the script reads it from the action group's one receiver of the Azure
mobile app.

| Read back | Expected |
| --- | --- |
| The script's report | Seven secrets "kept from the deployed resource"; `demoClientKeySecret: generated`; `demo: true, asked for with -DemoOn`; `alertPushAccount: from -AlertPushAccount`. No value is shown, and no account |
| The what-if, before "yes" | `Modify` on the app: its secrets, and the settings of both containers. `Modify` on the action group, for one receiver of the Azure mobile app (expected as `properties.azureAppPushReceivers`). `Create` for the job `azurebank-pool` and for one role assignment. The `Modify` lines of the earlier runs. Nothing to delete. No what-if of this folder has been run on a group that already holds its action group, so what else it prints for that group is not known: a difference there that is not about a receiver is read before "yes", and a group to create or to delete is a "no" |
| The deployment's answer | `Succeeded` |
| `az containerapp secret list --name azurebank --resource-group $group --query '[].name' --output tsv` | Nine names |
| The settings of each container, by name: the block below | `bff`: 6 settings, `Demo__Enabled` `true`. `api`: 13 settings, `Demo__Enabled` `true` |
| `az containerapp job show --name azurebank-pool --resource-group $group --query properties.configuration`; the same with `--query 'properties.template.containers[0].args'` | `Schedule`, the expression `0 */4 * * *`, parallelism 1, retry limit 0, timeout 600, two secret names; `recycle` |
| `az containerapp job list --resource-group $group --query '[].name' --output tsv`; `Show-Identities`; `az identity list-resources --resource-group $group --name azurebank-app` | Two jobs; a third line, `azurebank-pool: azurebank-app`; the app and the pool job |
| The role assignments, as step 22 reads them | Three rows, the custom role on `containerApps/azurebank`, `jobs/azurebank-migrate` and `jobs/azurebank-pool` |
| `Show-Receivers` | `emailReceivers: 1 (owner)` and `azureAppPushReceivers: 1 (owner-phone)`. With no account given: 0 for the second, as step 22 read it |
| `--check` | It waits until the revision that answered before this step is inactive, and says so: "no other revision is active: what answers now is that revision". Then, since 2026-10-06, "The bff container names no network of proxies"; "The pool job's PIN pepper and connection string are the app's"; "It is the public demo: the page carries the demo's tag, and a registration with an empty body was refused as closed."; and at the end "the demo is on and the job azurebank-pool is in shape; the smoke test passed; nothing was moved." |
| The two `Show-Executions` | Nothing for the pool job; the migrate job's executions, so that the silence is the function's answer and not its failure |
| `Show-List` | `entries: 0`. Whether the answer of a job that never ran holds a list at all is what this read is for: it is recorded nowhere. `deploy.py` reads an answer with no list as no execution, so a deployment and a start by hand go on after it. If the line says `a list: False`, the `Show-Executions` above it prints one line with no name and no code where "nothing" is expected: that line is the function's print of such an answer, not an execution. The same line beside `a list: True` is a list given as `null`: a stop, since `deploy.py` ends a deployment and a `--pool-run` on such an answer in a traceback before any write (the row of that list under "Not measured yet" says what was seen). Write down which it was |

```powershell
(az containerapp show --name azurebank --resource-group $group --output json | ConvertFrom-Json).properties.template.containers |
    ForEach-Object { '{0}: {1} settings, Demo__Enabled {2}' -f $_.name, @($_.env).Count, ($_.env | Where-Object name -eq 'Demo__Enabled').value }
```

**Step 26 is not started unless `--check` passed.** "Ready" is not enough. The revision is
expected to be made by the run of the template, outside `deploy.py`, and until the one that ran
before is inactive an answer could still come from it. That one has its registration open, and
step 26 is the run that creates the roles a registration needs.

From here the app is the demo with an empty pool: a claim is answered 429 `DEMO_POOL_EMPTY` and
registration is closed. That state fails closed and may be left standing until the job's next
run, which fills the pool by itself.

**The phone, once `--check` has passed and not sooner.** Added on 2026-10-06; the block has not
been sent. One test notification of the action group, to both of its receivers at once: the
request names them itself, by the names the template gives them, with the mailbox read from the
group and the account typed at the top of the step. It is this step's one request of the kind.
Microsoft's limits allow two test notifications for one action group and five for a subscription
in five minutes
(<https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/service-limits>, dated
2025-12-17, read on 2026-10-06). The owner has the phone in his hand before it is sent.

```powershell
$to = az monitor action-group show --name azurebank-owner --resource-group $group --query 'emailReceivers[0].emailAddress' --output tsv
$asked = [DateTime]::UtcNow
$answer = az monitor action-group test-notifications create --action-group-name azurebank-owner --resource-group $group `
    --alert-type metricstaticthreshold --add-action email owner $to usecommonalertschema `
    --add-action azureapppush owner-phone $phone --output json | ConvertFrom-Json
'asked at {0:HH:mm:ss}Z; an answer: {1}; state: {2}' -f $asked, [bool]$answer, $answer.state
$answer.actionDetails | Where-Object { $null -ne $_ } | ForEach-Object {
    $sent = if ($_.sendTime) { '{0:HH:mm:ss}Z' -f ([DateTime]$_.sendTime).ToUniversalTime() } else { 'at no time given' }
    '{0} {1}: {2}, sent {3}' -f $_.mechanismType, $_.name, $_.status, $sent
}
```

| Read back | Expected |
| --- | --- |
| The answer | **Not known for this offer.** On 2026-10-03 the offer refused step 15's test notification with `(Conflict) Free subscription not supported`, and nobody has asked since. If it is refused: Azure's text, then `an answer: False` and no line for a receiver. If it is taken: `an answer: True`, a state, and one line for each receiver, `owner` and `owner-phone`, with a status and the time it was sent, in UTC. The fields are the ones Microsoft's page of the request names (`state`, `actionDetails`, and in each the mechanism's type, the name, the status and the send time; <https://learn.microsoft.com/en-us/rest/api/monitor/action-groups/create-notifications-at-action-group-resource-level>, read on 2026-10-06) and have not been seen here: `an answer: True` with no state and no line for a receiver is the answer's shape differing from that page, not a failure, and `$answer` is then read on the screen and not pasted, since its `detail` may quote a receiver |
| What must be seen | On the phone: a notification of the Azure mobile app. In the mailbox: one message, expected with the word "Test" in its subject, as Microsoft's page on action groups says of a test sent from the portal. Both, each by the owner's own eyes: the answer's status says the request was handed on, not that anything arrived |
| What is written down | The time each of the two was seen, and so its delay from "asked at": to the second for the phone, which the owner is holding, to the minute for the mailbox; which came first; and the answer's two lines. Never the account and never the mailbox |

**If the request is refused as step 15's was:** not a stop, and no second request, by the command
or by the portal's Test button, which was not tried then and is not tried here. Nothing was sent,
so nothing is due on the phone or in the mailbox. `Show-Receivers` has shown the receiver in the
group, and the phone's road stays "not measured": its first proof is then the first alert that
fires, and none has been seen firing.

**If the request is taken and the phone shows nothing within ten minutes of "asked at"** (the ten
minutes are this page's choice: no delay has been measured or read). Not a stop: the alerts go by
e-mail as they did before the step. Three reads, in this order, and which of them differed is
written down, in words:

1. The answer's line for `owner-phone`, against the line for `owner`: a status that differs is
   the first finding.
2. `Show-Receivers`: `azureAppPushReceivers: 1 (owner-phone)`.
3. On the phone, by the owner: the address the app is signed in with against the one typed at the
   top of the step, letter for letter; the phone's settings for the app's notifications; and the
   app's own list of notifications, which may hold what the phone did not show.

What Azure does with a push for an account that has no app, or whose app is signed in with
another address, is not known: nothing may say so anywhere. That is why the look at the phone is
the read, and the answer alone is not. A second test notification is the owner's decision, not
sooner than five minutes after the first, and only after one of the three reads changed
something. **If the mailbox shows nothing either:** the answer's line for `owner`, the junk
folder, and step 15's verification of the address; the e-mail road was last seen working that
day.

**If the policy refuses.** Where a run of the template meets a policy's refusal has not been
measured: the refusals seen so far were single requests (step 15, and the trial). So the outcome
is read, not assumed:

| What is seen | Then read | What it is, and what is done |
| --- | --- | --- |
| `Invoke-Template` stops with "The what-if failed." and Azure's text names the policy | The app's secret names; `--check`; the job list | Nothing was sent: the what-if comes before the deployment. Expected: eight names; `--check` passing, with "the demo is off" in its last line, which it says only of a page without the tag; one job. Wait 15 minutes, then the step again as written |
| The deployment fails with `RequestDisallowedByPolicy` | The same three, and the app's latest revision | Nine names, a new revision and one job, and `--check` ends with "The app says the demo is on, and the job azurebank-pool could not be read": the app's part was applied and the job was refused. That sentence is the read: `--check` stops at the job before it asks the address anything, so it cannot show the tag here. The app is the demo with an empty pool and no job, which fails closed. Eight names, the revision step 24 left and `--check` passing with "the demo is off": it was refused before anything was applied. Either way wait 15 minutes and run the step again as written: with nine names the script keeps the eight keys and the switch, with eight it generates the client key again. Anything else: stop |

The third refusal is a stop.

**If the what-if or the deployment fails on the action group** (Azure's text names
`Microsoft.Insights/actionGroups`, `azurebank-owner` or the receiver). A receiver of this kind
has never been sent from this folder. A what-if that fails sent nothing: the step again with
`$phone` empty, which is the step as it was. A deployment that fails there may have written the
app and the job all the same: a deployment sends its resources side by side, and one that fails
is not expected to undo the others (expected, not provoked). So `--check` is read at once, before
the time noted at the top of the step. If it passes, the app is the demo and the job is in shape:
the step is run again with `$phone` empty, so that the deployment ends `Succeeded`, and the phone
waits for a run of its own
([Changing the infrastructure later](#changing-the-infrastructure-later)). If it does not pass:
the rule below for a deployment that answered `Succeeded`, the pool job first.

**If the deployment answers `Succeeded` and `--check` does not pass, or the app's latest revision
is not its latest ready one.** "Do not go on" is not a safe stop here: the job exists, it is
scheduled, and the revision that answers may still be the earlier one, with its registration
open. At its next run the job would create the roles and the copies beside an open registration,
which is the state ADR-0063 warns of, and the one road back from it is a new database. So, before
the time noted at the top of the step, and before any diagnosis:

1. `Show-Executions azurebank-pool`, and its control, `Show-Executions azurebank-migrate`.
2. **Nothing is printed for the pool job, and no run was listed before an earlier deletion of
   it:** on the owner's word,
   `az containerapp job delete --name azurebank-pool --resource-group $group --yes`, and its
   absence is read back: one job in the list, two role assignments. No run can now fill
   anything.
3. **An execution is printed, in any state, or one was listed before the job was deleted and
   made again:** the pool may exist. On the owner's word the app is stopped
   ([Stop the app by hand](#stop-the-app-by-hand)). From here the roads are the ones
   [Turning the demo back](#turning-the-demo-back) has for a pool job that has run: never
   `demo=false`.
4. Only then the diagnosis: the app's revisions with their `active` flag (`Show-Revisions`), and
   `python infra/deploy.py --app-log 15`. Then the step again, which brings the job back, or,
   after case 2 only, the first road of [Turning the demo back](#turning-the-demo-back).

#### 26. The first fill (operator, **writes**: the pool's rows and the two roles)

```powershell
python infra/deploy.py --pool-run             # not while a deployment is under way: it does not read the migrate job
Show-Executions azurebank-pool
python infra/deploy.py --pool-log             # when the lines are due
python infra/deploy.py --check
```

| Read back | Expected |
| --- | --- |
| The command's first lines | "Starting the job azurebank-pool once.", which is printed before the start is sent, and only if one is sent; then "Pool run execution ... started." and each status the run goes through |
| The run's verdict, and the command's last line | `Succeeded`, exit code 0 (done); "The pool run ended well". Its seconds are the first measurement of a fill on this database: write them down |
| The run's summary line, read with `--pool-log` | `pool: free=50 was=0 claimed=0 claims24h=0 clientsAtCap=0 seeded=50 deleted(expired=0 hardStop=0 staleFree=0 failed=0) swept(idempotency=0 grants=0) tombstones=0 foreignUsers=0 ceiling=no result=PoolOk` |
| The seconds from the run's first line to that one | On a local server, 4.63 s for 50 copies in a first fill (ADR-0062). On Basic, 5 DTU: not measured |
| `--check` | As in step 25 |

By the run's exit code:

- **0:** go on.
- **1: no second start before the run's lines have been read.** Wait until they are due and read
  them with `--pool-log`. On this database no role exists yet, and the tool creates the roles
  first and outside every copy (`backend/tools/AzureBank.Seeder/Pool/DemoCopyBuilder.cs`), so a
  right that `azurebank_app` lacks is expected to show here as exit 1, with a `recycle failed:`
  line and SQL Server's permission error, 229, in the exception printed with it, and not as exit
  12. That is read in the code, not run. A permission error in the lines: stop, with no second
  start, and bring the line to the owner. A line that says the database was not reached in time:
  the step again, once; a second such exit 1 is the owner's decision on the job's connect
  timeout. No line at all after a second read: stop, and read the log's cap.
- **2:** the job's configuration is wrong. Stop.
- **12:** a copy could not be built although the roles were created. Stop, and bring the line.
  With 229 in it, `azurebank_app` can write the roles' table and not a copy's: what a missing
  right looks like once the roles exist, which is how it was measured locally
  (`backend/tools/AzureBank.Seeder/README.md`).
- **13:** a user outside every copy exists. Stop. Nothing is deleted, and the app stays as it is,
  which fails closed: the one road the records name is a new database, by the owner's own hands
  ([Turning the demo back](#turning-the-demo-back)).
- **14, or "exit code not reported":** stop, and bring the line. With no code reported the
  command starts nothing again: `Show-Executions azurebank-pool` or `--pool-log` with the run's
  name reads the code once Azure has it. The same when the command ends with "the read of its
  exit code failed": the run is over, the read that carries its code was refused or failed, and
  the code is read the same two ways.

**If the start itself is refused:** that a job on a schedule can also be started by hand has not
been tried here. Wait for the job's next run and read that execution. **If a run of the schedule
got there first:** the command refuses while it runs. Read that execution's verdict and line
instead, by its name.

**There is no way back from this step, and none is needed:** the copies are rows nobody can sign
in to until they are claimed. From here the switch is no longer a way back
([Turning the demo back](#turning-the-demo-back)).

#### 27. From a real browser (owner; **writes**: two copies are claimed)

A browser window in the foreground, at the app's address.

| What is done | Expected |
| --- | --- |
| The page, and the demo's button | The start page; then a private copy: the dashboard with two accounts, at 12,450.00 and 2,300.00 |
| A deposit; a transfer to one of the copy's two contacts, with the PIN 123456 | Both succeed. The transfer is the proof that the job's pepper is the API's. Where a PIN is checked alone, read what the screen says and `data.verified` in the answer's body, never the status |
| The reveal of an account number; "Start over" | A second copy, with the starting balances |
| The network panel, for the claim, `/bff/auth/me` and the transfer | Each succeeds, the claim and `/bff/auth/me` with 200; no token is in a body the page keeps |

It spends two of the 50 copies and two of the day's 150 claims, and their audit rows stay. A
deployment after this step ends the browser's session.

It is the one step that proves two things nothing else in this session does: that the API obeys
its flag, and that a PIN hashed by the job is taken by the API. Step 28 follows it, so each
answer that is not the expected one has its rule:

| What is seen | What it means | What is done |
| --- | --- | --- |
| The claim answers 429 `DEMO_POOL_EMPTY` | The pool holds no free copy although step 26 ended well | `Show-Executions azurebank-pool` and `--pool-log`: read the line. Stop |
| The claim answers 429 with the rate limiter's code | The ten a minute that sign-ins share were spent, by this session's own checks | Wait a full minute, and once more. A second one: stop |
| The claim answers 404 | The BFF has the flag, since `--check` saw the tag, and the API does not read its own | Stop. Step 28 is not run. `python infra/deploy.py --app-log 15` for the API's lines |
| The claim answers 500 or 503 | The API could not serve it | `--app-log 15`. Stop |
| The transfer is refused for its PIN, or a PIN checked alone shows `data.verified` false. The status is 200 either way | The job's pepper is not the API's, whatever `--check` printed: every copy this job built fails every PIN | Stop. Step 28 is not run. On the owner's word the app is stopped ([Stop the app by hand](#stop-the-app-by-hand)), so that no visitor meets a copy whose PIN fails. `--check` once more, and its line kept: "the app's" beside a PIN that fails is a finding of its own |
| The dashboard does not show the two accounts with those balances | The copy is not the one the tool describes | Stop; the line of step 26 and `--app-log 15` |

In each of these cases the later steps wait for the owner's decision.

#### 28. The same road as the deployment identity, with the demo on (operator, **writes**; the owner approves the run)

```powershell
gh workflow run deploy.yml --ref main -f action=deploy
python infra/deploy.py --check                # afterwards, as the owner
```

| Read back | Expected |
| --- | --- |
| The run's log | "Running now" with four image references; no refusal for a pool run in progress; "Moving azurebank-migrate", the verdict; "Moving azurebank-pool"; the new revision; "Smoke passed", naming the demo's tag and the closed registration |
| The five counts of step 17, in the raw log | 0 each |
| `az containerapp job show --name azurebank-pool --resource-group $group`: its container's image and its `properties.configuration` | The head's tag. The configuration as step 25 read it: a change of the image changed nothing else |
| `--check` | As in step 25. With no copy spent, it is what shows that the deployment identity's change left the job's pepper where it was |

It is the first time the deployment identity reads and changes the pool job. **If Azure asks for
a right on the identity the job carries,** the run stops with its own sentence and no role is
added ([If Azure says no](#if-azure-says-no)).

**The way back:** the put-back is the script's own; by hand, `--app-only` with `IMAGE_TAG` the
tag step 24 deployed, never the one step 22 read: those images do not read the flag
([When something fails](#when-something-fails), "Going back by hand").

#### 29. The policy must still refuse: another name on a schedule, and two runs at once (operator; two **writes** that must change nothing)

**First.** As the owner, one request for a job named `azurebank-pool-2` in the environment, its
body in a file: the trigger `Schedule`, an expression that fires once a year, and one small
container of the image step 7's probe used, with no identity, no `command` and no `env`: no
program, no connection string and no database identity is in it. The name begins with the
excepted one on purpose: the same refusal then also shows that the exception is by the whole
name and not by its first letters. Every other rule of the policy is kept by this body, a
quarter of a vCPU, one replica at a time and no init container, so a refusal by the policy can
only be for the trigger. The block has not been sent.

```powershell
$scope = az group show --name $group --query id --output tsv
$body = Join-Path $env:TEMP 'pool-2.json'
try {
    @{
        location = 'italynorth'
        properties = @{
            environmentId = "$scope/providers/Microsoft.App/managedEnvironments/azurebank-env"
            workloadProfileName = 'Consumption'
            configuration = @{ triggerType = 'Schedule'; replicaTimeout = 60; replicaRetryLimit = 0
                               scheduleTriggerConfig = @{ cronExpression = '0 0 1 1 *'; parallelism = 1; replicaCompletionCount = 1 } }
            template = @{ containers = @(@{ name = 'probe'; image = 'mcr.microsoft.com/dotnet/sdk:10.0'
                                            resources = @{ cpu = 0.25; memory = '0.5Gi' } }) }
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $body
    az rest --method put --body "@$body" `
        --url "https://management.azure.com$scope/providers/Microsoft.App/jobs/azurebank-pool-2?api-version=2025-01-01"
} finally {
    Remove-Item -LiteralPath $body -ErrorAction Ignore
}
az containerapp job list --resource-group $group --query '[].name' --output tsv
```

| Read back | Expected |
| --- | --- |
| The answer | A failure with `RequestDisallowedByPolicy`. Any other failure, a 400 for the body among them, says nothing of the policy: stop, and bring it |
| The job list | Two names, as before |

**If it is accepted:** on the owner's word, given at once, that job is deleted
(`az containerapp job delete --name azurebank-pool-2 --resource-group $group --yes`; it is this
step's own and has not run), its absence is read back, and the session stops: the exception is
wider than a name. The policy goes back by step 23's way back until a change has put it right.
With the pool job in place, that is expected to refuse every later write of it: a deployment
with the demo on would stop when it moves the pool job, after its migration, and only
`--app-only` would still move the app. So it is taken on the owner's word, and the change that
puts the rule right comes before the next deployment. A run of the template without that
override writes the exception again: no file remembers `scheduledJobs=[]`. Expected, not seen.

**Second.** Not within ten minutes of a run of the job. As the owner, one PATCH of the pool job
that asks for two runs at once, as step 15 asks for two replicas. The function reads the job and
sends back its location and, of its configuration, the trigger, the timeout, the retry limit,
the expression and the completion count as it read them, with `parallelism` as asked. Nothing
else is in the body: no container, no secret, no identity. It has not been sent.

```powershell
# Asks the pool job for $Runs replicas of one execution, and for nothing else.
function Set-PoolParallelism([int]$Runs) {
    $job = az containerapp job show --name azurebank-pool --resource-group $group --output json | ConvertFrom-Json
    $now = $job.properties.configuration
    $body = Join-Path $env:TEMP 'pool-parallelism.json'
    try {
        @{ location = $job.location
           properties = @{ configuration = @{
               triggerType = $now.triggerType; replicaTimeout = $now.replicaTimeout; replicaRetryLimit = $now.replicaRetryLimit
               scheduleTriggerConfig = @{ cronExpression = $now.scheduleTriggerConfig.cronExpression; parallelism = $Runs
                                          replicaCompletionCount = $now.scheduleTriggerConfig.replicaCompletionCount } } } } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $body
        az rest --method patch --body "@$body" --url "https://management.azure.com$($job.id)?api-version=2025-01-01"
    } finally {
        Remove-Item -LiteralPath $body -ErrorAction Ignore
    }
}
Set-PoolParallelism 2
az containerapp job show --name azurebank-pool --resource-group $group --query properties.configuration
python infra/deploy.py --check
```

| Read back | Expected |
| --- | --- |
| The answer | A failure with `RequestDisallowedByPolicy`. It would be the first time the rule on a schedule's parallelism is seen at work ([Not measured yet](#not-measured-yet)). Any other failure says nothing of the policy: stop, and bring it |
| The job's `properties.configuration` | As step 25 read it, whole |
| `--check` | The job in shape, and its two secrets the app's |

**If it is accepted:** `Set-PoolParallelism 1` at once, read the configuration back
whole against step 25's, and stop. The one rule that bounds a scheduled job does not hold, and
until it does, two runs at once are bounded by `deploy.py`'s shape check alone, at a deployment
and at a `--check`.

**What this step does not make due.** Nothing on Azure refuses a changed expression: the policy
has no rule for one, and the deployment identity may write the whole job. A schedule changed to
every minute is caught by `deploy.py`'s shape check, at the next deployment or `--check`, and by
nothing sooner. Nor does any rule refuse a second execution beside one that is running: the
policy's rule is on `parallelism`, which is the replicas of one execution. A start by hand, or a
schedule or a timeout changed so that two executions overlap, is looked for by `deploy.py`
alone: its one read before a deployment or a `--pool-run`, and its shape check.

#### 30. The address the app sees (operator; **writes** nothing in the database; spends the shared sign-in limit for a minute)

Network A is this machine's own connection. Network B is this machine through a phone's mobile
data. The request is the smoke test's own sign-in, for an address nobody has, so nothing is
written. Its body is taken from the script itself, into a file. No request of the block has
been sent.

```powershell
$site = az containerapp show --name azurebank --resource-group $group --query properties.configuration.ingress.fqdn --output tsv
python -B -c "import json, sys; sys.path.insert(0, 'infra'); import deploy; print(json.dumps(deploy.SMOKE_LOGIN))" |
    Set-Content "$env:TEMP\sign-in.json"
$signIn = { curl.exe --silent --output NUL --write-out '%{http_code}' --request POST --header 'Content-Type: application/json' `
                --data-binary "@$env:TEMP\sign-in.json" "https://$site/bff/auth/login" }
1..12 | ForEach-Object { & $signIn }         # from network A: twelve statuses
& $signIn                                    # at once, from network B: one
Remove-Item "$env:TEMP\sign-in.json"
```

| Measurement | How | What is read |
| --- | --- | --- |
| The first | From A: twelve sign-ins inside a minute, each status printed. Then, at once, from B: one | From A: how many answer 401 before the first 429. From B: 401 or 429 |
| The second | `python infra/deploy.py --app-log 15`, not sooner than eight minutes after the twelve sign-ins and inside the fifteen it reads: a line takes minutes to arrive ([Reading the logs](#reading-the-logs)). It is the command's first run against the workspace, and its output is kept in no file | The rate limiter's warning names the client it rejected. It is compared with A's own public address, and what is written down is "A's own address" or "another, the same for A and B", and the count of warnings: never the value. If the log's cap was reached that day, the read is "not run" |
| The third | The first two again once the app has been at zero replicas, and once more after step 32's start or after a later deployment, whichever comes first: write down which, and whether the revision's name changed. Step 28's revision is the one the first measurement already ran on | The same client, or another |

Twelve sign-ins are enough for the two of the BFF's three limits that count by the caller's
address: one function of it feeds the ten a minute and the 300 a minute alike
([What it costs, and what bounds it](#what-it-costs-and-what-bounds-it)).

What follows from each answer:

| What is read | The cap of 1,000 claims a day for one client | Reading the client's address through the ingress | The public link |
| --- | --- | --- | --- |
| B is answered 429, and the warning names one client that is not A's own address | Stays | Needed: the networks of the ingress, below, and this is their measurement. Until 2026-10-06 this cell said "A change of the BFF's code is needed": the BFF could be told a proxy by its exact address only | Not published before the networks are set and their proof, below, has answered 401 for B |
| A needs more than eleven requests to meet a 429, or B's answer changes between repeats | Stays | Needed; a network that holds every address read, not one address | The same |
| B is answered 401 every time, and the warning names A's own public address | Goes back to its default of 10: a change takes the setting out of the `api` container and of the job, and one run of the template applies it | Not needed for the link | Its other preconditions only |
| The third measurement names another client than the second | As the first row | Needed; a network that holds both, so that it survives a new replica and a new revision | The same |

Whatever is read replaces "not provoked" in the row of [Not measured yet](#not-measured-yet)
about the shared sign-in limit: in words, never the address.

**The networks of the ingress: telling the app which proxy to believe.** Operator; **writes**:
one run of the template, which makes a new revision. Added on 2026-10-06. **None of it has been
run on Azure:** every "expected" below is what the code and its offline tests lead to expect, and
no request of its blocks has been sent. It is run only when the table above sent its reader
here: the app counts every visitor as one client that is not A.

Since that day the BFF can be told a proxy by the network it connects from
(`ForwardedHeaders:KnownIPNetworks`, ADR-0013): on a connection that comes from inside a listed
network, and on no other, it takes the last entry of `X-Forwarded-For` for the caller's address.
The template's parameter `proxyNetworks` writes that list on the `bff` container, one setting a
network (`ForwardedHeaders__KnownIPNetworks__0` and on). It is empty by default, and no network
of this deployment is written in any file of this repository: they are typed in this session, by
whoever read them, and kept in the session's private notes.

**1. Read the ingress's address.** The second measurement already printed it. Each warning of
the rate limiter ends with the client it rejected, which the line calls its partition
(`backend/src/AzureBank.Bff/Program.cs`, the limiter's rejection): with no proxy listed that is
the address the connection came from, the ingress's own. Expected and not seen on Azure: an IPv4
address in full, also when the socket reports it in its IPv4-mapped form, or an IPv6 address as
its /64. It is read on the screen, at the second measurement and again at the third, and it goes
into the private notes and nowhere else.

**2. Choose the networks.** Every address inside a listed network can name a caller's address,
so a network must hold what the ingress can connect from and nothing that a visitor, or somebody
else's workload, can connect from. Which range that is, this page cannot say: nothing in this
repository knows the platform's addresses, and no page of Microsoft's on the networking of a
Container Apps environment has been read for it. So, in this order:

1. Microsoft's page on the environment's networking is read in that session. If it names the
   range the environment's own infrastructure connects from, and every address read in step 1 is
   inside it, that range is the network. The page and its date go into the notes.
2. If no such range is found and the addresses read are in a private or a shared block
   (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10, or fd00::/8), which no visitor
   from the internet connects from: the narrowest network that holds every address read at the
   second and at the third measurement, and not narrower than a /24 around one address. Too
   narrow fails closed and in silence: the day the ingress moves outside it the header is read
   no more, and every visitor is one client again, as before this step. Too wide lets more of
   the platform's own addresses name a caller. That is this page's judgement, not a measurement.
3. If an address read is a public one: stop. Nothing is listed until Microsoft's own published
   range for it has been read.

The app refuses a network at startup, and `secrets.ps1` refuses the same ones before it writes
anything (`backend/src/AzureBank.Bff/Options/ProxyOptionsValidator.cs`; a test holds the
script's rule to the app's): a text that is not `address/prefix-length`, a prefix length out of
range, anything wider than a /8 and so `/0`, an IPv4-mapped IPv6 network, and a text that .NET
reads as another network than it shows, such as an address inside the network (`10.0.0.1/8`) or
an octet with a leading zero. One address is written with `/32`, or `/128`.

**3. Set them** (**writes**; on the owner's word). One run of the template with the app. It is
expected to make a new revision, and a new revision ends every session held in the replica's
memory: whoever is signed in to the demo is signed out.

```powershell
$networks = ''   # between the quotes: the networks, in CIDR form, separated by commas. Typed here, written in no file
# First: no deployment may be queued, waiting or running (Deploy a commit, "One deployment at a time").
gh run list --workflow deploy.yml --limit 5 --json status,displayTitle,createdAt --repo Gurgant/azurebank-v2
try {
    ./infra/secrets.ps1 -Action New -DeployApp -ProxyNetworks $networks
    Invoke-Template 'proxy-networks'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
Test-Path $folder                                              # False
python infra/deploy.py --check
(az containerapp show --name azurebank --resource-group $group --output json | ConvertFrom-Json).properties.template.containers |
    ForEach-Object { '{0}: {1} settings, {2} of them a network of proxies' -f $_.name, @($_.env).Count,
        @($_.env | Where-Object name -like 'ForwardedHeaders__KnownIPNetworks__*').Count }
```

The networks are passed once. From then on the script reads them from the `bff` container and
keeps them, as it keeps the demo's switch
([Changing the infrastructure later](#changing-the-infrastructure-later)).

| Read back | Expected |
| --- | --- |
| The script's report | `proxyNetworks: from -ProxyNetworks`, beside the lines of step 25's later runs: eight secrets and the demo "kept from the deployed resource". No network is shown. With `$networks` left empty the line is `proxyNetworks: not written, the template's default applies`, and the run changes nothing of this step: stop, and type them |
| The what-if, before "yes" | `Modify` on the app, for the settings of its `bff` container, and the `Modify` lines the earlier runs of this session showed. Nothing to create and nothing to delete: either is a "no". Expected, and no what-if of this change has been run |
| The deployment's answer | `Succeeded` |
| `--check` | It waits until the revision that answered before is inactive. Then one line more than at step 25, after the line about the revision: "The bff container names 1 network of proxies" (or the number typed), ending "No network was shown."; the lines of step 25; and at the end "nothing was moved". Until this run that line read "The bff container names no network of proxies" |
| The settings of each container, by count | `bff`: 7 settings with one network, one more for each further network, and as many "of them a network of proxies" as were typed. `api`: 13 settings, 0 of them a network. The block prints counts, never a value |

**If `--check` does not pass because the latest revision is not the latest ready one.** The app
refuses to start on a network it does not take, and a slip of the script's own rule would show
here and nowhere sooner. Expected and not provoked: the revision that answered before goes on
answering, since the new one never got ready. `python infra/deploy.py --app-log 15`, on the
screen: the BFF's refusal starts "ForwardedHeaders:KnownIPNetworks contains" and names the entry.
Then this step again with the networks put right, or the way back, below.

**4. The proof, from the two networks of the first measurement** (reads; it spends A's own
sign-in limit, twice). Not sooner than two minutes after `--check` passed, so that no earlier
sign-in is still counted. No request of the block has been sent.

```powershell
$site = az containerapp show --name azurebank --resource-group $group --query properties.configuration.ingress.fqdn --output tsv
python -B -c "import json, sys; sys.path.insert(0, 'infra'); import deploy; print(json.dumps(deploy.SMOKE_LOGIN))" |
    Set-Content "$env:TEMP\sign-in.json"
$signIn = { param([string]$Named)
    $headers = @('--header', 'Content-Type: application/json')
    if ($Named) { $headers += '--header', "X-Forwarded-For: $Named" }
    curl.exe --silent --output NUL --write-out '%{http_code}' --request POST @headers `
        --data-binary "@$env:TEMP\sign-in.json" "https://$site/bff/auth/login" }
1..12 | ForEach-Object { & $signIn }                         # from network A: twelve statuses
& $signIn                                                    # at once, from network B: one
Start-Sleep -Seconds 90                                      # A's twelve leave the limiter's window
1..12 | ForEach-Object { & $signIn "203.0.113.$_" }          # from A: each names another address as its own
Remove-Item "$env:TEMP\sign-in.json"
```

| Measurement | What is read | Expected |
| --- | --- | --- |
| Twelve sign-ins from A, inside a minute | The twelve statuses | Ten times 401, then 429 at the eleventh and at the twelfth: the ten a minute are A's own |
| One from B, at once | Its status | 401. **This is the proof.** Before the networks were set, B was answered 429 with A: one limit for everybody |
| Twelve more from A, each with an `X-Forwarded-For` header of its own that names another address | The twelve statuses | Ten times 401, then 429 twice, as the first twelve. The ingress is expected to append A's own address after whatever A wrote, and the app believes the last entry only. **Twelve times 401 is a stop:** the app believes what a caller writes, each lie is a client of its own, and the limit stops nobody. The way back, at once |
| `python infra/deploy.py --app-log 15`, not sooner than eight minutes after the last sign-in | The partition of the limiter's warnings, compared with A's own public address | "A's own address" (for an IPv6 address, its /64) on every warning of both runs, and never one of the addresses the third run named. What is written down is that sentence and the count of warnings: never the value |

What each other answer means:

| What is read | What it is | What is done |
| --- | --- | --- |
| B is answered 429, and the warnings name the client they named before this step | The header was not read: the connection does not come from inside a listed network. The ingress moved, or the address was misread | Nothing is worse than before the step. Step 1 again, on these warnings; then this step with the networks put right |
| B is answered 429, and the warnings name one client that is neither A's address nor the one of before | The last entry of the header is not the visitor: another proxy of the platform's stands between. The app believes one hop (`ForwardedHeaders:ForwardLimit` is 1, and this template sets no other) | Stop. It is the measurement for the next change: two hops. Nothing is worse than before; the way back, or leave it and note it |
| The third run is answered 401 twelve times | The app believes an entry a caller wrote | The way back, at once, and before the link is published |

**The same pattern, seen on a local process and not on Azure** (2026-10-06; the BFF with this
change in it, started by hand on this machine with no API behind it, so a sign-in that is let
through is answered 503 there and not 401). With the network that holds the connection's own address
listed: twelve sign-ins whose header ended in one address, each after another entry of its own,
were answered ten times and refused twice, and the limiter's two warnings named that last
address; one whose header ended in another address was answered. With a network listed that
does not hold the connection's address: the same twelve, ten answered and two refused, the
thirteenth refused with them, and the three warnings named the connection's own address. That
is the code on a real socket. It says nothing of what the ingress writes into the header.

**The way back** (**writes**; on the owner's word). The same run with the word `none`, which
writes an empty list whatever the app holds: the run takes the settings out, and the app takes
each connection's address for the caller's again, as before this step.

```powershell
try {
    ./infra/secrets.ps1 -Action New -DeployApp -ProxyNetworks none
    Invoke-Template 'proxy-networks-off'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
python infra/deploy.py --check                                # "The bff container names no network of proxies"
```

Expected: the report says `proxyNetworks: none, asked for with -ProxyNetworks`; the what-if shows
`Modify` on the app; and `--check` passes with the line above. It makes one more revision, and
ends every session once more.

#### 31. A run that ends with a signal, and the metrics (operator; **writes** one top-up)

The run must find at least one free copy and fewer than 50. So it is started after a claim and
before the job's next run tops the pool up again: after step 27 the pool is expected to hold 48
free copies until then, and if a run came in between, one more claim from the browser comes
first. As the owner, one start of the pool job whose execution carries the job's own container
with one more setting, `Demo__Pool__LowMark=50`: the low mark may be as high as the target
(ADR-0062). The body travels in a file. No secret is in it: the two secrets stay references,
and a read of a job holds no secret's value. Not while a run is listed as running: this start
does not look, as `--pool-run` does, so `Show-Executions azurebank-pool` comes first. The block
has not been sent; it prints the name of the execution it started.

```powershell
$job = az containerapp job show --name azurebank-pool --resource-group $group --output json | ConvertFrom-Json
$container = $job.properties.template.containers[0]
$body = Join-Path $env:TEMP 'pool-start.json'
try {
    @{ containers = @(@{
        name = $container.name; image = $container.image; args = @($container.args)
        resources = @{ cpu = $container.resources.cpu; memory = $container.resources.memory }
        env = @($container.env) + @(@{ name = 'Demo__Pool__LowMark'; value = '50' }) }) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $body
    az rest --method post --body "@$body" --query name --output tsv `
        --url "https://management.azure.com$($job.id)/start?api-version=2025-01-01"
} finally {
    Remove-Item -LiteralPath $body -ErrorAction Ignore
}
```

| Read back | Expected |
| --- | --- |
| `Show-Executions azurebank-pool` | That execution, with exit code `[10]`. Its status is what this step is for: how Azure words an execution whose container exits with a code that is not 0 and is not a failure. `Failed` is expected, since an execution that exited 7 read `Failed` in the trial |
| `python infra/deploy.py --pool-log '<that execution>'` | "exit code 10 (done, with a signal: the pool was low (PoolLow))"; the summary line with `was` below 50, `seeded` the difference and `result=PoolLow` |
| `python infra/deploy.py --check` | The job in shape, and its two secrets the app's. Expected and not measured: a start whose body was written by hand changes that execution and not the job |
| Reads only: the block below, which prints the metrics of the pool job and of the database by name, unit and dimensions; then, by the names it printed, one `az monitor metrics list` of the job's metric of executions over the last hour, and of the database's size, as step 20 reads a metric | The names and the dimensions, and whether a time series exists at all. An alert on a failed pool run, or on the database's size, is built on what these show and not before ([What is not here](#what-is-not-here)) |

```powershell
# Names, units and dimensions only: the definitions themselves hold the resource's ID. Not sent yet.
$pool = az containerapp job show --name azurebank-pool --resource-group $group --query id --output tsv
$database = az sql db show --resource-group $group --server $server --name AzureBank --query id --output tsv
foreach ($resource in $pool, $database) {
    az monitor metrics list-definitions --resource $resource --output json | ConvertFrom-Json |
        ForEach-Object { '{0} ({1}): {2}' -f $_.name.value, $_.unit, (@($_.dimensions.value) -join ', ') }
}
```

**If Azure refuses a start that carries a setting:** the signal is recorded as not provoked, and
the metrics are read all the same.

#### 32. Stop the app and start it, once (owner's sign-in; **writes**)

The two calls of [Stop the app by hand](#stop-the-app-by-hand). It is the way back that takes
minutes, and until this step no record shows it run.

| Read back | Expected |
| --- | --- |
| After the stop: the app's properties whose names end in `State` or `Status`, read through `ConvertFrom-Json`; the latest revision's `runningState`; its replicas; what the address answers | One of them says the app is stopped, and there is no replica. **Which property says it, and with which word, is in no file of this repository.** The step writes down the name and the value it read, and from then on that read is "the app's state" wherever this document asks for it. What the address answers then is not measured: expected, something that is not the page |
| After the start: `--check` | As in step 25 |

The deployment identity can neither stop nor start the app: only the owner's sign-in can.

#### 33. The end (operator; reads)

The reads of step 22 once more, with what they are expected to give now: two jobs, three lines of
identities, nine secret names, three role assignments, three alerts, the policy under its new
name with `scheduledJobs`, and the action group with one e-mail receiver and one receiver of the
Azure mobile app (`azureAppPushReceivers: 1 (owner-phone)`; 0 if step 25 ran with no account).
Then the day's log volume by table ([Afterwards](#afterwards)),
`Show-Executions` for both jobs, and `Test-Path $folder`, `False`.

**The lists, as Azure gives them.** `deploy.py` reads one answer of a job's executions and of
the app's revisions, and follows no link to a next page.

```powershell
Show-List jobs/azurebank-pool/executions startTime
Show-List jobs/azurebank-migrate/executions startTime
Show-List containerApps/azurebank/revisions createdTime
Show-Revisions                               # and once more after five minutes with no request
```

| Read back | Expected |
| --- | --- |
| The three `Show-List` | For each list: whether a link to a next page came with the answer, and whether its first entry is the oldest or the newest. Not measured, and these reads settle it for lists of this length: with a link, a run or a revision can be on a page the script does not read ([Not measured yet](#not-measured-yet)) |
| `Show-Revisions`, twice | Which revisions the list holds, and what `active` says for the latest one once it has 0 replicas |

**A run that nobody started.** A job on a schedule has never been seen accepted here, and never
seen starting by itself. The session started the pool job twice by hand, at steps 26 and 31, and
knows those two executions by name. This step is not closed before one run of the schedule after
step 25 has been read:

| Read back | Expected |
| --- | --- |
| `Show-Executions azurebank-pool` | An execution whose name is neither step 26's nor step 31's, started at minute 0 of an hour that is a multiple of four, UTC, with the status `Succeeded` and the exit code 0. A 10 is not expected: it means the run found fewer than 20 free copies (`Demo:Pool:LowMark`, 20 by default, and the job sets none), which is more claims than this session made. Stop, and read its line: `was`, `claims24h`, `clientsAtCap` |
| `python infra/deploy.py --pool-log '<that execution>'` | Its summary line, when it is due |
| The subscription's activity log for the resource group over the last eight hours, by the block below: for each event whose operation is `Microsoft.App/jobs/start/action`, its time, the job's name, its status, and one word for its caller ("the owner", "another", "none"). Never the caller itself, and no resource ID | Events for each start by hand, as "the owner": a request is expected to leave one for each status it went through. The workflow's start of the migration at step 28, as "another". For the run of the schedule: events, or none. Not measured, and this read settles it: it is what an alert on job starts could or could not see |

```powershell
# Not sent yet.
$me = az account show --query user.name --output tsv
az monitor activity-log list --resource-group $group --offset 8h --max-events 1000 --output json | ConvertFrom-Json |
    Where-Object { $_.operationName.value -eq 'Microsoft.App/jobs/start/action' } | Sort-Object eventTimestamp |
    ForEach-Object {
        $who = if (-not $_.caller) { 'none' } elseif ($_.caller -eq $me) { 'the owner' } else { 'another' }
        '{0}  {1}  {2}  {3}' -f $_.eventTimestamp, ($_.resourceId -split '/')[-1], $_.status.value, $who
    }
```

**If the session ends before a run of the schedule has passed,** "a scheduled run, and its start
event" stay under [Not measured yet](#not-measured-yet), with the three reads of the table above
as what settles them, and they are taken at the owner's next sign-in. A schedule that never
fires changes nothing a visitor sees until the free copies are gone, and no alert is built on
the job: without this read nobody is due to notice.

Then `az logout`, and what the session measured replaces every "expected" of steps 22 to 33.

### Afterwards

- **48 hours after step 2, and again after the second session: the cost, by meter** (the portal's
  Cost analysis, grouped by meter). The check passes only if the same answer shows the database's
  own row: "no rows" is "not run", not "0". The three environment meters and the Dedicated one must
  read 0. **Any cost on the log ingestion meter switches the logs off.** Whether the free 5 GB
  apply is settled the first time a cost row covers 35 MB or more of ingestion; until then the
  worst case stays in the numbers above. On the trial's own day the cost view had no row at all
  for its resource group, whose database existed for a little over two hours.
- **After the second session, in the workspace's Logs page:** the billed size of a line of the app
  (`ContainerAppConsoleLogs | summarize avg(_BilledSize)`); the day's billable volume
  (`Usage | where IsBillable | summarize sum(Quantity) by bin(TimeGenerated, 1d)`, in MB; the
  `Usage` table is not billed); and the count, as a number only, of console lines that contain `@`.
  The "about 65,100" above is then worked again from the app's own billed size, and so is the
  threshold of the alert on the workspace if that alert is ever turned back on. The trial read
  `_BilledSize` and the `Usage` table for its jobs' lines; neither of these two queries has been
  run as it is written here. At the end of the second session, on 2026-10-03, the day's volume
  read 0.19 MB by table ([Measured on Azure](#measured-on-azure)); the billed size of a line of
  the app and the count of lines with an `@` were not read.
- **48 hours after step 26: the cost by meter once more.** The pool job's seconds are expected
  on the two meters of vCPU and memory while active, inside the free amounts, so those two rows
  are expected to read 0 as before
  ([What it costs, and what bounds it](#what-it-costs-and-what-bounds-it)). A cost on either,
  or a meter that is a job's alone, is a finding: the numbers of that section are then worked
  again. "No rows" is "not run", as above.

## If Azure says no

Each row is decided now, so that nothing is decided on the day. "Stop" means: change nothing more
and bring the refusal's text to the owner. What Azure accepted when requests of the same shapes
were sent by hand is under [Measured on Azure](#measured-on-azure): a refusal of something that
was accepted there is a difference to understand, not a step to work around.

A refusal or an answer that no row below names is a stop too (ADR-0061, decision 11). A stop
removes nothing: whatever exists keeps its charge until the owner decides (from step 2 on, the
database's $0.161 a day), and the road to remove it all is
[Removing everything](#removing-everything), whose commands have not been run.

**The foundation and its second run (steps 2 and 3)**

| If | Then |
| --- | --- |
| The environment is refused with `ExpressEnvironmentFeatureNotSupported`, or `Assert-EnvironmentMode` throws, here or at the start of a later session: a mode other than `WorkloadProfiles`, or no mode with the logs not on `azure-monitor` | Stop. The template names the mode because a request that names none was refused that way; if it is refused all the same, that line was lost or is not honoured |
| The deployment is refused on the **policy definition** | Run it again with the policy off (`Invoke-Template 'foundation' @('denyPolicy=false')`), pass the same override on every later run, and write down that the shape then rests on `deploy.py`'s own check alone. The trial's definition was an earlier one, without four of this rule's conditions; the rule with those four, as it was until 2026-10-05, was accepted at step 2 on 2026-10-03 ([Measured on Azure](#measured-on-azure)). Since 2026-10-05 the rule also holds an exception by name, which has not been sent to Azure: a refusal of the definition for that exception is a stop, not a run with the policy off (the last table of this section; ADR-0064, decision 4). Until 2026-10-05 this row said that this rule, all of them in it, was accepted |
| It is refused on the **custom role** or on the **SQL server**; or the **second run** is refused, or its what-if shows the administrator changed or removed | Stop. There is one shape of the server in this folder and no other is written down: sent by hand it was accepted, twice, and so it was from the template at steps 2 and 3 on 2026-10-03. And there is no fallback that keeps the deployment identity away from the secrets |
| It is refused on the **workspace**, the destination, the diagnostic setting or the cap | `./infra/secrets.ps1 -Action New -LogsOff`, run again, and say so: nothing is kept then. If a workspace or a setting was created on the way, [Switching the logs off](#switching-the-logs-off) |
| A read in step 4 differs | A defect in the template: fix it before going on |
| Anything else is refused in either run: the lock, the federated credential, the database, a firewall rule, the policy assignment, the API version `2026-07-01` itself, or with an error no row here names | Stop |

`az sql server ad-only-auth disable` is never run, whatever is refused.

**An override is not remembered.** The parameter file keeps what the environment does with its
logs, read from Azure. It does not keep `denyPolicy=false`: a policy that is absent cannot be
told from one a run that stopped halfway never got to create, and a guard must not be switched
off by that. If the policy was left out because Azure refused it, pass the same override on every
later run of the template; without it the run asks Azure again for what it left out. The what-if
shows it first: a policy assignment to create.

Until 2026-10-03 this paragraph named two switches, the second `logVolumeAlert=false` for the
alert on the workspace, which the template then built unless told otherwise. Step 20 found that
the alert does not count lines, and the parameter's default is now `false`: a run leaves that
alert out by itself. A run that wants it passes `@('logVolumeAlert=true')`, and that is not
remembered either. Read, not tried: a run without it neither creates the rule nor deletes one
that exists, since a run of the template removes nothing that exists.

Since 2026-10-05 two more overrides exist that no file keeps. `scheduledJobs=[]` takes the
policy's exception out (step 23's way back): a run without it writes the exception again.
`poolTimeout` sets the pool job's timeout: a run without it puts 600 s back on that job, and its
what-if shows the `Modify`. The demo's switch is not one of them: the app remembers it
([Changing the infrastructure later](#changing-the-infrastructure-later)).

**The users (step 6)**

| If | Then |
| --- | --- |
| The tool's `-?` does not name `ActiveDirectoryAzCli`, or the sign-in with it fails | `./infra/sql-principals.ps1 -AuthenticationMethod ActiveDirectoryDefault`. In the trial go-sqlcmd signed in with `ActiveDirectoryAzCli` (its version was not recorded) |
| That fails too | The older ODBC `sqlcmd`, which signs in through a window: `-SqlcmdPath '<its path>' -OdbcSignInName '<the account's sign-in name>'`. It runs with `-X1`, so that it starts no operating-system command |
| The tool's signature is not a valid Microsoft one | Stop: this program is handed the administrator's sign-in |
| The address cannot be read from the server's refusal | The script stops and prints the two commands to allow the address by hand under another name and to delete that rule afterwards |
| `Msg 50003` or `Msg 50004` on a database nobody has touched | Azure's baseline differs from the local engine's: read the names the file printed, correct the file's expected lists, run again. A defect, not a choice. It happened twice on 2026-10-03, before the users were made: at the first check, on a run, and at the permission list, found by a read-only look before the next run ([Measured on Azure](#measured-on-azure)) |
| The server refuses `WITH SID ..., TYPE = E` | `./infra/sql-principals.ps1 -CreateForm ExternalProvider`: `CREATE USER ... FROM EXTERNAL PROVIDER WITH OBJECT_ID`, which looks the identity up by its object ID, never by a name. The users keep their two names and the stored ID is still compared. In the trial the first form was accepted, and so was a look-up by the identity's name; this second form itself was not sent |
| That form is refused as well | Stop |
| The second run changes something | A defect in the file: fix it before going on |

**The sign-in (steps 7 and 8)**

| If | Then |
| --- | --- |
| Start 1 or start 3 ends with exit 4 (the login is refused) | Stop. In the trial a user made from the client ID signed in, so here the user and the identity do not match: read the two lines the users script printed, run it once more, start the probe job again, and bring a second refusal to the owner |
| Start 1 or start 3 ends with exit 5 (other roles) | Stop: the users script's own lists said these roles and no others. Bring the line the program printed (in the workspace, when it is due: step 10) and the two lines of step 6 to the owner |
| Start 2 ends with exit 0, 4 or 5 | Stop: it got a token for an identity the job does not carry, and the isolation between the two identities does not hold |
| The open took more than 10,000 ms | Nothing changes by itself. The number is recorded and the app's connect timeout stays 10 s: the first sign-in after a cold start may then answer one 503 with `Retry-After: 10`, and the smoke test tries four times. A larger value is the owner's decision, through `Database:ConnectTimeoutSeconds`, with ADR-0058's table worked again: that timeout also bounds each COMMIT. In the trial it took 3,810 ms, once. At step 7 on 2026-10-03: 6,390 ms as the app's identity and 3,253 ms as the migrator's, once each, on a cold replica of the probe, whose string waits 30 s. The app's 10 s stays until its own first sign-in after a cold start is measured. This row said that would be in the second session; the second session ran on 2026-10-03 and timed no cold start |
| Start 1 or start 3 ends with exit 3 (no token for the identity the job carries) | Start it again. The third time: stop, that is not a timeout |
| Any start ends with exit 2 (another failure) | Start it once more. The same again: stop, and bring the line the program printed (the error's number, its class and the chain of exception types; in the workspace when it is due) to the owner |
| The probe job cannot be built or started (an exit code the program does not give, and no line of it) | Skip it. Step 16 is then the first sign-in of these two users, and the pull request says so |
| The made-up SQL sign-in is refused without the Entra-only reason | "Not proven". `az sql server ad-only-auth get` then stands alone, and the pull request says the refusal was not provoked. A second try with a real SQL user made for the purpose and dropped at once is the owner's decision; no script here does it |
| The made-up SQL sign-in is let in | Stop: Entra-only is not on |

**The logs (steps 10 and after)**

| If | Then |
| --- | --- |
| No line of the probe job is in the workspace once it is due | Set `disableLocalAuth: false` in the template, deploy, run the probe job once more, read again when due. Still none: [Switching the logs off](#switching-the-logs-off). In the trial the lines arrived with key access off |
| The lines of a migration that lasts seconds never arrive | The logs stay on, because the app's lines are the other half: a short run may then leave a verdict and no text |
| Azure reports no exit code for an execution | The verdict line is a status and two times. "Failed" is still a verdict |
| At step 20 the metric's count is below the rows of the hour's middle or above those of the widened hour | The alert on the workspace does not count lines: it is deleted and left out from then on, as step 20 writes. A log search rule in its place is billed, and the owner's decision. This happened on 2026-10-03: the metric had no time series for an hour in which the workspace ingested 446 rows. The alert was deleted, the template now leaves it out unless asked, and the owner decided against a log search rule for now |

**The app and the deployments (steps 9 and 15 to 19)**

| If | Then |
| --- | --- |
| The what-if or the deployment with `deployApp=true` is refused on a parameter of `azurebank-app-inputs` (a length) | A value the app needs is missing or the wrong length: run `secrets.ps1 -Action New -DeployApp -ImageTag <the full SHA>` again, as the step does, and read its report. The app, the migrate job, the pool job when it is built, and the action group wait for that check, so none of them was sent. Until 2026-10-05 this row named three: the app, the job and the action group |
| The what-if lists anything as `Unsupported` | Answer "no" and stop: an ID of the template reads something a what-if cannot work out again, as on 2026-10-03 at step 9 |
| The alert on the workspace is refused, in a run that asks for it with `logVolumeAlert=true` | Leave the override out: without it the template does not build that rule. Until 2026-10-03 the rule was built by default, and this row said to pass `logVolumeAlert=false` and that no request for it had ever been sent. At step 15 that day Azure accepted it, one of four rules then; step 20 is why it is left out now |
| The policy accepts two replicas | Put 1 back at once and stop: the policy does not work |
| The test notification of step 15 is refused | Not a stop. On this offer it is refused, `(Conflict) Free subscription not supported` on 2026-10-03, when no row here named it and the session went on with the owner's mailbox as the proof. What proves the e-mail road is the message that asks the receiver to verify the address and the one that follows the verification (step 15). If neither arrives: stop |
| A deployment as the identity is refused naming `userAssignedIdentities/assign/action` | **Stop.** No role is created: a right on the two database identities would let the deployment identity attach the schema-changing one to the app that faces the internet. The deployment from the owner's terminal keeps working meanwhile |
| The raw log of a run holds the server's name, the app's address, one of the five IDs or an address | Stop: the mask or the print is a defect |
| The last-resort road cannot sign in | Its first half (run the job again and read its log) stands alone, and the failure's text goes to the owner. On 2026-10-03 it could not, as it was written then: with the tool's connect timeout of 10 s the driver had no token in time. With `Connect Timeout=60` in the string, as it is written now, it signed in ([A migration nobody can read](#a-migration-nobody-can-read)) |

**Turning the demo on (steps 22 to 33).** Written on 2026-10-05; no request of these steps has
been sent, so every row is a refusal that has not been seen.

| If | Then |
| --- | --- |
| The policy definition with its exception by name is refused, or the what-if of step 23 would touch the app, a job, the action group or an alert | Answer "no" where it is the what-if, and stop. The parameter `allowedJobTriggers` with `Schedule` in it is not used to get round it: it would let the migrate job be scheduled too |
| The run of step 25 is refused by the policy | Step 25's table: three reads say whether the app's part was applied; wait 15 minutes and run the step again as written. The third refusal is a stop |
| The what-if or the deployment of step 25 fails on the action group or on its receiver of the Azure mobile app | Step 25's own rule. A failed what-if sent nothing. After a failed deployment `--check` is read at once, since the app and the job may have been written all the same. Then the step again with no account, and the phone waits for a run of its own |
| The test notification of step 25 is refused, as step 15's was on 2026-10-03 | Not a stop, and no second request. The phone's road stays not measured until an alert fires |
| The test notification of step 25 is taken and nothing shows on the phone | Not a stop: the alerts go by e-mail as before. Step 25 has the three reads; a second test is the owner's decision, not sooner than five minutes later |
| Step 25's deployment answers `Succeeded` and `--check` does not pass | Before the job's next run: the pool job is deleted if no run of it was ever listed, the app is stopped if one was (step 25). Only then the diagnosis |
| The start by hand of step 26 is refused | Wait for the job's next run and read that execution |
| A deployment as the identity is refused naming `userAssignedIdentities/assign/action` on the pool job (step 28) | Stop, as for the migrate job and the app: no role is created |
| A scheduled job under another name is accepted, or two runs at once on the pool job are (step 29) | The job of the first is deleted, the parallelism of the second put back to 1, each at once and on the owner's word; then stop |
| A start that carries a setting is refused (step 31) | Not a stop: the signal is recorded as not provoked, and the metrics are read all the same |

## Deploy a commit

From `main`, in this order:

```powershell
gh workflow run deploy.yml --ref main -f action=build-push
gh workflow run deploy.yml --ref main -f action=deploy
```

`build-push` builds the three images of the commit and pushes them tagged with its SHA. A tag is
written once: an image that is already published is left as it is, and only the registry's own
"there is no such manifest" counts as absent. The digest of each image is in the run's summary.

`deploy` (`infra/deploy.py`), in order:

1. checks that the three images can be pulled without signing in, then signs in to Azure as the
   deployment identity;
2. reads the app, and from its containers whether it is the public demo: `Demo__Enabled` as
   `true` on both is on, on neither is off, and anything else stops the run. It reads the migrate
   job, and the pool job only if the demo is on; with the demo off it says "The app says the demo
   is off: the job azurebank-pool is not read and not moved.", and whether such a job exists is
   not asked. It prints what runs now (the three image references, four with the demo on, and the
   two revision names), and stops if a shape has drifted: the app's scale, revision mode,
   ingress, its two containers and no init container; the migrate job's trigger, parallelism and
   retry limit; and on each the one database identity it must carry and no other. A drift in the
   identity names the field and the ending it should have, never what was found;
3. with the demo on, checks the pool job's shape too: the trigger `Schedule`, the expression the
   template writes, one run at a time, no retry, a timeout from 60 to 840 s, no init container,
   one container named `pool` whose arguments are `recycle` and which has no command, and the
   identity `azurebank-app`. Then it reads that job's executions once and stops if a run is, or
   may still be, in progress: the migration is about to change the schema that run works on. A
   run the schedule starts after that read is not seen, and neither is one listed only on a
   later page, if Azure gives that list in pages ([Not measured yet](#not-measured-yet));
4. tries to list the app's secrets and goes on only if Azure refuses;
5. moves the tools image on the migrate job, starts the migration once, and waits for that exact
   execution. When it ends, either way, it prints the verdict: the execution's name, status, start,
   end, length, exit code and a one-word reason. A failed migration stops here: neither the pool
   job nor the app is touched;
6. with the demo on, moves the tools image on the pool job and reads its shape again. It never
   starts that job: the template gives it a schedule;
7. moves both app images in one request, waits up to 15 minutes for the new revision to be ready,
   and reads the app's shape again, as in step 2;
8. waits until the revision that was the app's latest at step 2 is inactive, so that the answers
   below come from the new code. If that revision was not also the app's latest ready one, the
   revision that was answering is another, and it is not waited for: read in the script, not
   provoked. Until 2026-10-05 this step said "the revision that ran before";
9. smoke test: `/` is the built page; `/health/ready` answers `Healthy` (not merely 200: the BFF
   answers 200 `Degraded` when the API is down); one sign-in for an address nobody can register,
   sent to `/bff/auth/login`, must be refused with 401 and the code `INVALID_CREDENTIALS`. That
   answer means the BFF reached the API with its key and the API asked the database. It writes
   nothing. Measured on a local stack of this code: that request answers 401 with that code when
   the schema is there, and 500 on a database with no table while `/health/ready` still says
   `Healthy`. The sign-in is the one check that fails when a migration did not run, and with
   identities it is also the one that fails when the app cannot sign in to the database.

Until 2026-10-05 this list had seven steps and one job: steps 3 and 6 are the pool job's, and
with the demo off a deployment is the seven it was.

**With the demo on the smoke test asks two things more, and neither spends a copy.** The page
must say what the app's containers say: it carries the demo's tag,
`<meta name="azurebank-demo" content="true">`, exactly when the demo is on, so a page that says
"demo" on an app set otherwise fails, and so does the reverse. And after the refused sign-in one
more request is sent, only with the demo on: `POST /bff/auth/register` with the body `{}`, which
must be answered 403 with the code `REGISTRATION_CLOSED`. It is tried and waited for as the
sign-in is, and an answer that is not the refusal is told by its status and its error code,
never by its body. With the demo off no registration is ever sent: the door is open then, and
the request would register somebody. Both answers were measured on the compose stack with the
demo on, on 2026-10-04 (ADR-0063, Validation; `SECURITY.md`, "Registration closed on the demo"):
not by this script, whose two checks have met invented answers only, and not through the Azure
ingress. With the demo on a deployment sends four requests and no cookie, and is expected to
spend two permits of the ten a minute that sign-ins share.

**What those two checks do not prove, and what does.** Both are answered by the BFF alone. That
the `api` container carries the flag is read from the app, in step 2. That the pool job's PIN
pepper is the API's is what `python infra/deploy.py --check` compares
([Reading the logs](#reading-the-logs)). That a visitor is handed a copy and that a PIN is taken,
end to end, is proved by a browser (step 27) and by nothing a deployment runs: a wrong pepper
answers a PIN with 200 and `verified` false, so no status, alert or exit code shows it. No
deployment claims a copy: a claim spends one, leaves audit rows, and its answer holds a copy's
password, which must not reach a public log.

**The workflow prints the migration's verdict and never its text.** The log of a public repository
is public, and that text can name the server, an address or a value from a database error. Inside
GitHub Actions the verdict's reason is printed only if it is one plain word, and Azure's own
message is not printed at all. The same line is on the run's summary page. Around it, an
execution's name and its status are printed only in the shape expected, and in whatever else the
run prints of Azure's words (a refusal, a revision's error, a replica's state) an ID is replaced
by `<id>` and anything shaped like an IPv4 address by `<address>`. The name of a host is not
looked for. The text could not be printed in time anyway: in the trial a line could be read about
six and a half minutes after it was written (the median), and the verdict as soon as the run had
ended.

**If step 7 or step 9 fails, the script tries to put the app back** on the template it had at
step 2 (if that fails too, the run says the app may be serving a broken revision), under a new
revision, and the run still fails, saying "put back to `<tag>`; the schema stays where the
migration left it". The pool job is not put back: it stays on the new tools image, as the migrate
job does. The sign-in is tried up to four times and the last try decides. If the last
try is rate limited (429) or gets no answer (the connection refused, reset, closed or timed out),
nothing is proved either way: the run fails as *unproven* and the new revision is left in place.
With the demo on the registration is tried the same way, and ends the same three ways.
While the page or `/health/ready` gets no answer, they are asked again every five seconds for five
minutes; still nothing then is a failure, and the app is put back.

**An interrupt puts nothing back.** Ctrl+C stops the script where it is, in any mode, with one
sentence that starts "Interrupted." and exit code 1. A workflow run that is cancelled may reach
the script as that interrupt, or end with no sentence at all: GitHub's page on cancelling a run
says the interrupt is sent to the step's shell, a second signal 7.5 seconds later, and that the
process tree is killed if the step is still running 2.5 seconds after that
(<https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-cancellation>,
read on 2026-10-06). Whether the script, which that shell starts, is sent the interrupt has not
been tried here. Either way no put-back follows and nothing that was started is stopped, so what
was moved or started is read afterwards ([When something fails](#when-something-fails)). Until
2026-10-05 an interrupt ended in Python's traceback.

No request a deployment sends carries an identity: a change is a location and a template.

Every deployment ends the sessions held in the replica's memory, and so does every scale to zero.

**One deployment at a time.** The workflow's concurrency group is about the workflow's own runs:
a `deploy.py` run from a terminal is not held back by it, and does not hold a workflow run back.
On 2026-10-03 one of each overlapped for about 50 s, not on purpose, with the same images. Both
ended well, and afterwards one revision was active (step 17). It was not tried with different
images. Do not run one while the other runs.

**A run of the template is a third writer of the app, and nothing holds it back either.** Until
2026-10-05 this section named two writers. `deploy.py` sends the app the template it read at its
start: at its move, after the migration, and once more if it puts the app back. A run of the
template that ended in between is expected to be undone on the app: its settings, the demo's
flag among them, go back to what the script read, while what that run created beside the app
stays. After step 25 that is the pool job on its schedule beside an app whose flags are off
again, the state [Turning the demo back](#turning-the-demo-back) forbids. And the deployment
ends green: its smoke test asks for the demo it read at its start. No command of this folder
shows it afterwards, since with the flags off neither a deployment nor `--check` asks whether a
pool job exists; the job list of step 1 does. The other way round it is the deployment that is
undone: `secrets.ps1` writes the tag the app ran when the script read it, so a run of the
template that began before a deployment moved the images is expected to put the earlier tag back
on the app and on each job, with no migration and no smoke test.

So no run of the template with the app starts while a `deploy` run of the workflow is queued,
waiting for approval or in progress, or while `deploy.py` runs in a terminal; and no deployment
is dispatched, approved or started between `secrets.ps1 -Action New` and the `--check` that
follows the run of the template. The read that comes before such a run:

```powershell
gh run list --workflow deploy.yml --limit 5 --json status,displayTitle,createdAt --repo Gurgant/azurebank-v2
```

Every run it lists must read `completed`. `queued`, `waiting`, `requested`, `pending` and
`in_progress` are a run that has not ended: wait for it, or cancel it before it is approved.
This is a rule for whoever types, not a guard: the script does not read the app again before it
sends its template. It is read in `deploy.py` (`image_patch`, `put_back`) and in `secrets.ps1`,
and none of it has been provoked on Azure. The command's flags and fields are the ones
`gh run list --help` names (gh 2.97.0); it has not been run for this page.

## Reading the logs

From a terminal where the owner has run `az login`, with the two variables of step 16 set:

```powershell
python infra/deploy.py --job-log                  # the latest migration: its verdict, then what it printed
python infra/deploy.py --job-log '<execution>'    # a named one
python infra/deploy.py --pool-log                 # the latest run of the pool job, the same way
python infra/deploy.py --pool-log '<execution>'   # a named one
python infra/deploy.py --app-log 30               # what the app's two containers printed in the last 30 minutes
```

`--app-log` takes 1 to 1440 minutes. Each command shows at most 5,000 lines, and each is refused
inside GitHub Actions. The other way in is the portal: the workspace `azurebank-logs`, Logs.
Two more commands of the owner's terminal read without the workspace, and are told at the end of
this section: `--check`, which reads the running app and moves nothing, and `--pool-run`, which
starts the pool job once and reads how that run ended. Until 2026-10-05 this section had the two
log commands of the first deployment and no other.

- **The cap comes first.** Each command starts by saying what the daily cap is doing: its value,
  `dataIngestionStatus`, and the next reset. `OverQuota` means the workspace has taken no line
  since the cap was reached and takes none until the reset, at an hour Azure picks.
- **`--job-log` reads one execution's lines.** It asks for the job's lines whose
  `ContainerGroupName` starts with the execution's name and a hyphen, between two minutes before
  the execution's start and five after its end. Until 2026-10-03 it asked by the job's name and
  that period alone, and a read of one execution printed its 20 lines and the 5 of the next one,
  which had started inside the period (step 16). A name that is not letters, digits and hyphens,
  100 at most, is refused before anything is read. The query with the new filter is tested
  offline and has not been sent to the workspace yet.
- **`--pool-log` reads a run of the pool job the same way.** The query differs by the job's name
  and nothing else, and the verdict above the lines is worded with the pool job's own exit codes
  (the table below). The line to look for is the run's one summary line, which starts `pool:`;
  [`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md) says what each of its counts
  means. A run that left no such line did not finish, or was refused: its last line says which.
  Before the pool job has run, the command ends with "The job azurebank-pool has no execution
  yet."; on a deployment where the demo was never turned on there is no such job, and the
  command ends in Azure's own words for that read. No run of the pool job exists yet, so this
  command has read nothing on Azure.
- **An empty answer is not proof that nothing was printed.** A line takes minutes to arrive (in
  the trial a median of 387 s and 398 s for two jobs, 486 s at the most; on 2026-10-03 the lines
  of step 16's migration were there when they were read, 11 minutes after they were written),
  Microsoft's page allows a new diagnostic setting up to 90 minutes, and a capped workspace takes
  none. The trial's three runs that lasted seconds each kept their line: three runs, not a
  promise.
- **Telling two failures of the migration apart by the run's length**, which is on the verdict
  line: a login refused because the database has no user for the identity ends after about four
  seconds; an identity that gets no token is waited for the whole 60 s. Neither has been seen
  from `migrate` on Azure. The trial's own program got the two answers `migrate` would get: with
  the right identity signing in to `master`, where it had no user, error 18456, class 14, after 41
  to 52 ms; with no token, an error numbered 0, class 20, around
  `Azure.Identity.AuthenticationFailedException`, after 40 to 71 ms.
  The token's failure is quick each time: it is `migrate`'s own wait that makes that run long.
  From the owner's terminal, with `Active Directory Default` and not a managed identity,
  `migrate` printed the no-token answer once, error 0 of class 20, after 11 s (step 19).
- **What the text can hold.** The SQL server's name and the database's. A caller's address: the
  rate limiter's warning names it. A value from a database error: on a local stack, registrations
  racing for one address printed that address four times in two lines of the API's console, whole,
  and a handle the same way. The rule that keeps people's identifiers out of the logs (ADR-0017)
  covers the application's own messages, not the text of a database error.
- **Who can read it.** The owner, and a Global Administrator of the directory, who can give
  themselves access. The deployment identity cannot: it has no right on the workspace.
- **Never paste the text of `--job-log` or `--app-log` into a pull request, an issue or a chat.**
- **It is a debugging record, not an audit trail.** Whoever wants to act unseen can fill the cap
  first. Who changed the app is read in the subscription's activity log, which the cap does not
  touch.

**Reading the running app, and moving nothing.**

```powershell
python infra/deploy.py --check
```

A run of the template that changes the app is expected to make a revision outside `deploy.py`:
no migration, no smoke test and no put-back follow it. No record shows one yet: step 15 created
the app, and every revision since was made by the script's own request. `--check` is its
read-back, and the read-back of any other step that could change the app or the pool job. Every
request it sends is a read, but for two listings of secrets, which Azure asks for as a POST; a
wrong answer puts nothing back. In order:

1. the app, read until its last update has succeeded and its latest revision is its latest ready
   one, for up to 300 s. An update that ended `Failed` or `Canceled` is not waited out;
2. from that read, the app's address, whether it is the demo, and its shape, as a deployment
   reads them;
3. the app's revisions, read until no other one is active, for up to 180 s. "Ready" is not "the
   one before has stopped answering": until every other revision is inactive an answer could
   still come from one of them. When none is, the line is "no other revision is active: what
   answers now is that revision". Since 2026-10-06 one line follows it, with how many
   networks of proxies the `bff` container names and never which: "The bff container names no
   network of proxies", or "names 1 network of proxies", and on. The count was read at step 2,
   with the shape, and a setting about forwarded headers that the template never writes, on
   either container, ends the check there;
4. with the demo on, the pool job: its shape, as a deployment checks it, and then the secrets of
   the app and of the job, listed as whoever is signed in. The job's PIN pepper and connection
   string are compared with the app's, and one line says "The pool job's PIN pepper and
   connection string are the app's", or the check fails and names which of the two differs. No
   value, no part of one and no length is printed or put into an error. With the demo off the
   pool job is not read and no secret is listed, and whether a pool job exists is not asked: one
   left beside flags that are off is shown by the job list of step 1, not by this command;
5. the smoke test of a deployment, with the demo as the app says it.

It ends "Checked: ... the smoke test passed; nothing was moved." It is the one mode of the script
that lists a secret. A deployment never does: a workflow run goes on proving that its own
identity is refused that listing.

**Why the two secrets are compared.** The tool that builds the copies must hash their PINs with
the pepper the API verifies them with. The template writes the job's two secrets from the two
expressions it writes the app's from, so a run of the template is expected to leave them equal.
But the job holds a copy of its own, and whoever may write the app can overwrite the app's secret
alone ([What each identity can do](#what-each-identity-can-do)). With two peppers that differ, a
PIN of a copy is refused while every status stays good: a PIN that is not taken is answered 200
(`backend/src/AzureBank.Bff/Controllers/BffAuthController.cs`, `verify-pin`). So `--check` is run
after every step that could change either side: a run of the template, a deployment, a start of
the job by hand, a stop and a start of the app. "The app's" says the two secrets are equal. That
a PIN is then taken, end to end, is what a browser proves (step 27).

**Starting the pool job by hand, and reading how that run ended.**

```powershell
python infra/deploy.py --pool-run
```

The job runs on its schedule, and a deployment never starts it. This starts it once beside the
schedule, as whoever is signed in: the first fill, or a refill by hand. It reads the job and
checks its shape; reads its executions once and refuses while one is, or may still be, in
progress, because two runs at once each top the pool up from their own count
(`backend/tools/AzureBank.Seeder/README.md`); prints "Starting the job azurebank-pool once." and
sends the start, once and never again; waits for that exact execution for the job's timeout and
two minutes; and prints its verdict. That first line comes before the one write of the command:
a run that ends without it sent no start, and one that is cut short after it may have made a
run. **How the run
ended is told by the exit code of its container, never by the execution's status alone**: how
Azure words an execution whose container exits with a signal's code has not been seen. Where the
status could make the code doubted, the last line says "The exit code decides here, whatever
status Azure gave the execution."

| Exit code | What the script says of it | `--pool-run` |
| --- | --- | --- |
| 0 | done | ends well |
| 1 | did not finish, and left no summary line; read its last line before it is started again | fails |
| 2 | refused before anything was opened; the job's configuration must change | fails |
| 10 | done, with a signal: the pool was low (PoolLow) | ends well |
| 11 | done, with a signal: the pool was empty (PoolEmpty) | ends well |
| 12 | needs a look: a copy could not be built (TopUpIncomplete) | fails |
| 13 | needs a look: a user outside every copy exists (ForeignUsers) | fails |
| 14 | needs a look: a copy could not be deleted (DeleteFailed) | fails |
| 15 | done, with a signal: the day's claims held the top-up back (ClaimCeiling) | ends well |
| not reported | Azure gave the run no exit code: how it ended is not guessed from its status | fails |

The codes are the tool's own (`backend/tools/AzureBank.Seeder/Pool/PoolExitCodes.cs`), and what
to do about each is in [`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md). For a code
from 10 to 15 the last line also names the count of the run's summary line that the code says to
read: `was` for 10 and 11, `free` for 12, `foreignUsers` for 13, `failed` for 14, `ceiling` for
15. The line itself is not fetched by this command: the last sentence names
`python infra/deploy.py --pool-log <the execution>`, which reads it once the workspace has it.
Whatever the code, the run is never started a second time by the script.

"Not reported" is an answer of Azure's that carried no code. A read of the code that Azure
refuses or fails is told apart from it: the command prints the verdict as the waiting knew it,
on which no exit code is expected, and then fails with "the read of its exit code failed" and
Azure's own words ([When something fails](#when-something-fails)).

Four things this command does not do. It asks the app nothing: the job's container carries the
demo's flag as a plain word, so a run is expected to build the pool whatever the app's two
containers say, and whether the app is the demo is `--check`'s to say before a run is started by
hand. It does not see a run the schedule starts after its one read of the executions, nor one
listed only on a later page, if Azure gives that list in pages. It does not read the migrate
job: a deployment refuses to start beside a pool run, and this command does not refuse to start
beside a migration, which may be changing the schema the run works on. So it is not run while a
deployment is under way: the read under [Deploy a commit](#deploy-a-commit), "One deployment at
a time", comes first. And it does not keep what it read: the exit code is Azure's to keep and
the line the workspace's, 30 days; `Show-Executions azurebank-pool` (under
[Create it once](#create-it-once)) prints the codes Azure still holds, in brackets, and on
2026-10-03 an older execution of the migrate job no longer carried its own (step 16).

## Switching the logs off

By rule, on the owner's word, when one of these is true:

- any cost shows on the log ingestion meter;
- a day's billable volume in the `Usage` table is above twice the cap (100 MB), read after any
  alert;
- no line ever reaches the workspace;
- the offer refuses the workspace after part of it exists.

Three deletions and a read. `keepLogs=false` alone deletes nothing: the template never removes a
workspace or a setting that exists.

```powershell
# 1. The diagnostic setting, and the alert on the workspace if a run turned it back on (step 20).
$environment = az containerapp env show --name azurebank-env --resource-group $group --query id --output tsv
az monitor diagnostic-settings delete --name to-azurebank-logs --resource $environment
az monitor metrics alert delete --name azurebank-log-volume --resource-group $group

# 2. The environment sends its logs nowhere. Add -DeployApp if the app exists, and then read
#    first that no deployment is queued, waiting or running (Deploy a commit, "One deployment at a time").
try {
    ./infra/secrets.ps1 -Action New -LogsOff
    Invoke-Template 'logs-off'
} finally {
    ./infra/secrets.ps1 -Action Remove
}

# 3. The workspace and what it holds. Without --force Azure keeps the name and the data for 14 days,
#    and a workspace created again under that name is the old one.
az monitor log-analytics workspace delete --resource-group $group --workspace-name azurebank-logs --force --yes

# 4. Read back.
az containerapp env show --name azurebank-env --resource-group $group --query properties.appLogsConfiguration
az monitor diagnostic-settings list --resource $environment --query '[].name' --output tsv
az resource list --resource-group $group --resource-type Microsoft.OperationalInsights/workspaces --query '[].name' --output tsv
```

Expected at the end: no destination, or `none`; no setting; no workspace. From then on
`secrets.ps1` reads the environment and keeps `keepLogs=false` on every later run, and the two log
commands say that the logs are switched off. A deployment still prints its verdict.

To switch them on again, run the template as under
[Changing the infrastructure later](#changing-the-infrastructure-later), with
`Invoke-Template 'logs-on' @('keepLogs=true')`. It creates a new, empty workspace.

Of these commands one has been run: the deletion of the alert on the workspace, as step 20's own,
on 2026-10-03. It exited 0 and the alert was gone from the list. The others have not been run,
and neither has that deletion for an alert that does not exist.

## Stop the app by hand

The portal: the app `azurebank`, **Stop**. From a terminal (the core CLI has no
`az containerapp stop`; measured: the command is not recognised):

```powershell
$app = az containerapp show --name azurebank --resource-group azurebank-demo --query id --output tsv
az rest --method post --url "https://management.azure.com${app}/stop?api-version=2025-01-01"
```

`/start` instead of `/stop` starts it again. The database keeps its daily charge while the app is
stopped. The deployment identity cannot stop or start the app.

Neither call has been run on this app. Step 32 runs both once, and reads which property of the
app says that it is stopped: no file of this repository names it. With the demo on, stopping the
app does not stop the pool job: it is expected to go on by its schedule, which harms nothing
while nobody is served ([Turning the demo back](#turning-the-demo-back)). What a deployment or a
`--check` reads of a stopped app has not been seen.

## When something fails

| What you see | What it means | What to do |
| --- | --- | --- |
| `build-push`: "The registry gave no clear answer" | The registry did not say "no such manifest". For a package that has never been published it may answer `denied`, the same as for a private one | First publication only: run again with `-f first_publication=true`. Otherwise read the answer printed below the error and run again. The first publication of 2026-10-03 did not stop this way: it went through with the box left alone |
| `deploy`: an image "is not published, or its package is not public" | The check before the Azure sign-in | Run `build-push` at this commit. If the image is published and the anonymous check of step 14 still answers `denied`, the owner sets that package to Public, which cannot be undone. On 2026-10-03 the three packages were public as soon as the workflow had published them |
| "is not in the shape this script deploys onto" | Something changed the app or a job outside the template: its scale, its ingress, its containers, or the identity it carries; for the pool job also its schedule, its timeout, its arguments or a command. The words in brackets say when it was read. "(nothing was changed)": nothing was changed by this run. "(after its images moved)": the app had moved, and the run printed what Azure says about the revision and put the app back, as below. "(after its image moved)": the job runs the new tools image; for the migrate job no migration ran and the app was not touched, for the pool job the migration had run and the app was not touched. "(after the put-back)": the old images run again, and the shape is still wrong. "(nothing was moved)" is `--check`'s, and "(nothing was started)" is `--pool-run`'s | Run the template again ([Changing the infrastructure later](#changing-the-infrastructure-later)). If it is the identity, read [If something was stolen](#if-something-was-stolen) first. A schedule or a timeout of the pool job that changed is read by this check and by nothing else on Azure: who changed it is in the activity log |
| "This identity can list the secrets" | The identity holds more than the custom role. Nothing was changed | Look at its role assignments: there must be exactly two, or three once the demo is on, the third on the pool job |
| "Azure asked for a right on a database identity" | Azure wants `assign/action` on the attached identity before it changes the job or the app. The request changed nothing and was not tried again | Stop. No role is added: [If Azure says no](#if-azure-says-no) |
| "Execution ... is Running: a migration may still be running" | An execution blocks every later deployment until it ends, and the deployment identity cannot stop it | The owner: `az containerapp job stop --name azurebank-migrate --resource-group azurebank-demo --job-execution-name <name>` |
| "The migration did not succeed", after its verdict | The job runs the new tools image; some migrations may be applied; the app still runs the old images. Exit code 1: failed after it reached for the server, and running it again is safe. Exit code 2: refused before any connection, and the configuration must change | `python infra/deploy.py --job-log` from a terminal, when the lines are due. If there is no text: [A migration nobody can read](#a-migration-nobody-can-read) |
| "the new revision never became ready", then "put back to ..." | The run printed the revision's state and each container's state and restart count, then put the app back | Read those lines, then `python infra/deploy.py --app-log 30`; fix; deploy again |
| "Smoke test failed", then "put back to ..." | The page, the readiness answer or the sign-in answer was wrong on the new revision. A sign-in that answers 500 or 503 where 401 was expected can be the database sign-in. With no user for the identity the driver gets error 18456, class 14, which the API answers with 500; with no token it gets an error of class 20, which the API answers with 503 (the two errors: [Measured on Azure](#measured-on-azure), the first in `master`, where the identity had no user; the two answers: read in the API's handler, not seen) | The same |
| "Smoke test unproven" | The last of four sign-in tries was a 429 or got no answer (the wait is 65 s after a 429, 20 s otherwise). An earlier try may have got another answer: only the last one decides. Sign-ins are limited to 10 a minute, and behind the ingress every visitor may share that limit. The new revision is serving and was not put back | Deploy again later, or check a sign-in by hand |
| "Azure refused or failed a request", "The Azure CLI gave no answer in 180 s" | One request to Azure failed after the run had started. The script does not ask twice, and it puts nothing back: no check had failed. The app may already be on the new images, unchecked | Look at the app's latest and latest ready revision; deploy again, or go back by hand, below |
| "was still active after 180 s" | The old revision did not go inactive, so the smoke test was not run and nothing was put back | Look at the app's revisions in the portal; deploy again |
| "The put-back ... did not succeed. The app may be serving a broken revision" | Both the deployment and the way back failed | Go back by hand, below |
| "There is no log workspace azurebank-logs in this resource group" | The logs are switched off: nothing is kept | The verdict line is all there is |
| The first request after the app was idle answers 503 with `Retry-After: 10` | The replica started from zero and its first sign-in to the database, token included, did not fit in the 10 s connect timeout. Possible; in the trial the first open of a process on a cold replica took 3,810 ms, measured once, and at step 7 of the first deployment 6,390 ms as the app's identity, once | Ask again. If it happens every time, it is the row of the probe's 10,000 ms in [If Azure says no](#if-azure-says-no) |
| "Interrupted. Nothing is put back and nothing is stopped by this" | The script was interrupted, in whichever mode it ran: Ctrl+C on a terminal. It stopped where it was, with exit code 1 and this one sentence. It did not put the app back and stopped nothing it had started: a request that was on its way may have reached Azure, a migration or a pool run that was started goes on, and an app that was moved stays on the new images, unchecked. A workflow run that is cancelled may end with this sentence or with no sentence at all: GitHub's page on cancelling a run says the interrupt is sent to the step's shell, and that the process tree is killed if the step is still running ten seconds later, and which of the two it is has not been tried. What to do is the same for both. Until 2026-10-05 an interrupt ended in Python's traceback | What the run printed before it says how far it got, and a deployment's "Running now" line names the images to go back to. Then `python infra/deploy.py --check` for the app, `--job-log` for a migration and `--pool-log` for a run of the pool job. An execution still listed as running refuses the next deployment until it ends (the rows of "is Running" in the two tables). Then deploy again, or go back by hand, below |

**With the demo on, and the three commands that came with it.** Added on 2026-10-05. Each row
quotes the sentence `deploy.py` prints, so a search of this page for the words on the terminal
finds it. None of these has been seen on Azure: what each means is read from the script and from
the code of the app.

| What you see | What it means | What to do |
| --- | --- | --- |
| `--check`: "The app is not in the shape this script deploys onto", then "the settings about forwarded headers in the container 'bff' are not what the template writes", or "the container 'api' carries a setting about forwarded headers" | A setting that tells a host whose `X-Forwarded-For` header to believe was put on the app by hand: the template writes the networks of proxies on `bff` alone, numbered from 0, and nothing else of the kind. Nothing was moved, and what was found is not shown. A deployment does not read these settings and goes on moving the images | The names of the two containers' settings are read (`az containerapp show`, `--query 'properties.template.containers[].env[].name'`). Then one run of the template writes the `bff`'s settings whole: with `-ProxyNetworks` and the networks that are meant, or with the word `none` (step 30). `secrets.ps1` stops on such a setting too when no network is named: "The bff container of the deployed app carries a forwarded-headers setting this template never writes." This repair has not been rehearsed |
| "The app is not in the shape this script deploys onto", then "Demo__Enabled is true in ['bff'] and not in ['api']", either way round, or "Demo__Enabled in the container ... is something the template never writes" | The app's two containers do not say the same thing about the demo, or one carries the setting with something other than the plain `true` or `false`. The template writes both from one switch, so somebody changed a setting by hand. No side is chosen: nothing was changed, and a `--check` that meets it says "(nothing was moved)" | `secrets.ps1` refuses such an app as well, with one of two sentences: "The two containers of the deployed app disagree about Demo__Enabled. Nothing was written.", or, for a setting that is not the plain `true` or `false` written once, "The bff container of the deployed app carries Demo__Enabled with something this template never writes. Nothing was written." (or "The api container ..."). So the owner first puts the setting right on the app itself, then runs the template and `--check`. Which value is right is not a guess: if a run of the pool job was ever listed, it is on ([Turning the demo back](#turning-the-demo-back)). This repair has not been rehearsed |
| "The app says the demo is on, and the job azurebank-pool could not be read: ..." | With the demo on, a deployment and a `--check` need that job, and no answer of Azure's is read as "there is no pool job": what Azure answers for a job that is not there has not been seen. Nothing was changed. Every workflow deployment stops here until the job is back, and only `--app-only`, which runs no migration, still moves the app | If the job was never created or is gone by accident: a run of the template creates it again, since `secrets.ps1` keeps the switch. If it was deleted on purpose, that is the first step of a road of [Turning the demo back](#turning-the-demo-back): finish that road. If it is there and Azure refuses the read: the role assignments, which must be three |
| `--pool-run`: "The job azurebank-pool could not be read: ... Nothing was started. infra/main.bicep writes that job only with the demo on" | The same read, by the command that asks the app nothing. On a deployment where the demo was never turned on there is no such job to start. `--pool-log` has no sentence of its own for it: there the read of the executions ends in Azure's words | Turn the demo on first (step 25), or read the refusal as above |
| "The executions of the job azurebank-pool could not be read, so whether a pool run is in progress is not known: ..." | The job was read a moment before, and the read of its executions failed. Nothing was changed, or with `--pool-run` nothing was started | Run it again. Refused again: `Show-Executions azurebank-pool` as the owner, and the role assignment on the pool job |
| "Execution ... of the job azurebank-pool is Running: a pool run may still be in progress, and a deployment does not start beside one", or with `--pool-run` "a second run is not started beside one" | A run of the pool job is listed as running, or in a state the script does not know and young enough to be alive. A migration beside it would change the schema it works on, and two runs at once each top the pool up from their own count. Nothing was changed, or started | Wait for the job's timeout, which the sentence names, and deploy or start again. Refused again: the owner reads `Show-Executions azurebank-pool` and stops that one, `az containerapp job stop --name azurebank-pool --resource-group azurebank-demo --job-execution-name <name>`. The deployment identity cannot. That Azure stops an execution in such a state is expected, not seen |
| "Smoke test failed: ... The page does not carry the tag of the public demo", or "The page carries the tag of the public demo", then "put back to ..." | The page and the app's containers disagree about the demo on the new revision: the BFF's image does not read the flag, or reads it another way than the settings say | The same as for "Smoke test failed" above; and whether the commit deployed is a descendant of the one that reads the flag (step 22's `git` command) |
| "Smoke test failed: the registration probe expected 403 REGISTRATION_CLOSED and got ...", then "put back to ..." | With the demo on, a registration with an empty body was not refused as closed by the new revision: on the demo that door must be shut, whatever the body. Only the status and an error code are shown, never the body | `python infra/deploy.py --check` on the revision that was put back. If it fails the same way, registration is open beside the pool: stop the app ([Stop the app by hand](#stop-the-app-by-hand)), then read `--app-log 30` |
| "Smoke test unproven: in 4 tries the registration probe got 429 (rate limited) last", or "got no answer last" | The page, the readiness answer and the sign-in were right, and the last try of the registration proved nothing. The limit it met is the one sign-ins share, ten a minute, which a closed registration is expected to spend from too. The new revision is serving and was not put back | Deploy again later; or `python infra/deploy.py --check` from a terminal, which asks the address the same four questions and moves nothing |
| `--check`: "The app is not in a state to be checked", or "After 300 s the app was not in a state to be checked (its last update: ...; its latest revision: ...; its latest ready revision: ...)" | The app's last update has not succeeded, or its latest revision is not its latest ready one. Until it is, an answer could still come from another revision: nothing was proved, nothing was moved | Look at the app's revisions, then check again. After a run of the template that turned the demo on, step 25 says what is done before the pool job's next run |
| `--check`: "After 180 s the list of the app's revisions did not show ... answering alone (...), so an answer could still come from another revision" | Another revision had not gone inactive, or the latest was not in the list. Nothing was proved, nothing was moved | The same |
| `--check`: "The list of the app's revisions came with a link to a next page, and this script reads one answer" | Whether Azure gives that list in pages is recorded nowhere here. A page that was not read could hold a revision that is still active, so the check stops and proves nothing | Read the revisions by hand, names and `active` only (`Show-Revisions`, among the functions of the third session; `Show-List` says whether the link is there). If Azure always sends that link, `--check` cannot pass until the script reads on: a change of `deploy.py` |
| `--check`: "The app is in shape and reports no address (its ingress holds no host name), so there is nothing to ask" | The shape check passed and the app's answer names no host. Nothing was proved, nothing was moved | Read the app's ingress; run the template again |
| `--check`: "The pool job's PIN pepper (its secret pin-pepper) differs from the app's. Nothing was moved, and no value was shown.", or "connection string (its secret app-connection)", or both | The job holds another pepper than the API verifies with, or signs in with another string than the app. With another pepper no copy that job built takes a PIN, and every status stays good | For the pepper: on the owner's word, stop the app, so that no visitor meets a copy whose PIN fails ([Stop the app by hand](#stop-the-app-by-hand)). Making the two equal again is a run of the template, which writes the job's two secrets from the app's, and then `--check`. Both need the app started again: what a run of the template or a `--check` does with a stopped app has not been seen, and while the app is up a visitor can be given such a copy. Equal secrets do not repair the pool: a free copy built with the other pepper is still handed out, with a PIN that fails, until a run deletes it, and none does before the copy is too old to count (`Demo:Pool:MaxFreeAgeHours`, 44 hours by default); a claimed one goes when its time is over. So before visitors are let back the owner chooses: those hours with the app stopped, or the new database of [Turning the demo back](#turning-the-demo-back). A secret that changed outside the template is a change nobody ordered: read [If something was stolen](#if-something-was-stolen). Reasoned, not rehearsed |
| `--check`: "The pool job's secrets could not be compared with the app's: ... lists no single value of text for ..." | One side lists that secret twice, not at all, empty, or as something that is not text. Two that hold nothing are not "the app's" | Read the secret names of the app and of the job; run the template again; check again |
| `--check`: "The secrets of ... could not be listed, so the pool job's secrets could not be compared with the app's: ..." | Azure refused a listing, or its answer could not be read. Listing the app's and the job's secrets takes the owner's sign-in: it is the one thing the deployment identity is refused on purpose | Run it as the owner. An answer that "could not be read" is the CLI's output, not Azure's refusal: run it again |
| `--check`: "... This was --check, not a deployment: nothing was moved and nothing is put back; where that says to deploy again, check again." | The smoke test failed or was unproven inside a check. Its own words are a deployment's; this sentence corrects them | The row of that smoke test's sentence, with "check again" for "deploy again" |
| `--pool-run`: "The job azurebank-pool was asked to start once, and Azure refused or failed a request before the run it made was known", or "an answer was not in the shape this script reads before the run it made was known" | The start was sent, and whether a run began is not known. The start is not sent again | `Show-Executions azurebank-pool` before any second start. A new execution: read it with `--pool-log`, by its name. None: start again |
| `--pool-run`: "The pool run was started (...), and a read of the job's executions failed while its end was waited for" | The run exists and may still be in progress. It was not started again, and a second start is refused while it is listed as running | `python infra/deploy.py --pool-log <the execution>` when it has ended and its lines are due |
| `--pool-run`: "Timed out waiting for ... of the job azurebank-pool: it had not ended ... s after it was started here (the job's timeout and two minutes)" | Azure did not end the run within its own timeout. While it is listed as running, a deployment and a second start are refused | The sentence ends with the command that stops it, for the owner. Then `--pool-log` for what it printed |
| `--pool-run`: "The pool run did not end well (...: exit code ..., ...)" | The run ended with 1, 2, 12, 13 or 14, or with a code that is not the tool's. It was not started again | By its code: the table under [Reading the logs](#reading-the-logs), then [`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md). After exit 1, no second start before its last line has been read |
| `--pool-run`: "The pool run ended and Azure reported no exit code for it" | The run is over, the read of its exit code was answered, and the answer carried no code. How it ended is not guessed from its status, so the command fails although the run may have ended well | `Show-Executions azurebank-pool`, or `--pool-log` with the run's name, reads the code once Azure has it. On 2026-10-03 an older execution of the migrate job no longer carried its own |
| `--pool-run`: "The pool run ended (...), and the read of its exit code failed: ..." | The run is over, and the one read that carries its exit code was refused by Azure or failed: Azure's words follow the colon. The verdict line above the sentence is what the waiting knew, and no code is expected on it. How the run ended is not guessed from its status, so the command fails although the run may have ended well, and the run was not started again | The sentence ends with its command, `python infra/deploy.py --pool-log <the execution>`, which reads the verdict again, with the code if Azure has it, and the run's lines once they are due. Or `Show-Executions azurebank-pool`, which asks with the same API version. If that read fails as well, the code cannot be read now: stop, and no second start before the run's lines have been read |

**Going back by hand.** As the owner, from a terminal, to a commit whose images are published
and, with the demo on, one that reads the demo's flag (below):

```powershell
$env:AZURE_SUBSCRIPTION_ID = az account show --query id --output tsv
$env:AZURE_RESOURCE_GROUP  = 'azurebank-demo'
$env:IMAGE_TAG             = '<the full SHA to go back to>'
python infra/deploy.py --app-only
```

It moves the app only: no job is touched and no migration runs. It keeps the same checks and the
same put-back. It is refused inside GitHub Actions: a workflow that could deploy any published tag
without the migration would be a second, weaker road.

**With the demo on, only to a commit that reads the flag.** Until 2026-10-05 this said "to any
commit whose images are published". `git merge-base --is-ancestor e5107f0f '<the full SHA to go
back to>'` must exit 0, as step 22 reads it for the images that run. Images older than that
commit neither close registration nor tag the page. The script would move the app to them all
the same: its smoke test then waits five minutes for the tag, fails, and puts the app back, and
from the moment those images answer until the put-back's revision does, registration is open
beside the pool. One registration that commits makes every later run of the pool job exit 13,
and the one road from there is a new database
([Turning the demo back](#turning-the-demo-back)). Read in the script's smoke test; not
provoked.

**The schema has no road back.** `migrate` refuses a database that is ahead of the build, so an
older commit cannot be deployed through the workflow once a newer migration has run; `--app-only`
can put older images on a newer schema, and that works only if the migrations in between left the
schema usable by the older code. Beyond that there is only a point-in-time restore into a second
database, which is a second daily charge while both exist.

### A migration nobody can read

A failed migration whose text is not in the workspace: the cap was reached, or the run was too
short to leave a line.

1. Read the cap (`python infra/deploy.py --job-log` says it first). If the workspace is taking
   lines, start the job again as the owner
   (`az containerapp job start --name azurebank-migrate --resource-group azurebank-demo`):
   `migrate` is safe to run again, and this time its lines are read.
2. Otherwise run `migrate` from this machine, from the checkout at the deployed commit, signed in
   as the owner.

```powershell
git switch --detach '<the deployed SHA>'
./infra/sql-principals.ps1                   # first: it stops, and so do you, if the database holds any code
$server = az sql server list --resource-group $group --query '[0].name' --output tsv
$fqdn   = az sql server list --resource-group $group --query '[0].fullyQualifiedDomainName' --output tsv
# Connect Timeout=60: with the tool's own 10 s the driver had no token in time (measured, below).
$env:ConnectionStrings__DefaultConnection = "Server=tcp:$fqdn,1433;Database=AzureBank;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60"
try {
    # The first try is refused by the firewall, and its last line names this machine's address.
    dotnet run --project backend/tools/AzureBank.Seeder --configuration Release -- migrate --wait-seconds 0
    $address = Read-Host 'The address the server named'
    az sql server firewall-rule create --resource-group $group --server $server --name owner-migrating-by-hand `
        --start-ip-address $address --end-ip-address $address --output none
    # A new rule can take minutes to work: migrate waits for it.
    dotnet run --project backend/tools/AzureBank.Seeder --configuration Release -- migrate --wait-seconds 300
} finally {
    az sql server firewall-rule delete --resource-group $group --server $server --name owner-migrating-by-hand
    Remove-Item Env:ConnectionStrings__DefaultConnection
    az sql server firewall-rule list --resource-group $group --server $server --query '[].name' --output tsv
}
```

Four things to know about this road:

- **It signs in as the administrator**, with more rights than the migrator, and it is the one
  exception to "run nothing else as administrator in this database". That is why the users script
  runs first: its first check refuses a database that holds a trigger or a module.
- **It is the source at that commit, not the image.** The image has no `az` inside, so
  `Active Directory Default` has nothing to sign in with there, and a managed identity does not
  exist off Azure.
- **It ran once, at step 19 on 2026-10-03, with nothing to migrate, and not as it was first
  written.** Until that day this line said "It has not been run", and the string had no connect
  timeout of its own. With `Active Directory Default` the driver walks several credentials and
  is expected to take the `az login` session; which one it took was not read. Within the tool's
  own connect timeout of 10 s it got no token: the first try ended after 11 s without reaching
  the server, with error 0 of class 20, "DefaultAzureCredential failed to retrieve a token from
  the included credentials", and exit 1. The `az` session itself was fine: asked directly,
  `az account get-access-token` for the database answered in 929 ms and in 986 ms. That was a
  diagnosis, with a command this file's rules forbid; what was recorded of it is its exit code,
  its time and whether a token came back.
- **With `Connect Timeout=60` in the string it works.** A keyword in the connection string wins
  over the tool's setting, and the tool's first line then read a connect timeout of 60 s. The
  first try reached the server after 13 s and, run again, after 8 s, and got the firewall's
  refusal, which names the address (error 40615, class 14). With the temporary rule in, the
  second try ended with exit 0 after 11 s: no migration pending, the database at 16 of 16,
  `migrate` took 9.4 s. The rule was deleted and the list read back `AllowAzureServices` alone.
  Before it, the users script had run on that database, which held the 16 migrations, and ended
  with its two lines. Why the driver needs longer than `az` for the same session was not looked
  into, and the road has not been run with a migration to apply.

## If something was stolen

There are no passwords to change. If code may have run as one of the two database identities (a
deployment nobody ordered, an image nobody built, an identity on a resource it does not belong
to), the steps are these, in this order. They have not been rehearsed.

1. **Delete the app and both jobs.** With them go the app's secrets, the pool job's two, and the
   three places an identity can be used from. The pool job is one of them: it carries
   `azurebank-app`, the app's connection string and the PIN pepper, and the deployment identity
   can write it and start it. Until 2026-10-05 this step named the app and one job. First, and
   kept: `Show-Executions azurebank-pool`, with its control, as in
   [Turning the demo back](#turning-the-demo-back): the control cannot be taken afterwards,
   since the next lines delete the job it reads. Whether the pool job has ever run decides
   step 4, and once the job is deleted its executions are not expected to be readable: a job
   that was deleted and made again is expected to show only its own.

   ```powershell
   # Kept. Nothing printed means this job never ran: it says nothing of a job of that name that
   # was deleted before it.
   Show-Executions azurebank-pool
   Show-Executions azurebank-migrate            # its control: this one must print its executions
   az containerapp delete --name azurebank --resource-group $group --yes
   az containerapp job delete --name azurebank-migrate --resource-group $group --yes
   az containerapp job delete --name azurebank-pool --resource-group $group --yes   # if the demo was turned on
   ```

2. **Delete the two identities and make them again**, under the same names: each then has new
   IDs. They are the resources the template makes, made now so that the next step does not wait
   for a run of the template.

   ```powershell
   'azurebank-app', 'azurebank-migrate' | ForEach-Object {
       az identity delete --name $_ --resource-group $group
       az identity create --name $_ --resource-group $group --location italynorth --output none
   }
   ```

3. **Drop both database users: run the users script, and read what it prints.** The ID each user
   stores now matches no identity, so the file runs `DROP USER` on both and creates them again
   for the new identities, in its one guarded transaction. The drop is what refuses a token taken
   earlier at its next sign-in (reasoned, not seen). A token is valid for a time the documentation
   read does not state, and the platform caches tokens for about a day.

   ```powershell
   ./infra/sql-principals.ps1
   ```

   The drop goes through the file, never through a `DROP USER` typed as administrator: a trigger
   left by whoever held the schema-changing identity would run as the administrator on that very
   statement, and the file is what refuses a database that holds one. If the script stops with
   `Msg 50003` or `Msg 50004`, the database itself was changed: run nothing else there.

4. **Run the template with the app**, on a commit whose images are known. With no app deployed,
   `secrets.ps1` generates the eight application secrets anew (seven until 2026-10-05).

   **With no app deployed, nothing remembers the demo's switch.** The script reads it from the
   app's two containers, and the app is gone: without `-DemoOn` its report says
   `demo: not written, the template's default applies`, and the app comes back with both flags
   off. On a database that holds the pool that is the state
   [Turning the demo back](#turning-the-demo-back) forbids: registration open beside the pool.
   The script cannot know what the database holds and does not guess. **So if a run of the pool
   job was ever listed on this database (step 1's read, or a read kept before an earlier deletion
   of the job), pass `-DemoOn`**, which also brings the pool job back. Where the demo was turned
   on and no such read was kept, pass it too: with it registration stays closed.

   ```powershell
   # First: no deployment may be queued, waiting or running (Deploy a commit, "One deployment at a time").
   gh run list --workflow deploy.yml --limit 5 --json status,displayTitle,createdAt --repo Gurgant/azurebank-v2
   try {
       ./infra/secrets.ps1 -Action New -DeployApp -ImageTag '<a known commit>'            # no run was ever listed
       # ./infra/secrets.ps1 -Action New -DeployApp -ImageTag '<a known commit>' -DemoOn  # one was, or no read was kept
       Invoke-Template 'after-a-theft'
   } finally {
       ./infra/secrets.ps1 -Action Remove
   }
   ```

   **Not designed: what the new secrets mean for the rows already there.** The new PIN pepper is
   not the one the copies in the database were built with, so no copy built before takes a PIN,
   and the new audit keys do not continue the chain the old ones wrote. Nothing in this folder
   repairs either. The road known to work is a new database, by the owner's own hands, in the
   order [Turning the demo back](#turning-the-demo-back) gives for it: it starts a new chain and
   loses every row.

5. **Look at who could run a job in `demo`** ([What each identity can do](#what-each-identity-can-do)),
   then deploy, and with the demo on `python infra/deploy.py --check`.

While an intruder can still run code in the app or in a job, a new token is one request away:
that is why step 1 comes first. The app and the jobs are deleted, and not put back on known
images, because nothing else in these files makes the eight application secrets anew.

## Where each secret lives

| Secret | Lives in | Who can read it |
| --- | --- | --- |
| JWT secret, idempotency key, step-up key, service key, audit chain key, audit anchor key, PIN pepper | The secrets of the app `azurebank` | The owner, by listing. The deployment identity cannot list them, and can still reach them by running code: see [What each identity can do](#what-each-identity-can-do) |
| The demo's client key (`demo-client-key`), since 2026-10-05 | A secret of the app, referenced by the `api` container only. Under it the API stores the address a copy was claimed from as a hash, never the address. It is there whether the demo is on or off, and used only when on | The same |
| The app's connection string | A secret of the app, referenced by the `api` container only | The same. It holds the server's name and a client ID, and no password |
| The migration connection string | The secret of the job `azurebank-migrate` | The same. No password |
| The pool job's two, with the demo on: a copy of the app's connection string (`app-connection`) and a copy of the PIN pepper (`pin-pepper`) | The secrets of the job `azurebank-pool`, which the template writes from the two expressions it writes the app's from. `secrets.ps1` asks nothing of a job | The same. `--check` lists them beside the app's, as the owner, to say whether they are equal, and shows neither |
| A database password | **Nowhere: none exists.** The server takes Microsoft Entra sign-ins only | Nobody |
| `parameters.json` | `%LOCALAPPDATA%\AzureBank\deploy` (elsewhere `~/.azurebank-deploy`), open to its owner only, for the minutes of a session. Without `-DeployApp` it holds no secret | Removed in `finally` |
| In GitHub | The three Azure identifiers, as secrets of the environment `demo`. No application secret | A job in `demo` |

The two connection strings are secrets although they hold no password. An identity can be used by
every container of the app, so the string is kept out of the BFF, which faces the internet: that
container is handed neither the server's name nor the client ID. This is not a lock, because
neither is a secret ([What each identity can do](#what-each-identity-can-do)). A secret also stays
out of every read of the app and of an execution, which a plain setting would not.

`secrets.ps1 -Action New` never generates a value twice: each of the eight application secrets
comes from the deployed app if it exists, else from a file left by a run that stopped, else from
the system's random generator. A failed read is never taken for an absent value: the script stops
and writes nothing. One exception, for one name: the demo's client key is generated for a
deployed app that lacks it while that app's demo is off, because the app the first deployment
created never needed it. A deployed app whose demo is on and which lacks the key stops the
script, as a missing one of the seven does. Until 2026-10-05 this paragraph counted seven and
had no exception. `-Action Remove` deletes `parameters.json`, `what-if.json` and `budget.json`
by name, then the folder if it is empty, and nothing else.

Rotating an application secret is not in these files, short of
[If something was stolen](#if-something-was-stolen).

## What each identity can do

**The deployment identity, `azurebank-deploy`.** The custom role lets it read and write the app
and each job it is assigned on (the migrate job, and with the demo on the pool job: three
assignments, where there were two until 2026-10-05), start such a job, and read executions,
revisions and replicas. It cannot list secrets,
delete or stop anything, or touch the environment, the database server, the workspace, any
identity or any role. It cannot read the logs. It has no user in the database. The Deny policy on
the resource group is the one guard it cannot change.

Writing the app is more than moving an image. Within what the policy allows, whoever acts as this
identity can:

- run any public image, with any command, with every secret of the app in its environment, so the
  secrets can leave in one request;
- run that code as the app's database identity: read and write every row;
- overwrite a secret with a value of their choice;
- change the ingress: another target port, more port mappings, address rules;
- run any image in the migrate job, as the schema-changing identity. So, by running code in that
  job, it can do everything the migrator can do, and **it can leave code in the database that
  runs as whoever next changes users there**: the users file refuses to run on such a database;
- with the demo on, run any image in the pool job and start it at will, as the app's database
  identity, with the connection string and the PIN pepper in its environment. That reaches
  nothing the first two lines do not already reach through the app. A start is also expected to
  take a body that changes the container for that one execution: `deploy.py` sends none, a test
  holds that, and nothing on Azure refuses one (not measured; step 31 sends one on purpose, as
  the owner);
- change the pool job's schedule or its timeout. **Nothing on Azure polices either**: the policy
  has no rule on a schedule's expression. `deploy.py` reads both at a deployment and at a
  `--check`, and nothing reads them between two of those.

It is not expected to be able to attach an identity to a resource: Azure asks for a right on the
identity itself (`assign/action`), which this role does not hold and which is never given to it.
That refusal has not been provoked.

The policy refuses: a second replica, a minimum above zero, several active revisions, plain HTTP, a
third container, any init container, a container above half a vCPU; and for a job, a trigger other
than Manual (but `Schedule` on a job named in `scheduledJobs`, which is `azurebank-pool` and no
other), parallel runs under any trigger, any init container, a container above half a vCPU. So
whoever puts a job of another name on a schedule, the migrate job among them, or gives the pool
job two runs at once, is expected to be refused. Neither refusal has been seen: step 29 makes
both due. It
does not limit how many containers a job has, and it has no rule about identities: the provider
publishes no policy alias for the identity block of an app or of a job (measured: 0 of 1,058).

So the boundary is **who can run a job in the environment `demo`**: its branch rule (`main` only),
its required reviewer, and whoever holds a GitHub token that can edit the environment or merge to
`main`. Any workflow file in this repository that names `environment: demo` gets the same identity;
the subject of the federated credential names the environment, not the workflow.

`deploy.py`'s shape check catches drift (a click in the portal, a half-applied change). It does not
stop someone who can edit the script; the policy does.

Images are deployed by tag. `build-push` never overwrites a tag, but someone with push rights to
the packages can; deploying by digest would close that and is not done here.

**The app's identity, `azurebank-app`.** In the database it reads and writes every row and cannot
change the schema (in the trial, a user with these two roles was refused `CREATE TABLE` with error
262 and `ALTER TABLE` with 1088). That includes the rows of the migrations history table, by which
it could steer the next migration: a `DENY` on that table is not set. It holds no role on any
Azure resource.
**It is available to both containers of the app, and with the demo on to the pool job.** Code
running in the BFF, which faces the internet, can therefore ask for a database token. Between
that code and the database stands only what the BFF is not handed: the server's name and the identity's client ID. Both are identifiers,
not secrets, so neither is a barrier to count on. Microsoft's page says a request for a token
must name the identity, and that one which names none is answered for a system-assigned identity,
which the app does not have; whether such a request is refused here has not been seen. With
passwords only the `api` container held the credential. The real fix is the API in an app of its
own, and it is not done here.

**The migration's identity, `azurebank-migrate`.** The same, and it may change the schema. It
cannot create a user or make itself an owner directly (measured on a local engine: both refused;
on Azure SQL, in the trial: `CREATE USER` refused with error 15247, and thirteen kinds of
statement that migrations use allowed). It can create a trigger, which is why the users file is
guarded.

**Who can act as either:** code running in any container of the app or in the pool job (the
app's identity), in the migrate job (the migration's), in the probe job of the first session
while it exists, or in any resource the owner attaches the identity to. In
the trial a job that carried one identity got no token for the other, tried both ways.

**The owner's account.** It is the only administrator of the database and the only Owner of the
subscription, and a university issues it. If that account is disabled, the app keeps running and
only a Global Administrator of the directory can stop it. Owner and Contributor can switch
Entra-only off; nothing here ever does.

## Who can reach the database server

The firewall rule `AllowAzureServices` admits every address Azure owns: any customer's virtual
machine, function or container app in any tenant, and GitHub's hosted runners. The app's own
outbound address cannot be pinned in a Consumption-only environment, so the rule cannot be
narrower.

What holds the line is the sign-in. The server refuses every password (in the trial a
SQL-password sign-in was refused, with Entra-only authentication named as the reason), so there is
none to guess or to steal: what opens the database is a token for one of the two identities, or the
administrator's own sign-in. Sign-in attempts are still recorded nowhere: there is no SQL auditing,
and the workspace holds what the containers print, not what the server sees.

The temporary rule `owner-while-creating-users` exists only while `sql-principals.ps1` runs. If the
terminal is closed or the machine sleeps, it stays until the next run of the script, which deletes
it first, or until someone reads the rule list: every session starts by reading it (step 1). A
rule neither starts nor ends at once. In the trial a new rule let this machine in after 19 s
(twice), and about 20 s after a rule had been deleted and read back as gone, the server still
accepted a new connection from the same address.

## What only the owner does

1. `az login` at the start of each session, and `gh auth login`: the browser and the second factor.
2. The GitHub environment `demo`: `main` only, and the required reviewer (step 11).
3. A package to Public, only if the anonymous check of step 14 answers `denied` for it. It
   cannot be undone. On 2026-10-03 it was not needed: the three packages were public as soon as
   the workflow had published them. Until then this line had the owner set each of the three.
4. The merge of the pull request that puts the workflow on `main`, and the required checks.
5. The approval of each `deploy` run, if the reviewer is set.
6. Reading the mailbox: at step 15 the message that asks to verify the address, with its
   one-time code, and the one that follows the verification; every alert afterwards.
7. Stopping the app when an alert says so. Nothing does it for him.
8. The word for each step that writes, and for each deletion that a rule above allows: the
   workspace and its setting, and the pool job where step 25 or
   [Turning the demo back](#turning-the-demo-back) says so.
9. With the demo on: the sign-in under which `--check`, `--pool-run` and `--pool-log` run, since
   all three are refused inside GitHub Actions; the stop and the start of the app; and, if the
   demo is ever turned back after the pool job has run, the lock and the database, by his own
   hands.
10. For the alerts on his phone (step 25; since 2026-10-06): the Azure mobile app installed,
    signed in and allowed to notify; the address he signed in to it with, typed at the top of
    the step; and the look at the phone, and at the mailbox, when the test notification is sent.

## Before renaming or transferring the repository

Delete the federated credential first:

```powershell
az identity federated-credential delete --identity-name azurebank-deploy --resource-group azurebank-demo --name github-demo
```

The credential trusts the repository by name. After a rename or a transfer, whoever then owns
`Gurgant/azurebank-v2` could sign in as the deployment identity.

## Changing the infrastructure later

Edit the template, then run it the same way. Without `-ImageTag` the parameter file takes the tag
the app runs now, the eight secrets it holds now, the address its alerts write to now, the
account of the Azure mobile app they notify now if there is one, what its environment does with
its logs now, whether its two containers say the demo is on now and the networks of proxies its
`bff` container believes now if there are any, so the run leaves all seven alone (until
2026-10-05: seven secrets, and four things; until 2026-10-06: five, and later that day six):

```powershell
# First: no deployment may be queued, waiting or running (Deploy a commit, "One deployment at a time").
gh run list --workflow deploy.yml --limit 5 --json status,displayTitle,createdAt --repo Gurgant/azurebank-v2
try {
    ./infra/secrets.ps1 -Action New -DeployApp
    Invoke-Template 'change'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

- **The first run of the template after 2026-10-05 changes the app, whatever the run is for, and
  with the demo still off.** Expected in its what-if: `Modify` on the app, for a ninth secret and
  four settings (`Demo__Enabled`, written as `false`, on both containers; `Demo__ClientKeySecret`
  and `Demo__Claim__MaxPerClientPerDay` on `api`), and the script's report says
  `demoClientKeySecret: generated`. A change of the app's template is expected to make a new
  revision, and a new revision ends every session held in the replica's memory. Also expected,
  in a run without the app too: `Modify` on the role definition, whose description changed, and
  on the policy definition and its assignment. Nothing is created and nothing deleted by it:
  offline, a run with the switch off predicts the 22 resources it predicted before, and 14
  without the app ([Checking these files](#checking-these-files)).
- **The demo's switch is passed once.** `-DemoOn` at step 25; afterwards the script reads
  `Demo__Enabled` on the app's two containers and writes what they say, and its report says
  `demo: kept from the deployed resource`, on or off. Two containers that disagree stop it, and
  so does a container that carries the setting any other way than the template writes it. With
  no app deployed nothing remembers the switch
  ([If something was stolen](#if-something-was-stolen)). **Never `demo=false` as an override once
  the pool job has an execution**: [Turning the demo back](#turning-the-demo-back). A run with
  the switch off does not delete a pool job that exists; it is expected to leave it, with its
  schedule.
- **The phone's account is passed once, too.** `-AlertPushAccount` at step 25, or in a run of
  this section if step 25 went without it: the step's three things by the owner's hands first,
  then its one test notification. Afterwards the script reads the action group's one receiver
  of the Azure mobile app and writes its account back, and its report says
  `alertPushAccount: kept from the deployed resource`. With no such receiver it says
  `alertPushAccount: not written, the template's default applies`, and the run asks for none.
  Two such receivers stop it, since the template writes one. **To take the phone out** there is
  no switch: the receiver is removed from the group by hand, in the portal or with
  `az monitor action-group update --name azurebank-owner --resource-group $group --remove-action owner-phone --output none`,
  and the next run then finds none, unless `AZUREBANK_ALERT_PUSH_ACCOUNT` is set in the
  terminal. The command is read in Microsoft's reference
  (<https://learn.microsoft.com/en-us/cli/azure/monitor/action-group>, read on 2026-10-06) and
  has not been sent.
- **The networks of proxies are passed once, too** (added on 2026-10-06; no run has passed
  any). `-ProxyNetworks` at step 30, if that step's measurement asks for them. Afterwards the
  script reads the `bff` container's settings `ForwardedHeaders__KnownIPNetworks__0` and on and
  writes the same list back, and its report says
  `proxyNetworks: kept from the deployed resource`. With none it says
  `proxyNetworks: not written, the template's default applies`, and the app believes no
  forwarded header. **To take them out** there is a word: `-ProxyNetworks none` writes an empty
  list whatever the app holds, the report says
  `proxyNetworks: none, asked for with -ProxyNetworks`, and the run takes the settings out
  (step 30, "The way back"). An entry the app would not take for a network stops the script,
  named by its place in the list and never by what it holds. So does a forwarded-headers
  setting on the `bff` container that the template never writes, when no network is named:
  it was set by hand, and a run would take it out in silence.
- Images move through the `deploy` workflow only: once the app exists, `secrets.ps1` refuses
  another `-ImageTag`. So a run of the template creates the pool job on the tag the app runs.
- The server is not changed by a later run: step 3 is where that is seen for the template. The
  `administrators` block is never edited in place.
- The environment's mode is read again (`Assert-EnvironmentMode`) at the start of every session,
  with the reads of step 1. Microsoft's FAQ says that an environment whose features Express
  supports may be moved to it after a notice, and that one with no running app or job and no
  recent activity may be archived; this one has jobs, a second container and Azure Monitor logs,
  which Express does not have (read on 2026-10-03).
- If the policy was left out because Azure refused it, the override that left it out is passed
  again: `Invoke-Template 'change' @('denyPolicy=false')`. The file does not remember it
  ([If Azure says no](#if-azure-says-no)). The alert on the workspace needs no override to stay
  out: since step 20 it is built only with `@('logVolumeAlert=true')`.
- A job that must run on a schedule is named in the parameter `scheduledJobs`, which holds
  `azurebank-pool`, or the policy refuses it. `allowedJobTriggers` holds for every job and stays
  `Manual`: a trigger added there is allowed to the migrate job too. Until 2026-10-05 this line
  sent a job with another trigger to `allowedJobTriggers`.
- The pool job's schedule is a variable of the template and not a parameter: an override could
  make the interval shorter than the job's timeout, and two runs at once can build up to twice
  the pool's target (`backend/tools/AzureBank.Seeder/README.md`). Its timeout is the parameter
  `poolTimeout`, 60 to 840 s, and a test holds the interval longer than the longest of them. An
  override of it is not remembered: `secrets.ps1` writes no timeout, so a later run without it
  puts 600 back, and that run's what-if shows the `Modify` on the job.
- After an identity was deleted and made again, run `./infra/sql-principals.ps1`: the user it left
  matches nothing and is replaced.

## Turning the demo back

Written on 2026-10-05. **None of it has been run.** There is no switch that turns the demo off,
and which road exists depends on one read: **whether the pool job has an execution, in any
state.**

```powershell
Show-Executions azurebank-pool               # the read
Show-Executions azurebank-migrate            # its control: this one must print its executions
```

Not "whether step 26 has run": the job fills the pool by itself at its next run. And the control
comes with it, so that a silence is the function's answer and not its failure.

**The read holds only for a job that was never deleted.** A pool job that was deleted and made
again is expected to have no execution of its own, while the roles and the copies an earlier run
wrote are still in the database. After any road that deleted the job once a run had been listed
(below, "If it is the job itself that must not run again";
[If something was stolen](#if-something-was-stolen)), nothing printed is not the line: what
counts is the read that was kept before that deletion, and the roads are the two further down.

**Why an execution is the line.** The first run of the pool job creates the two roles a
registration needs. Before it, a registration on this database cannot commit
([Not measured yet](#not-measured-yet) has the local measurement). After it, an app whose flags
are off registers whoever asks, beside the pool: every later run of the job then exits 13, no
file here removes a user, and `seed` and `reset` refuse a database that holds a pool row. With
the flags off the sign-in gate also refuses nobody (ADR-0063, decision 10), so the owner of a
claimed copy signs in past its time.

**While the pool job has no execution: the switch may go off, the job first.**

1. The read above prints nothing for the pool job, and the control prints. Both are kept before
   anything is deleted: once the job is gone, its executions are not expected to be readable.
   **An execution, in any state, ends this road, and so does one that was listed before an
   earlier deletion of the job:** the roads are then the two further down.
2. On the owner's word, `az containerapp job delete --name azurebank-pool --resource-group $group --yes`,
   and its absence is read back: one job in the list, and two role assignments, since the third
   is expected to go with the job.
3. On the owner's word, one run of the template with the switch off:

   ```powershell
   # First: no deployment may be queued, waiting or running (Deploy a commit, "One deployment at a time").
   gh run list --workflow deploy.yml --limit 5 --json status,displayTitle,createdAt --repo Gurgant/azurebank-v2
   try {
       ./infra/secrets.ps1 -Action New -DeployApp
       Invoke-Template 'demo-off' @('demo=false')
   } finally {
       ./infra/secrets.ps1 -Action Remove
   }
   ```

   The what-if must show `Modify` on the app and **nothing to create for a job**. A job to create
   means the override was not taken: the file itself says `demo: kept from the deployed
   resource`, which is on. Answer "no". That a value given after the file wins over the file's
   is how `denyPolicy=false` is passed too, and the what-if shows it before anything is sent.
4. `python infra/deploy.py --check`: the demo is off, and the page carries no tag. The client key
   stays, as a secret nothing uses.

**Never step 3 while the job exists.** A run with `demo=false` does not delete it: a run of the
template removes nothing that exists, as [Switching the logs off](#switching-the-logs-off) says
of the workspace. And a scheduled job whose own flag is the plain word `true`, beside an app
whose flags are off, is the state ADR-0063 warns of: visitors register beside the pool and every
run exits 13.

**Once the pool job has an execution: two roads, and no third.**

| Wanted | Who | What is done | Read back |
| --- | --- | --- | --- |
| Nobody reaches the demo, within minutes | The owner's sign-in only: the deployment identity can neither stop nor start the app | [Stop the app by hand](#stop-the-app-by-hand). The job goes on by its schedule, and harms nothing while nobody is served | The app's state, by the read step 32 recorded; what the address answers |
| The demo off again, on a new and empty database | The owner's own hands for the lock and the database; the operator for the rest, each on the owner's word | The six steps below, in this order and no other | Each step's own |

1. **The pool job is deleted first**, on the owner's word, as in step 2 above. Read back: one job,
   two role assignments. No run can now fill any database. The app still answers as the demo and
   hands out the copies that are free.
2. **The owner removes the lock `keep-the-database` and deletes the database**, by his own hands.
   Read back: the database is gone from the server's list, by name. The app now answers errors,
   and the BFF still refuses a registration, because both flags are still on.
3. **One run of the template with the app and `demo=false`**, on the owner's word: the block of
   step 3 above, as `Invoke-Template 'as-before' @('demo=false')`. The what-if must show the
   database and its lock to create, `Modify` on the app, and nothing to create for a job. It makes
   the empty database again and turns both flags off while no pool row exists anywhere. Read
   back: `Demo__Enabled` on both containers, `false`; one job.
4. `./infra/sql-principals.ps1`: its two lines (step 6).
5. The workflow's `deploy`, approved by the owner. With the demo off the script reads no pool job;
   the migration applies every migration to the empty database; the smoke test sends its three
   requests. Read back: the verdict and its exit code.
6. `python infra/deploy.py --check`: no tag, and no job read. `Show-Identities`: two lines.

The app is not stopped for this road. Every state on the way fails closed or writes nothing:
flags on with no database answers errors, and flags off with no schema can write nothing. And
what a run of the template does to a stopped app has not been seen. The audit chain starts again
with the new database. The app's address is expected not to change, since neither the app nor
its environment is made again: not measured.

**What no road here takes back.** Neither the switch going off nor the six steps leave the
deployment as it was before the demo. The policy keeps its exception for a job named
`azurebank-pool`, with its new name and its description: the exception is the template's default
and is not behind the switch. Only step 23's way back takes it out, and that override has to be
passed again at every later run of the template. The role definition keeps its new description.
The app keeps the ninth secret and the demo's four settings, the flag written as `false`. While
no pool job exists, the exception serves only whoever may create a job in the resource group,
which the deployment identity may not: its role is assigned on resources that exist.

**Never `demo=false` while the pool job exists, and never while the database holds a pool row.**
The first leaves the scheduled job beside an app whose flags are off. The second reopens
registration on a database that now has its roles.

**"The job deleted and the app left up" is not a third road.** In that state this folder's own
tools refuse it or undo it. Every workflow deployment stops at the pool job it cannot read, a
security fix included, and only `--app-only`, which runs no migration, still moves the app. The
next run of the template, for any reason, creates the job again without being asked: the script
writes the switch it reads from the app, and the job is built whenever the app and the switch
are. The free copies, up to 50, are still handed out. And nothing sweeps expired grants or
deletes the copies whose time is over: those are `recycle`'s to do
([`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md)). So the job is deleted only as the
first step of a road that ends with the demo off.

**If it is the job itself that must not run again** (its image, its cost, a run that
misbehaves): stopping the app does not stop the job. So the app is stopped first, by the owner,
and then the job is deleted, on his word, which is step 1 of the six. With the app stopped the
change may wait there: nothing runs and nothing is handed out. The ways on are the rest of the
six, with the app started again before the third, or the start and then step 25's commands
again, which bring the job back.

**If the first run of the pool job exits 13** (a user outside every copy, on a database with no
pool row): stop. Nothing is deleted by the operator: no file here removes a user, and nothing
but the users file is run as administrator in that database. The app stays on with an empty
pool, which fails closed. The one road the records name is the six steps above, and the deletion
in it is the owner's own.

## Removing everything

In this order. The role definition and the policy definition are not inside the resource group
and are not deleted with it. The role can be assigned in this group only, so it is removed first,
through the group, while the group still exists: its assignments, two or with the demo on three,
then the definition. The policy definition is found by the first words of its name, which the
name the first deployment gave it and the one of 2026-10-05 share. Whether
it could still be found once the group is gone is not measured: the trial deleted its role first,
as here, and then found it neither by its ID nor in the lists. The workspace is deleted on its
own, with `--force`, if what it holds must be gone at once: that a workspace deleted with its
group is kept 14 days like one deleted alone is a reading, not something a page says. The trial
made its own deletions through `az rest`, in this order (the database, the workspace for good,
then the group, which took 27 minutes to go). None of the commands below has been run yet.

```powershell
$group = 'azurebank-demo'
az lock list --resource-group $group --query '[].id' --output tsv | ForEach-Object { az lock delete --ids $_ }
az monitor log-analytics workspace delete --resource-group $group --workspace-name azurebank-logs --force --yes
$principal = az identity show --resource-group $group --name azurebank-deploy --query principalId --output tsv
az role assignment list --assignee $principal --all --query '[].id' --output tsv | ForEach-Object { az role assignment delete --ids $_ }
az role definition list --custom-role-only true --resource-group $group --output json | ConvertFrom-Json |
    Where-Object roleName -like 'AzureBank deploy *' |
    ForEach-Object { az role definition delete --name $_.name --resource-group $group }
az group delete --name $group
az policy definition list --output json | ConvertFrom-Json |
    Where-Object { $_.policyType -eq 'Custom' -and $_.displayName -like 'AzureBank: one small replica*' } |
    ForEach-Object { az policy definition delete --name $_.name }
az consumption budget delete --budget-name azurebank-monthly
'AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID' | ForEach-Object { gh secret delete $_ --env demo --repo Gurgant/azurebank-v2 }
```

Then, in GitHub's settings: the environment `demo`, the three packages, the workflow runs. The
database's daily charge stops when the database is gone. The three identities and the two database
users go with the group. On this machine, if it is no longer wanted:
`winget uninstall --id Microsoft.Sqlcmd`.

## What is not here

- **Nothing stops the app automatically, and nothing stops the logs.** Three alert rules warn,
  all on the app, by e-mail and, once step 25 has given the action group his account of the
  Azure mobile app, on the owner's phone; the owner stops the app and says when the logs go off.
  A warning on the phone stops nothing either. Until 2026-10-06 this line said "by e-mail" alone.
- No alert on the log's volume and none that its cap was reached. The metric rule that was meant
  for the volume did not count lines and was deleted on 2026-10-03 (step 20); a rule that does
  is a log search rule, which is billed, and the owner decided against one for now.
- No SQL auditing: a sign-in attempt on the server leaves no record.
- One warning line per caller per window from the rate limiter, instead of one per request. It is
  the change that would let the app's own limiter bound the log, and it touches the BFF's security
  logging.
- The API in an app of its own, so that only it can ask for a database token.
- No alert on changes to the role assignments, the federated credential or the job.
- No deployment by image digest; no rollback of the schema.
- No rotation of application secrets, no Key Vault, no private endpoint, no custom domain.
- No demo data: the database a first deployment leaves has a schema and no rows, and on Azure the
  app goes live on it with its registration open, reachable by anyone who has its address. Open
  is not usable there: until something has created the roles, a registration is expected to
  answer 500 and to leave no row, as it did on a local stack
  ([Not measured yet](#not-measured-yet)). Until
  2026-10-05 this line also said "No scheduled job" and "The app's registration endpoint is not
  closed by anything in this folder". Of the folder that is no longer so: it holds a scheduled
  job, behind the switch `demo`, and that switch closes registration on both containers. Of
  Azure nothing changed with it: none of it has run there, and the deployed app's registration
  stays open until step 25 has run.
- No switch that turns the demo off ([Turning the demo back](#turning-the-demo-back)), and
  nothing that removes a user from the database.
- No alert on a failed run of the pool job, and none on the database's size. The one rule this
  folder built on a metric nobody had seen report was created and then deleted (step 20); step
  31 reads the two metrics before anything is built on them. Until then a pool run is read on
  demand: its exit code, its line, and what a visitor is answered.
- No workflow action that starts the pool job. The job refills the pool by itself, and a start
  by hand is `--pool-run`, from the owner's terminal: one public door fewer.
- No claim of a copy by a deployment, and no command that claims one: a claim spends a copy,
  leaves audit rows, and its answer holds a copy's password. A browser proves the claim and the
  PIN (step 27); `--check` compares the two secrets that decide the PIN, and spends nothing.
- The visitor's own address behind the ingress, on Azure. What the BFF sees there is not
  measured (step 30), no run has told it which proxy to believe, and no network of this
  deployment is written in any file. Until that step has run and its proof has passed, the cap
  of claims for one client is 1,000 a day, which stops nobody. Until 2026-10-06 this line said
  "No file here tells the BFF to trust a forwarded header (`ForwardedHeaders:KnownProxies`)":
  the template can now tell it the networks of the ingress, when a run names them
  (`proxyNetworks`).
- A cap of claims that goes back to 10 by itself, and a second hop. Once the BFF counts a
  visitor by the visitor's own address, taking `Demo__Claim__MaxPerClientPerDay` back to its
  default is a change of the template that is not made here. And the BFF believes one hop: if
  the platform has two proxies in a row, `ForwardedHeaders:ForwardLimit` has no parameter.
- No rotation of the PIN pepper for the pool job: the template carries no key id and no previous
  pepper, on the app or on the job.
- The text of the migration is not shown by the workflow, on purpose.
- The password design this folder first had, the other two shapes of the SQL server and the
  switch that bound a user to its identity's object ID. They were written as steps down in case
  Azure refused the first shape or stored the other ID. In the trial it did neither, and none is
  a step of this runbook any more. They are in the history.

## Measured on Azure

Two parts: a trial on 2026-10-02, and, at the end of this section, the first deployment, on
2026-10-03 with this folder's own files: its first session, its second session, a later
deployment the same day, and the last reads of that day.

On 2026-10-02, in a throwaway resource group in the same subscription and region (Italy North),
created and deleted that day. Requests of the shapes this folder makes were sent by hand, with
`az rest` and go-sqlcmd (its version was not recorded). Each line below comes from a run of the
trial that was recorded as it went, but one: the environment request that named its mode, sent
between two recorded runs, and its line says so. **None of this folder's files ran.** The
resources were the trial's own: another server, other identities, other users, other jobs. Where
a request differed from the one this folder makes, the line says so, and what the template sends
and the trial did not is under [Not measured yet](#not-measured-yet). One run each, unless a line
says otherwise.

**The SQL server and the owner's sign-in**

- A server that takes Microsoft Entra sign-ins only, created with the `administrators` block and
  no SQL administrator login (API version `2023-08-01`, the properties `main.bicep` sends):
  accepted, the operation done after 57 s, and read back as Entra-only. The service gave it a SQL
  administrator name of its own making; no password was sent for it.
- The same request a second time: accepted, done at once, and the server read back exactly as
  before. The administrator and the Entra-only switch, sent once more as child resources: both
  accepted.
- The database, Basic, 5 DTU, 2 GB, local backups: the operation done after 56 s, and online.
- go-sqlcmd signed in as the owner through the `az login` session (`ActiveDirectoryAzCli`): it was
  `dbo`, a member of `db_owner`, and the server said it takes Entra sign-ins only. The first try
  was refused for this machine's address, and a firewall rule for that address let it in after
  19 s.
- A SQL-password sign-in for a login that does not exist: refused, exit 1, "Login failed for user
  ... Reason: Azure Active Directory only authentication is enabled."
- The temporary firewall rule was deleted and read back as gone. About 20 s later the server still
  accepted a new connection from the same address.

**`CREATE USER`, and which ID a user carries**

- `CREATE USER [<the identity's name>] FROM EXTERNAL PROVIDER`, run by the owner, who holds no
  directory role: accepted. **The ID the server stored is the identity's client ID**, as 16 bytes.
- `CREATE USER ... WITH SID = <the client ID as 16 bytes>, TYPE = E`, the form this folder uses:
  accepted for both users, and the database's own comparison of each stored ID with the client ID
  said they match.
- So a user is bound to the client ID, and this folder has no switch for the object ID. The second
  form this folder keeps, `FROM EXTERNAL PROVIDER WITH OBJECT_ID`, was not sent: the trial looked
  the identity up by its name.

**A sign-in as each identity, from Container Apps jobs**

A throwaway program on Microsoft.Data.SqlClient 6.1.1, the app's version, in a job at 0.5 vCPU and
1 GiB, with a string of the app's shape: the managed-identity sign-in, the client ID as `User ID`,
and a connect timeout of 10 s.

- Each identity signed in as its own user: a reader and a writer, `db_ddladmin` for the migrator
  only, neither of them an owner.
- **The first `Open()` on a cold replica took 3,810 ms**, token included, against the 10 s
  timeout. One measurement. The next open, from the pool: 0 ms. A new login with the token already
  held: 104 ms. In the other job, where the token endpoint had been asked once when the container
  started, the first open took 1,065 ms.
- The app's identity was refused a change of schema: `CREATE TABLE` with error 262 (class 14),
  `ALTER TABLE` with error 1088 (class 16). It was allowed an application lock inside a
  transaction, a locked read and an update.
- The migrator's identity was allowed the thirteen kinds of statement tried (the lock and its
  release, the history table, creating and dropping tables, an index, a column and a check
  constraint, altering a column, a data update), and refused `CREATE USER` with error 15247.
- **A token that cannot be had**, for an identity the job does not carry or for one that does not
  exist: a `SqlException` with number 0 and **class 20**, around an
  `Azure.Identity.AuthenticationFailedException`, after 40 to 71 ms (four tries). Read in the API,
  not run there: its handler
  (`backend/src/AzureBank.Api/Handlers/ServiceUnavailableExceptionHandler.cs`) answers what
  `IsUnreachable` takes as the 503 of a database that cannot be reached, and that takes a class of
  20 or above (`sql.Class >= 20`). Its first test, EF Core's own transient list
  (`IsTransientToEf`), does not take this error: EF Core SqlServer 10.0.1's detector, called on
  this machine with an error of that number and class, answered false (and true for 4060, 40613
  and 1205, three numbers that list holds).
- **The right identity signing in to `master`, where it had no user**: error 18456, class 14,
  "Login failed for user '<token-identified principal>'", after 41 to 52 ms (two tries). The trial
  did not try a database of its own with no user for the identity; for the app's database this is
  the error expected. Read in the same handler: that number is in neither its own list
  (`UnreachableNumbers`) nor EF Core's (the detector answered false for it as well), and the class
  is below 20, so the answer is a 500.
- An execution read through API version `2026-07-01` carried its status and its container's exit
  code: `Succeeded` and 0 for three runs (45, 44 and 29 s long), `Failed` and 7 for a run made to
  exit 7. The reasons were `CompletionsReached` and `BackoffLimitExceeded`. The failed one carried
  no length.

**The environment and the logs**

- An environment whose request named no mode (API version `2025-01-01`, the Consumption profile,
  logs to Azure Monitor): refused, HTTP 400, `ExpressEnvironmentFeatureNotSupported`. With
  `environmentMode: 'WorkloadProfiles'` on API version `2026-07-01`: accepted, sent by hand
  between two recorded runs, so that answer is a note made at the time and not part of the
  record. The record shows the environment afterwards, read back with the logs destination
  `azure-monitor` and the Consumption profile, and two jobs that ran in it; it does not show the
  mode read back. The offer allows one environment in the region (0 of 1 before it, 1 of 1 with
  it).
- A workspace on the pay-per-GB plan, kept 30 days, with a daily cap of 0.05 GB and key access
  off: accepted, and read back with that cap (API version `2025-02-01`; `main.bicep` uses
  `2023-09-01`). One diagnostic setting, console and system logs to that workspace: accepted.
- The jobs' console lines reached the workspace, in the table `ContainerAppConsoleLogs`, with
  `JobName` filled and the text in `Log`, and the owner read them through the log API with key
  access off: 45 lines. **The first arrived under nine minutes after the setting was created**
  (8 min 38 s), not the 90 minutes Microsoft's page allows for. A line could be read 387 s and
  398 s after it was written (the median for each of the two jobs), and 486 s at the most.
- Three runs that lasted seconds each kept their line.
- Billed: 19,732 bytes for those 45 lines, 438 a line, for 194 characters of text on average.
- **Not shown: what any of it cost.** The cost view had no row for the resource group on the same
  day (once it answered 429), so whether the free 5 GB a month apply to this offer is still open.

**The role, the policy, a budget**

- A custom role with the nine actions, assignable in one resource group: accepted and read back
  with nine actions. It was found by its ID, and in the list of custom roles, asked at the group
  and asked at the subscription; after its deletion, by neither.
- A custom policy definition at the subscription, assigned to the resource group, with the effect
  Deny: accepted. **It refused at once**: a job at 0.75 vCPU got `RequestDisallowedByPolicy` on the
  first try, within seconds of the assignment, and a job at 0.5 vCPU was accepted. The rule sent
  was an earlier one than this folder's: it had every condition `guardrails.bicep` had on
  2026-10-03 but four (an init container on an app, an init container on a job, parallel runs
  under a schedule trigger and under an event trigger). Those four were first sent in the
  definition step 2 of the first deployment created on 2026-10-03; none of them has been seen
  refusing. The exception by name that the file has held since 2026-10-05 was in neither rule
  and has not been sent to Azure. Until 2026-10-05 this line said "every condition of
  `guardrails.bicep`".
- A budget on the subscription, 20 a month, with four e-mail notifications to one mailbox:
  accepted and read back. No e-mail was due in the seconds it existed.

At the end the database, the workspace and then the resource group were deleted (the group took
27 minutes to go), and the group, the role, the policy and the budget were each read back as gone.

**The first deployment, steps 1 to 5 (2026-10-03)**

Not the trial: this folder's template and scripts, from this branch, in the resource group
`azurebank-demo`. The runbook's blocks and functions ran as written here, but for one change: the
question before a deployment was answered by a variable, not typed. Each line is what the step
printed. One run each.

- Step 1, before any write: the Azure CLI 2.90.0 with no extension, the Bicep CLI 0.47.16,
  go-sqlcmd 1.10.0. `az group exists` answered `false`. The subscription held no budget, no
  Container Apps environment in Italy North, no custom role and no custom policy definition, and
  the protected folder did not exist.
- Step 2: the group was created. The what-if listed fourteen resources to create and nothing
  else. The deployment answered `Succeeded`, the whole run 17 minutes, and the protected folder
  was gone afterwards. The environment's mode read back `WorkloadProfiles`: the first read on
  record that shows the property.
- Step 3, the same template again, `keepLogs` now read from the deployed environment. The
  what-if: nothing to create or delete; `NoChange` on the server, the database, the lock, the
  firewall rule, the three identities, the federated credential, the role and the workspace; one
  `Ignore` on a database; `Modify` on four resources, naming the policy definition's `version`
  and `versions`, the environment's `peerAuthentication`, `peerTrafficConfiguration` and
  `publicNetworkAccess`, the diagnostic setting's `logs` and `metrics`, and the policy
  assignment's `definitionVersion`. The deployment answered `Succeeded`, the whole run under a
  minute. The three properties named on the environment read the same before and after: public
  network access enabled, peer traffic encryption off, mTLS off. The other three were not read
  before and after.
- Step 4, every read the table expects, and each gave the value expected: the Consumption profile
  alone; the mode `WorkloadProfiles`; the logs to `azure-monitor`, one workspace with a cap of
  0.05, kept 30 days, key access off, on the pay-per-GB plan and taking data (`RespectQuota`), and
  one diagnostic setting with the console and the system logs; the database `Basic`, capacity 5,
  2147483648 bytes, `Local` backups, online; the rule `AllowAzureServices` alone, one
  administrator, Entra-only `true`, TLS 1.2; the three identities, attached to nothing, and
  `az identity list-resources` answering `[]` for `azurebank-app` and for `azurebank-migrate`;
  one federated credential, with the GitHub issuer, the subject ending `environment:demo` and the
  audience `api://AzureADTokenExchange`; one custom role, nine actions and no data action, and no
  role assignment for the deployment identity (the same command listed five for the owner); the
  lock, `CanNotDelete`, on the database; the assignment `azurebank-shape`, enforcement `Default`;
  no job.
- Step 5: the budget `azurebank-monthly`, 20 a month, read back with its four notifications, each
  to the one mailbox given: 30, 50 and 100 % of the actual cost and 30 % of the forecast. The
  protected folder was gone afterwards.

**The first deployment, step 6 (2026-10-03): two stops before the users were made**

The same day, this folder's own script and file, on the database `AzureBank` that step 2 created.

- `sql-principals.ps1` ran the users file twice, and both runs stopped at its first check:
  `Code found: [database_firewall_rules]`, `Msg 50003`, exit 1. Nothing was run. The script knew
  the server's refusal of this machine by its own pattern and allowed the address the refusal
  named with its temporary rule; at the end it deleted that rule, with the lock on the database
  in place, and read back `AllowAzureServices` alone.
- A read-only query of the catalog, as the Microsoft Entra administrator, through a firewall rule
  created and deleted in the same run, returned this and no other row. In `sys.sql_modules`: the
  view `database_firewall_rules`, in the schema `sys`, `is_ms_shipped` 1, created on 2026-08-28,
  before the database. In `sys.triggers`: nothing. Among the principals above the four the engine
  makes: nothing but the fixed roles.
- The local engine has no such view: on LocalDB 17.0 `OBJECT_ID` finds none, and none of the
  local runs below listed it. Microsoft's page on it names Azure SQL Database and SQL database in
  Fabric only
  (<https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-firewall-rules-azure-sql-database>,
  2025-07-29).
- So the first check, and the same list before the commit, now leave out a module that is in
  `sys` and marked `is_ms_shipped`, the two together, and nothing else. Every trigger still
  counts. Both conditions, because each alone says less. No object can be created in `sys`, says
  Microsoft's page on schemas
  (<https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/ownership-and-user-schema-separation>,
  2024-05-09), and on LocalDB a sysadmin's `CREATE VIEW` in `sys` is refused (Msg 2760), and so
  is moving a view there (Msg 2710). `is_ms_shipped` is not reserved to `sys`: on LocalDB a
  sysadmin marks a view in `dbo` with it (`sys.sp_MS_marksystemobject`, which Microsoft does not
  document), and Microsoft's page on change data capture in Azure SQL Database names objects
  marked with it in the schema `cdc`
  (<https://learn.microsoft.com/en-us/azure/azure-sql/database/change-data-capture-overview>,
  2025-09-24). No page read says that a user cannot set it on Azure SQL Database. The pages were
  read on 2026-10-03.
- Before the step ran again, a read-only query with the users file's own conditions, through a
  firewall rule created and deleted in the same run, listed what the lists before the commit
  would refuse in that database, the users not yet made. One row and nothing else: `SELECT`,
  granted to `public`, on that view, whose ID is positive. Among the modules, that view alone,
  which the narrowed first check leaves out; no trigger, no user, no role, no schema owned by a
  user; of the role members, `dbo` in `db_owner` alone, which the file expects. The permission
  list kept `public`'s grants on objects only when their ID is negative, so it would have refused
  that row, and the same run would have stopped there.
- So the permission list now also keeps `SELECT`, granted to `public`, on an object that is in
  `sys` and marked `is_ms_shipped`, the pair the first check reads, and nothing wider: not a
  `DENY`, not a grant that may be passed on, no other permission, grantee or class. It is a right
  to read and creates no code; nobody can create an object in `sys`; and Microsoft's page on the
  view, the one above, says that every user with permission to connect to the database may read
  it. Each refused permission on an object now prints the object's schema and name; that row
  would have printed none.
- What the file did after the two changes is in the next part.

**The first deployment, steps 6 to 10 (2026-10-03): the users, the sign-ins, the app's what-if**

The same session, with both checks narrowed. Each line is what one run of the step printed,
unless the line says otherwise.

- Step 6: the users file ran twice and made the two users. Each run printed go-sqlcmd 1.10.0
  signing in with `ActiveDirectoryAzCli`, that the server refused this machine's address and
  that the script was allowing it until the users were created, then
  `azurebank_app: db_datareader, db_datawriter; ID as asked: 1`,
  `azurebank_migrator: db_datareader, db_datawriter, db_ddladmin; ID as asked: 1` and
  `Firewall rules now: AllowAzureServices.`, and ended with exit 0. The file prints the two
  users' lines only when its lists before the commit are clean, so they were, with the two users
  in place. The second run printed the same lines as the first, and the file's run inside step 8
  printed them too. That the second run changed nothing is not shown: the file prints the same
  lines when it has replaced a user.
- Step 7, the probe job, three starts. Each request for the job was accepted, the Deny policy
  assigned, and the SDK image built the program inside half a vCPU:
  - start 1, carrying `azurebank-app` and asking its token: `Succeeded`, 39 s, exit code 0, and
    the lines `open: 6390 ms` and
    `user azurebank_app: db_datareader 1, db_datawriter 1, db_ddladmin 0`;
  - start 2, the same identity asking the migrator's token: `Failed`, no length, exit code 3, and
    `failed after 641 ms: number 0, class 20, SqlException > AuthenticationFailedException > MsalServiceException`,
    the chain the trial saw (there after 40 to 71 ms);
  - start 3, carrying `azurebank-migrate`: `Succeeded`, 38 s, exit code 0, `open: 3253 ms` and
    `user azurebank_migrator: db_datareader 1, db_datawriter 1, db_ddladmin 1`;
  - then the job was deleted, no job was left, and the program file was gone.
- **The first open as the app's identity took 6,390 ms**, against the app's connect timeout of
  10 s. It is one cold run of the probe program, whose string waits 30 s (`Connect Timeout=30`);
  the migrator's took 3,253 ms, and the trial's first open 3,810 ms. The timeout stays 10 s: that
  decision stands until the app's own first sign-in after a cold start is measured. The second
  session, on 2026-10-03, timed no cold start.
- Step 8: the users file once more (above), then the made-up SQL sign-in, refused with "Reason:
  Azure Active Directory only authentication is enabled.", and the script printed its line
  `Proved: a SQL sign-in is refused, and the server says it is because Microsoft Entra-only authentication is on.`
  and the rule list `AllowAzureServices` alone. It throws on any other outcome.
- Step 9, the app's what-if on the template as it was then, answered "no". `secrets.ps1` with
  `-DeployApp` wrote 13 parameters (`keepLogs` read from the environment, the mailbox from the
  variable, the seven generated), and the protected folder was gone afterwards. The what-if: 7
  `Create` (the job, one role assignment, the action group, four alerts); `Modify` on the four
  resources of step 3, naming the same properties; `NoChange` on the role definition, the three
  identities, the federated credential, the workspace, the server, the database, the lock and the
  firewall rule; one `Ignore` on a database; and two `Unsupported`. Nothing was refused. The
  runbook expected nine to create.
- The first `Unsupported` is the app, printed as its ID unworked: `resourceId(...)` around the
  name,
  `if(and(true(), or(... empty(parameters('jwtSecret')) ...)), fail('deployApp=true needs ...'), 'azurebank')`.
  What the plain values decided was worked out (`deployApp` to `true()`, the tag and the address
  to `false()`); every secure parameter was left as written. The second was printed as
  `Microsoft.Authorization/roleDefinitions`. It is the role assignment on the app:
  `bicep snapshot` of that template, run offline the same day with values of the same shapes,
  gives the app's ID in the same form and the assignment's ID holding it, and `Show-WhatIf`,
  given that ID, prints that type.
- Microsoft's page on what-if says it does not work out "any reference to a secure parameter
  value", and that a resource is short-circuited when its resource ID or API version cannot be
  calculated (<https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-what-if>,
  2026-03-03, read on 2026-10-03). The template's check of the app's values made the app's name go
  through `fail()` and the seven secure parameters. So the check moved into `app-inputs.bicep`
  ([What this creates](#what-this-creates)), and offline the changed template named all 23
  resources of the run with the app, the alert on the workspace among them. On Azure, see step 9
  again, below.
- Step 10: the five lines of step 7 were in the workspace when they were read, at 02:40:28Z,
  written from 02:31:37Z to 02:33:36Z. The reads of step 1: the rule `AllowAzureServices` alone;
  Entra-only `true`; the log's cap 0.05, taking data (`RespectQuota`); no job; nothing attached
  to either identity; the mode `WorkloadProfiles`; the protected folder gone.
- Step 9 again, later that day, on the changed template, answered "no". `secrets.ps1` wrote the
  same 13 parameters, each read as at step 9. The what-if: 9 `Create` (the app, the job, two role
  assignments, the action group, four alerts); `Modify` on the same four resources, naming the
  same properties; `NoChange` on the same ten; one `Ignore` on a database; nothing
  `Unsupported`, nothing to delete, and no line for the check of the app's values. The protected
  folder was gone afterwards.

**The first deployment, steps 12 to 21 (2026-10-03): the second session**

The same day, from 09:54Z, with the workflow on `main`, at commit `1fc4d131`. Times are UTC. Each
line is what one run of the step printed or what a read gave, unless the line says otherwise. The
steps are in the runbook's order, but for step 20, which ran last and is told last; of the
others, step 14 ran first, then 13, 12 and 15. What a migration printed is told here, not pasted.

- Step 12: the three identifiers were set as secrets of `demo` through the pipe, and
  `gh secret list` showed the three names.
- Step 13, the reads of step 1 and of step 11: the rule `AllowAzureServices` alone; Entra-only
  `true`; the cap 0.05, taking data (`RespectQuota`); no job; nothing attached to an identity;
  the mode `WorkloadProfiles`; the database `Online`, `Basic`; the environment `demo` with a
  `required_reviewers` rule and exactly `main`; the protected folder absent.
- Step 14, 09:54:30Z to 09:56:29Z: `build-push` succeeded on its first run, with
  `first_publication` left alone and no "The registry gave no clear answer" line, and pushed
  three digests. Read in the workflow, and not kept as text: without that box the script builds
  only when the registry says there is no such manifest, so that is what the registry answered
  the workflow's token for packages that did not exist yet. **The three packages were readable
  anonymously at once**, before anybody had changed a visibility setting: the runbook's check
  gave `exit 0` three times; with an anonymous token from the registry, a request for each of
  the three manifests answered 200, and one for a package that does not exist answered 403;
  each package's page opened without a login, under the repository. The owner changed nothing.
  Why they were public was not looked into.
- Step 15, at 10:01:32Z: the what-if was equal line for line to the second one of step 9: 9
  `Create` (the app, the job, two role assignments, the action group, four alerts), `Modify` on
  the same four resources, nothing to delete. The deployment answered `Succeeded`, with the Deny
  policy assigned, and the protected folder was gone afterwards. Read back: scale 0 to 1;
  `Single`; ingress external, target port 8080, `allowInsecure` false, no additional port
  mapping; eight secret names on the app and one on the job; `azurebank: azurebank-app` and
  `azurebank-migrate: azurebank-migrate`; the job `Manual`, retry limit 0, timeout 600,
  parallelism 1; two role assignments for the deployment identity, the custom role on the app
  and on the job, and none for either database identity; four alerts, enabled; the action group
  enabled, with one e-mail receiver. The app's first revision read `Running` before any
  migration had run.
  - The policy refused: the PATCH that asked for two replicas failed with
    `RequestDisallowedByPolicy`, and `maxReplicas` read 1 afterwards.
  - The test notification was refused by the offer:
    `az monitor action-group test-notifications create` answered
    `(Conflict) Free subscription not supported`. The portal's Test button was not tried. What
    the mailbox got instead, as the owner read it: "Action required: Verify your email for Azure
    Monitor action group" at 10:02Z, with a one-time code valid for 30 minutes, and after the
    owner verified, "You're now in the azurebank action group" at 10:03Z.
  - `az identity list-resources`, read at 16:11Z: the app `azurebank` for `azurebank-app`, the
    job `azurebank-migrate` for `azurebank-migrate`, and nothing for `azurebank-deploy`.
- Step 16, 10:04:54Z to 10:06:59Z, `deploy.py` as the owner: the job moved to the tools image;
  the execution `Succeeded`, 34 s, exit code 0, reason `CompletionsReached`; the new revision
  ready 38 s after the verdict; the revision before it inactive; the smoke test passed: the
  page, `Healthy`, and the sign-in refused by the API after it asked the database. What the
  migration printed, read with `--job-log` at 10:16Z, 11 minutes after it was written: 16
  migrations pending, from `InitialCreate` to `AddUserSessionStamp`, each applied, the database
  at 16 of 16, and `migrate` took 5.3 s.
  - That read's verdict for the same execution said "exit code not reported", where the
    deployment's own verdict, four seconds after the run ended, had said exit code 0. Three
    later executions existed by then. At 10:44Z the latest execution's verdict still carried
    its exit code 0, fifteen minutes after it ended. So an exit code is read when the run ends,
    as the deployment does; an older execution's may no longer be there.
  - `--job-log` with that execution's name printed 25 lines: its own 20, and the 5 of the next
    execution, written about four and a half minutes later and read about six and a half
    minutes after they were written. It asked by the job's name and a period, from two minutes
    before the execution's start to five after its end. In the table, every line of the job
    carried in `ContainerGroupName` its execution's name, a hyphen and a suffix: 20 lines for
    this execution and 5 for each of the four that followed that day. The table also has the
    columns `JobName`, `Log`, `ContainerAppName` and `ContainerName`, the last of which read
    `migrate` on those lines. `--job-log` asks by `ContainerGroupName` as well since then; that
    query is tested offline and has not been sent to the workspace.
- Step 17, the workflow's `deploy`, twice at that commit, each approved by the owner: both
  succeeded. In each log: "the listing was refused", which the script prints only for Azure's
  `AuthorizationFailed`; the verdict `Succeeded`, exit code 0, 37 s; the smoke test's line, with
  the address masked as `***`. Each migration had nothing to do. In each raw log the five counts
  were 0: the server's name, the app's address, the three client IDs, the tenant and the
  subscription, and anything shaped like an IPv4 address. So the deployment identity read and
  changed the job and the app, each carrying its identity, with its nine actions and no right
  on either identity.
  - An overlap, not planned. A second full run of `deploy.py` from the owner's terminal, which
    no step asks for, ran from 10:14:01Z to 10:15:53Z and ended well: `Succeeded`, 24 s, nothing
    to migrate, a new revision, the smoke test. The workflow's second run started its own
    `deploy.py` at 10:15:05Z, about 50 s before that one ended, and read the app with a latest
    revision that was not yet the latest ready one. It ended well too. Read afterwards: one
    active revision, at 100 %, and both containers and the job on that commit's images. The
    workflow's concurrency group does not cover a run from a terminal. The images were the same
    in both; it was not tried with different ones.
- Step 18, 10:18:27Z to 10:19:45Z, `deploy.py --app-only`: no job touched, the new revision
  ready after 39 s, the smoke test passed.
- Step 19, the last-resort migration from this machine, from a checkout of that commit:
  - the users script first, on the database that then held the 16 migrations: exit 0, both
    users with their roles and `ID as asked: 1`, the temporary rule in and out;
  - as the runbook had it, with the tool's connect timeout of 10 s: exit 1 after 11 s without
    reaching the server, error 0 of class 20, "DefaultAzureCredential failed to retrieve a token
    from the included credentials";
  - `az account get-access-token` for the database, as a diagnosis: a token after 929 ms, and
    again after 986 ms;
  - with `Connect Timeout=60` in the string: the first try reached the server after 13 s and,
    in a second run, after 8 s, and was refused for this machine's address (error 40615, class
    14); with the temporary rule `owner-migrating-by-hand`, the second try ended with exit 0
    after 11 s, with no migration pending, the database at 16 of 16, and `migrate` at 9.4 s;
    the rule was deleted, and `AllowAzureServices` was alone afterwards.
- Step 21, at 10:23:42Z, from outside: `http://` answered 301 to `https://` of the same name;
  `/health/ready` answered `Healthy`; `POST /api/auth/login` answered 404; port 5068 gave no
  connection (curl's exit code 28).
- Step 20, read at 12:01Z for the hour from 10:00Z to 11:00Z. The query as the runbook had it
  was refused: `SEM0001`, "union: column named 'SourceTable' already exists". Of the 680 tables
  of the workspace's schema one, `LAJobLogs`, has a column of that name. With `TableOfRow` it
  ran. The four numbers: the metric `Ingestion Volume`, no time series at all; the rows, 395 in
  the hour's middle forty minutes, 446 in the hour and 446 in the widened hour
  (`ContainerAppConsoleLogs` 182, 199 and 199; `ContainerAppSystemLogs` 213, 247 and 247). The
  metric had no time series for any hour of that day up to 12:00Z either, with its dimension
  `Table Name` or without, nor for the last 40 minutes by the minute. `Query Count` of the same
  workspace had one for the four hours from 09:00Z (1, 2, 2 and 2); `Ingestion Time` had none.
  The reason: the first total is not 0, so the hour counts, and the metric counted nothing where
  the workspace ingested 395 to 446 rows. They do not agree. The alert `azurebank-log-volume`
  was deleted at 16:11Z, on the owner's word (exit 0), and the alerts read afterwards were
  `azurebank-bytes-out`, `azurebank-replica-time` and `azurebank-requests`, all enabled. The
  owner decided against a billed log search rule for now. Why the metric reported nothing was
  not looked into.
- Not done in this session: no cold start was timed, so the app's own first sign-in against its
  10 s is still not measured; no cost of this resource group was read by meter; the portal's
  Test button was not tried; an overlap of two deployments with different images was not tried.

**A later deployment the same day, at commit `8552f935` (2026-10-03)**

The commit that adds a 17th migration, `AddDemoCopies`. The workflow, approved by the owner.

- `build-push` succeeded, then `deploy`, 10:26Z to 10:30Z: the verdict `Succeeded`, 25 s, exit
  code 0; the smoke test passed; the same five counts in the raw log, all 0.
- What the migration printed, read with `--job-log` at 10:44Z: one migration pending,
  `AddDemoCopies`; it was applied; the database at 17 of 17; `migrate` took 2.5 s.
- So a migration added by a later commit was applied on the live database through the workflow,
  by the migrator's identity, whose user holds `db_datareader`, `db_datawriter` and
  `db_ddladmin`. Until then [Not measured yet](#not-measured-yet) said of that migration "Read,
  not run".

**The end of that day (2026-10-03, from 16:11Z)**

The last reads before `az logout`:

- the firewall rule `AllowAzureServices` alone; the cap 0.05, taking data (`RespectQuota`); one
  job, `azurebank-migrate`;
- the app `Running`, with one active revision and 0 replicas: scaled to zero;
- five executions of the job, all `Succeeded`: step 16's, the two of step 17, the unplanned one
  and the later deployment's;
- the day's log volume: 0.120 MB in `ContainerAppConsoleLogs` and 0.069 MB in
  `ContainerAppSystemLogs`, both billable, 0.19 MB of the 50 MB cap.

## Not measured yet

**Measured on a local stack of this code** (`compose.yaml`, Production images, SQL Server 2022,
on 2026-10-02), not on Azure:

- The smoke test's three answers, and the real `deploy.py` smoke test passing against them. A
  sign-in for an unknown address: 401 `application/json` with `"errorCode":"INVALID_CREDENTIALS"`.
  The proxied `/api/auth/login`: 404, empty. Once the shared sign-in limit is spent: 429 with
  `Retry-After: 60` and `"errorCode":"RATE_LIMIT_EXCEEDED"`.
- Before any migration the app starts and `/health/ready` says `Healthy`; the sign-in answers 500
  on a database with no table and 503 with no database.
- The API as a user that holds `db_datareader` and `db_datawriter` only (a SQL login there, since
  no identity exists off Azure): it starts, the smoke test passes, a seeded user signs in, reads
  the accounts, and the statement that takes `sp_getapplock` succeeds. The three real-stack test
  suites and a transfer were not run as that user.
- The seven application secrets in the shapes `secrets.ps1` generates are accepted by the API at
  start.
- The BFF's setting, on the built image and the same 31 requests: 26 request lines at Information
  become 0; the 2 request lines of 5xx answers stay; the 11 warnings stay, kind by kind; the API's
  own lines are untouched.
- A lost registration race prints the duplicate value in the API's console: an e-mail address
  four times in two lines, a handle the same way. The BFF's console holds neither.
- **A registration on a database that only `migrate` has touched, on 2026-10-05.** `compose.yaml`
  under a project name of its own, with the `seed` service made to run `migrate` a second time,
  so that the API started on a schema with no row: 17 migrations in the history table, 0 users,
  0 roles. The images were built from the sources of `6988514f`; SQL Server 2022 CU27
  (16.0.4295.3). One `POST /bff/auth/register` with a body that passes the rules was answered
  **500**, and afterwards the database held **0 users and 0 roles**, and no role membership and
  no account either. The API's console gave the reason, `Role USER does not exist.`, and its
  request line read `HTTP "POST" "/api/auth/register" responded 500`, at Error. The control:
  after `seed` had run on that database (4 users, 2 roles), the same body was answered 201 and
  the users were 5. A body of `{}` was answered 400 by the BFF and did not reach the API. So on
  the database a first deployment leaves, a registration cannot commit until something has
  created the roles: with the demo, the pool job's first run. One request each; not on Azure.

**Measured on a local SQL Server** (17.0, LocalDB, with go-sqlcmd 1.10.0 and `-b`; run again on
2026-10-03 on the file without the switch for the object ID, again that day after the first
check was narrowed, and again after the permission list changed). The local engine refuses both
real forms of `CREATE USER` in `sql-principals.sql` (`TYPE = E`: a syntax error; `FROM EXTERNAL
PROVIDER`: not configured), so the file ran with three substitutions: the database's name, a user
made from a disabled SQL login whose ID is the 16 bytes asked for, and the user type that goes
with it. Everything else is the file as it is.

- On a clean database it commits and exits 0; a second run changes nothing.
- A new app identity replaces only that user. Asking for the second form of `CREATE USER` while
  both users exist changes nothing.
- As the migrator, directly: `CREATE USER` is refused (Msg 15247) and so is joining `db_owner`
  (Msg 15151). A DDL trigger is accepted.
- With both guards switched off, a trigger planted by the migrator makes the migrator an owner
  when the file runs, and the file exits 0. With the file as it is: `Msg 50003`, exit 1, nothing
  changed.
- With only the first check off: `Msg 50004`, exit 1, everything rolled back. A trigger that
  grants `CONTROL` and removes itself is caught by the permission list.
- Thirteen single oddities (a view, a leftover user, a role, the app in `db_owner` or
  `db_ddladmin`, `guest` allowed to connect, a grant to a user, a `DENY` for a user, a schema owned
  by the migrator, a grant to `public` on a table, a `DENY` for `public`, `CONTROL` for the
  migrator, a right name with a wrong ID): each refused, with its name printed.
- Two more after the first check was narrowed: a view in `dbo` and a trigger on a table in `dbo`,
  each marked `is_ms_shipped` by `sys.sp_MS_marksystemobject` as sysadmin. Each was refused at the
  first check (`Msg 50003`) with its name printed. Every case of the run before the change gave
  the same answer again.
- After the permission list changed: `SELECT` for `public` on a view, on a table, and on a table
  marked `is_ms_shipped`, all in `dbo`. Each was refused with the object's schema and name
  printed: the view at the first check, and by the permission list as well with that check
  switched off. So was `SELECT` for `public` on the schema `sys`, though a permission on a schema
  is still printed without the schema's name. LocalDB has no object in `sys` with a positive ID on
  which a grant can be made (a new database has 115, system and internal tables, and a grant on
  each was refused, Msg 15151), so the grant now kept cannot happen there. In a copy where the
  kept condition names `dbo` instead of `sys`, `SELECT` for `public` on the marked table was kept
  and the file committed, and so was `SELECT` on one of its columns; refused, each with its object
  named, were the same grant `WITH GRANT OPTION`, a `DENY`, an `UPDATE`, a grant to the app, and a
  grant on a table not marked. Every case of the run before the change gave the same answer again,
  a grant to `public` on a table now with the table's name.
- A value made of an ID, a quote and a statement, passed to `sqlcmd` as the runner never passes
  it: the statement ran, the lists named the user it created and the run was refused, and that
  user was still there afterwards, because it was made before the transaction began. The file
  does not keep such text out; the runner does, by parsing each ID.
- After the first 16 migrations a database held 0 triggers and 0 modules, and none of the lists had
  an unexpected row. (Measured on 2026-10-02 and not repeated on a local engine. Since then the
  two code lists have changed, and they now leave one kind of module out, so they can only name
  less; and a 17th migration, `AddDemoCopies`, was added. Until 2026-10-03 this line said of it
  "Read, not run". That day it ran on Azure: the migrate job applied it on the live database, 17
  of 17 ([Measured on Azure](#measured-on-azure)). What was read of it stands: it adds a table
  with its primary key, a column, five indexes, a foreign key, a default and two CHECK
  constraints, kinds the first 16 already create, and no `Sql()` statement. Whether a database
  then holds 0 triggers and 0 modules was not measured again, locally or on Azure: the users
  file last ran on the live database at step 19, when it held 16 migrations, and passed there.)
- Without `-b`, `sqlcmd` exits 0 when the file stops on an error.

**Measured on this machine, of the tools:** go-sqlcmd 1.10.0 passes the users script's three
checks; the ODBC `sqlcmd` is refused as the default tool and accepted with `-OdbcSignInName`; a
program validly signed by someone else is refused. go-sqlcmd prints a refused sign-in as text with
no error number, and an error inside a batch with `Msg` and its number: that is why the users
script knows the firewall's refusal by its sentence. With the .NET SDK 10.0.401, the program of
step 7 is built and started by `dotnet run`, as the probe job does it. Run here against a local
SQL Server, as SQL logins with the roles of the two users and others, it exited 0 for the roles
expected; 5 when `db_ddladmin` was expected and absent, or present and not expected, and for a
user that writes and cannot read; 4 for a wrong password (error 18456); 2 for a port where
nothing listens (class 20, around a `Win32Exception`) and for a login with no user in the
database (error 4060). Exit 3 needs a token refused on Azure. The helper functions of this file
(`Show-Identities`, `Set-Probe`, `Start-Probe`, `Assert-EnvironmentMode`) ran against a stand-in
for `az`: the requests they send are the ones written here, and no Azure answered them.
`Assert-EnvironmentMode` passed `WorkloadProfiles`, and an answer with no mode whose logs go to
`azure-monitor`; it threw on `Express`, `Archived` and `ConsumptionOnly`, on no mode with the
logs off or elsewhere, and on no answer. On 2026-10-05 the same was done for the blocks of the
third session that build a request or filter an answer: `Show-List`, `Show-Revisions`, the job
of step 29, `Set-PoolParallelism`, the sign-ins of step 30, the start and the metric definitions
of step 31, the activity log's filter of step 33, and the blocks of steps 22 and 25 that print
names only. Every PowerShell block of this page parses, and those ran against functions standing
in for `az`, `git`, `python` and `curl.exe`, with invented answers: each sent the request
written here, with the body its step describes, and removed its body file. `Show-Executions`,
given an answer that holds no list, printed one line with no name and no code. No Azure answered
any of them: the shape of every answer in that run was invented. On 2026-10-06 the same was done
for what the phone's receiver added. The 65 PowerShell blocks of this page parse, one more than
before, and a block with an error planted in it is reported. `Show-Receivers` sent the request
written here and printed counts and names, never an address, for five invented answers of the
action group: no such receivers, an empty list of them, a null, one and two; with no answer it
threw. Step 25's block for the test notification sent its two requests and printed its lines
for an answer in the shape Microsoft's page of that request gives, for the same fields in
another case, for another shape and for a refusal. The run of that step with
`-AlertPushAccount` was not run as a block: what the script does with the argument, given and
empty, is in the tests. Later that day the same was done for step 30's second part, the networks
of the ingress. The 68 PowerShell blocks of this page parse, three more than before, and a block
with an error planted in it is reported. The three new blocks ran against functions standing in
for `az`, `gh`, `python`, `curl.exe`, `Invoke-Template` and `Start-Sleep`, and a stand-in script
where `secrets.ps1` is. The run that sets the networks handed the script the list as one
argument and printed, for an invented app, each container's count of settings and of networks,
and no value. The proof sent twelve sign-ins, one, and twelve more, each of the last with one
`X-Forwarded-For` header of its own, and removed its body file. The way back handed the script
the word `none`. As it was first written the proof's block handed `curl.exe` an empty argument
where no header was named: seen in that run, and put right before the page was committed. No
Azure answered any of them. On 2026-10-03 the tests also
ran on Linux, in WSL
(Ubuntu 24.04, Python 3.12, PowerShell 7.6.6 and Bicep 0.47.16), from an archive of the branch:
all passed, among them the Linux half of two (the folder's and the file's modes, and the
signature check that says it checked nothing), and the two that hold only on Windows were
skipped. actionlint 1.7.12 with ShellCheck 0.11.0 read the run blocks of the three workflows and
found nothing; an unquoted variable planted in a copy is reported.

**Measured on GitHub's runner, of the CI job `infra`** (read in the job's log, on 2026-10-03): on
the push of `1fc4d131` to `main` the job ended `success`. Its Bicep was 0.47.16; the three
templates built and linted clean; 267 tests ran, among them the ones that run `snapshot`, and
the two that hold only on Windows were skipped and no other; actionlint 1.7.12 printed no
finding. Until this was read, the table below held that job as something no run had shown, and
said that the branch had not been pushed.

**Not measured.** The first deployment has run both of its sessions, on 2026-10-03. The table
holds what no run has shown yet, each line with where it would show.

Until that day the table also held what only the second session could show. These lines were
taken out because it showed them ([Measured on Azure](#measured-on-azure)): the template's second
step deployed, and `deploy.py` and the workflow `deploy.yml` on Azure (steps 15 to 17); the action
group and the alert rules accepted with their metric names (step 15); the app's scale read back,
0 and 1; the template passing the Deny policy, and the policy refusing a second replica on a
PATCH; the registry's answer to the workflow's token for a package that does not exist yet, read
from what the script did with it, and the digest lines (step 14); the smoke test's answers
through the Azure ingress; the job start, the execution's states and the revision's `active` flag
as `deploy.py` reads them (step 16); the columns `--job-log` reads; the deployment identity's
reads and changes of the app and of the job with its nine actions, and its listing of secrets
refused with `AuthorizationFailed` (step 17); the app ready on an empty database, before the
first migration; `migrate` as the migrator's identity (step 16); the raw log of a workflow run
holding no identifier, name or address (step 17); and `migrate` from a checkout as the owner,
once its string carried `Connect Timeout=60` (step 19). One line was answered the other way: the
fourth alert does not count lines (step 20).

| What | Where it shows |
| --- | --- |
| Azure refusing a run with a value missing, through `app-inputs.bicep` (seen offline and on a local engine only, under [Checking these files](#checking-these-files)), and whether it refuses before the foundation's resources are sent again or only when the check's own deployment starts (the app, the migrate job, the pool job when it is built, and the action group wait for it either way; until 2026-10-05 this row named three of them). The what-if run again at step 9 was given every value | not provoked |
| The four conditions of the policy named above at work: an init container on an app or on a job, and parallel runs under a schedule or an event trigger. The definition is deployed and assigned, and none of the four has been seen refusing | not provoked; step 29, which has not been run, makes the one on a schedule due |
| That a second run of the users file on Azure SQL changes nothing. The second run of step 6 ended with the same lines as the first, and the file prints those lines whether or not it replaced a user | no step reads it |
| The users file dropping and creating a user inside its transaction, which it does when an identity has been made again; its second form, `FROM EXTERNAL PROVIDER WITH OBJECT_ID` | not provoked; the second form only if it is asked for |
| A container of the app that asks for a token and names no identity gets none (read on Microsoft's page: such a request is answered for a system-assigned identity, and the app has none) | not provoked |
| The API as `azurebank_app` under the three real-stack test suites, and a transfer as that user. This row placed it before step 15; step 15 ran on 2026-10-03, and that day's records hold no such run | on a local SQL Server |
| **The app after a cold start:** its three probes against a start from zero (1 s delay, 3 s period, 10 failures; 4 s timeout on readiness), the first database request after it, and its own first sign-in against its 10 s: the probe program's first open as the app's identity took 6,390 ms at step 7, one cold run with a string that waits 30 s. The second session saw each new revision become ready, the smoke test answered through both containers each time and, at the end of the day, the active revision at 0 replicas; it read no container's state and timed no cold start | the days after |
| Any of the three alert rules ever firing, and an alert's e-mail arriving: the test notification is refused on this offer, and what arrived at step 15 is the verification and the membership message. That the rules cost nothing: no cost of this resource group has been read by meter | the days after |
| Why the metric `Ingestion Volume` had no time series on 2026-10-03, and whether it has one on another day or on a workspace that takes more | step 20, by whoever turns that alert back on |
| What the `Replicas` metric reports while the app is scaled to zero: 0, or nothing. If nothing, a day's average is 1 on any day the app ran at all, the alert on replica time fires on any use, and that rule has to count another way | the first days after step 16 |
| What the registry answers for a package that exists and is private, anonymously or to the workflow's token: none of the three packages has been private. Why they were public as soon as they were published | not provoked; not looked into |
| Whether every visitor shares one sign-in limit behind the Azure ingress, and with it the limit of 300 a minute on every other request, which counts by the same address | not provoked; step 30, which has not been run, measures it |
| The networks of the ingress, all of it (added on 2026-10-06): the address the app's warnings name behind the ingress, and whether it is the same after a new replica; what a run of the template with `proxyNetworks` shows in its what-if, and that it makes a revision; that the ingress appends the visitor's own address after whatever the visitor wrote, in one hop; the proof (A refused at the eleventh sign-in while B is answered, and a header A writes not believed); the way back with `none`; what Azure does with a revision whose `bff` refuses to start on a network | not run; step 30's second part, in the session that measures the address |
| A replica's container states, which `deploy.py` reads only when a new revision does not get ready | a real failure; not provoked |
| `--app-log` against the workspace: the table has the two columns it reads (`ContainerAppName`, `ContainerName`), and the command has not been run. `--job-log` with its filter on `ContainerGroupName`: tested offline, and the column was read at step 16, but the query has not been sent | the next read of either |
| The automatic put-back on a real failure. Its trigger is proved by unit tests only; its request and its wait are the ones `--app-only` uses, which ran at step 18 | a real failure; not provoked |
| What the app reads just after the put-back request. If its state still says `Failed`, left by the deployment that failed, `deploy.py` reports a put-back that did not succeed although it may have | a real put-back; not provoked |
| A request to Azure that fails once in the middle of a run. Nothing is asked twice: the run stops, and nothing is put back | not provoked |
| The last-resort road with a migration to apply: step 19 ran it with nothing pending. Why the driver got no token within 10 s there, when `az` gave one in under a second | the day it is needed; not looked into |
| Two deployments at once with different images: the overlap of 2026-10-03 had the same images in both | not provoked |
| The meters after 48 hours: the three environment meters and the Dedicated one at 0; whether the free 5 GB of logs apply to this offer; whether the cost view returns a row at all (on the trial's own day it returned none). The second session read no cost of this resource group by meter | 48 hours after steps 2 and 21 |
| What the workspace bills for a line of the app; how far the cap overshoots; whether an environment set to `none` still feeds a setting that exists | after step 21; the last two are not provoked |
| How long a managed identity's token stays valid for the database | not found in the pages read |
| That the identity is refused a scale change, a delete or a stop. One refusal is provoked on every deployment (the secrets listing); the policy's refusal is provoked as the owner | not provoked |
| Every command under [Switching the logs off](#switching-the-logs-off) but one, the deletion of the alert on the workspace, which ran as step 20's; every command under [Stop the app by hand](#stop-the-app-by-hand), which step 32 would run once, under [If something was stolen](#if-something-was-stolen) and under [Removing everything](#removing-everything). The trial made its own deletions with other commands | the day they are needed |

**Not measured, of what turns the demo on.** Added on 2026-10-05. Nothing of it has run on Azure:
the switch, the pool job, the policy's exception and the three commands are tested offline,
against stand-ins and invented answers, and steps 22 to 33 are where each line would show.

| What | Where it shows |
| --- | --- |
| The API's line for a refused sign-in as the workspace stores it: the query of step 22 is written from the line a local stack printed and from the columns step 16 read, and has not been sent | step 22's control |
| That Azure Policy takes a rule on a job's name (the field `name`, `in` a parameter), on a create and on a PATCH; that a job's name there is its bare name; that the definition's new name and description are accepted | steps 23, 25 and 29 |
| How soon a changed policy definition or assignment is enforced | the time between steps 23 and 25 |
| Where a run of the template meets a refusal of the policy: at the what-if, at validation, or at the job after the app was changed | step 25, only if a refusal happens |
| That a run with `deployApp=false` leaves the deployed app, its job, the action group and the alerts alone, and how its what-if names them | the what-if of step 23, before anything is sent |
| What the first run of the template after 2026-10-05 shows for the app (a ninth secret, four settings) and for the role definition (its description); that the role's assignments are untouched by it | steps 23 and 25 |
| Azure refusing a client key under 32 characters, through `app-inputs.bicep`: no engine has refused one | not provoked |
| That a value given after the parameter file wins over the file's (`demo=false`, `scheduledJobs=[]`) | the what-if of a way back, before it is sent; only if one is taken |
| That a job on a schedule is accepted, with `scheduleTriggerConfig` as the template writes it; that Azure reads the expression in UTC; that it starts with nobody typing, what such a run exits with, and whether the activity log holds a start event for it | steps 25 and 33 |
| That a job which waits for the app is not created when the app's own update fails; whether the revision before keeps answering when a new one never gets ready | not provoked; step 25's rule does not rest on either |
| That a job on a schedule can be started by hand; that a start takes a body which changes one setting for that execution, and leaves the job's own configuration and secrets as they were | steps 26 and 31 |
| What the list of a job's executions holds for a job that has never run: an empty list, no list at all, or a list given as `null`. `deploy.py` reads an answer with no list as no execution, so a deployment and a start by hand go on after it. A list given as `null` ends both in Python's traceback and not in a sentence of the script, before anything is written or started: seen offline against a stand-in, and nothing in the script catches it. `Show-List` prints `a list: True` and `entries: 0` for it as for an empty list, so that line alone does not tell the two apart. `Show-Executions` does: for a list given as `null`, as for one that is missing, it prints one line with no name and no code, and for an empty list nothing. So step 25 shows a `null` as that line beside `a list: True`: seen offline, each function's own expressions run on three literal answers | step 25: `Show-Executions` beside `Show-List` |
| The requests of steps 29, 31 and 33 as they are written: that Azure takes a PATCH of a job that holds a configuration and no template, and leaves the job's secrets as they were; that a start's body may hold the container alone; the fields of the activity log's events and of the metric definitions as the two filters read them. Each block was run offline against stand-ins, above | steps 29, 31 and 33 |
| What Azure answers the deployment identity for a pool job that is not there, "not found" or "not authorised"; what it answers its PATCH of a job that carries `azurebank-app`; that its role lets it read that job's executions | step 28; the first is not provoked |
| How Azure words an execution whose container exits 10, 11 or 15; whether its exit code is reported at the moment the run ends; whether a status and a code can disagree | step 31 |
| Whether Azure gives the list of a job's executions, or of an app's revisions, in pages, and in which order; what `active` says for the latest revision of an app scaled to zero. `--check` stops on a link to a next page of revisions; nothing follows one. For a job's executions that means, until the list has been read: a deployment and `--pool-run` do not see a run listed only on a later page; a deployment or `--pool-run` can wait out its whole wait and end "Timed out waiting" for a run of its own that has ended, if that run is not in the answer it reads; and `--job-log` or `--pool-log` can take an older run for the latest, or say that a named run does not exist. `Show-Executions` reads one answer as well. The pool job gains six executions a day | one read of each list at step 33 |
| The seconds of a first fill on Basic; the first open as the app's identity from the tools image; the first write this folder asks of `azurebank_app` on Azure SQL | step 26 |
| What a right that `azurebank_app` lacks looks like on a first fill: exit 1 with SQL Server's 229, by the code | not provoked |
| `--pool-log` against the workspace: the pool job's lines under its `JobName`, and the query by execution, which has not been sent for either job | step 26 |
| `--check` on Azure: that the owner may list the secrets of the app and of a job, that a job answers that listing in the shape the app does, and that an app at rest after a run of the template reads `Succeeded` with its latest revision ready | step 25 |
| The demo's two answers through the ingress: the tag in the page, and 403 `REGISTRATION_CLOSED` for a registration with the body `{}`. Both were measured on the compose stack with the demo on, on 2026-10-04 (ADR-0063, Validation), and not by `deploy.py`, whose two checks have met invented answers only; the 400 for that body with the demo off was seen on a local stack on 2026-10-05, above. That a closed registration spends a permit of the ten sign-ins share | step 25's `--check`; the permit is not provoked |
| The claim, the session and a PIN on the deployed demo, from a browser | step 27 |
| A job of another name refused a schedule, and the pool job refused two runs at once. That anything refuses a changed expression: nothing does, and `deploy.py`'s shape check is its only read | step 29 |
| What a stopped app answers, which property says it is stopped, and that the two calls are the ones written here; what a deployment, a `--check` or a run of the template does with a stopped app | step 32; the last three are not provoked |
| The metrics of the pool job and of the database: their names, their dimensions, and whether either has a time series | step 31 |
| Whether a visitor's session survives 6, 10 and 14 idle minutes: sessions are held in the replica's memory and end with every scale to zero | a session of its own, with the cold start |
| Whether the pool job's seconds count against the free amounts on this offer | the cost by meter, 48 hours after step 26 |
| A recovery after a theft that keeps the database: not designed ([If something was stolen](#if-something-was-stolen)) | the day it is needed |
| An app that reports an ingress and no host name; an output of the CLI that is not JSON, or that the terminal's encoding cannot decode | not provoked |
| A run of the template that ends while a deployment is under way, and the reverse: the deployment's own request is expected to put the app's settings back to what it read at its start, the demo's flag among them, and a run of the template to put back the tag it read. Read in `deploy.py` and `secrets.ps1`; the rule that keeps the two apart is under [Deploy a commit](#deploy-a-commit), and no code holds it | not provoked |
| Whether a workflow run that is cancelled reaches `deploy.py` as an interrupt, and so ends in its one sentence, or the script is killed without it. GitHub's page on cancelling a run says the interrupt is sent to the step's shell, and that the process tree is killed if the step is still running ten seconds later; it does not say what a program started by that shell is sent. Seen offline for an interrupt raised inside the script: exit code 1, nothing on standard output, the sentence on standard error | not provoked |
| That a read of a pool run's exit code can be refused or fail after the run was seen over, and how Azure words it: the sentence for it is tested against an invented refusal | not provoked |
| The repair of two containers that disagree about the demo; every road of [Turning the demo back](#turning-the-demo-back) | the day they are needed |
| Added on 2026-10-06. That Azure takes a receiver of the Azure mobile app from this template, with the account as its `emailAddress` and `owner-phone` as its name, and how a what-if words that change; what a read of the action group answers for a group with no such receiver: no property, an empty list or a null (`secrets.ps1` and `Show-Receivers` read each as none, against invented answers) | step 25: the what-if, the deployment and `Show-Receivers`; step 22 for the group with none |
| Added on 2026-10-06. That an alert reaches the owner's phone at all: whether the address the app is signed in with is the one the receiver needs (the sign-in name the template knows as `entraAdminLogin` is not taken for it); what Azure does with a push for an account that has no app; the delay; what a notification costs on this offer; and whether this offer still refuses a test notification, as it did on 2026-10-03 | step 25's one test notification, by the owner's eyes; if it is refused, the first alert that fires; the cost by meter afterwards |
| The CI job `infra` with the tests added on 2026-10-05: its minutes against its limit of 10. On this machine the suite of 428 to 444 tests took from under 6 to 29 minutes, the longer runs with other work beside them; on 2026-10-06, with the eight tests of the phone's receiver, the 452 took 21 minutes in one whole run, again with other work beside it. The limit is in `.github/workflows/ci.yml`, which this change does not edit: a job that passes it is put right by a change of that file | the first run of CI on the pull request: that workflow runs on a pull request to `main` and on a push to `main`, not on a push of a branch |


## Checking these files

```powershell
bicep build infra/main.bicep --stdout > $null          # and the other two templates; a warning is on standard error
bicep lint infra/main.bicep
python -m unittest discover -s infra -p "test_*.py"
```

Both commands must write nothing to standard error. `main.bicep` silences one warning on one line
(BCP081: Bicep 0.47.16 has no types for the environment's API version); a test takes that line
out of a copy and sees the warning come back, and sees an unused parameter still reported.
`app-inputs.bicep` silences one code from its pragma on, `no-unused-params`: its parameters are
there to be checked by Azure, and nothing in the file reads them. A test takes the pragma out of a
copy and sees the warning come back, through `main.bicep` too, and sees an unused variable still
reported.

**The check of the app's values, measured on this machine** (Bicep 0.47.16, 2026-10-03).
`bicep snapshot` works a template out offline with the values given, as a what-if does
(<https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/bicep-cli>, 2026-05-14, read
on 2026-10-03). On the template as it was at step 9 it gave the app's ID unworked, in the form that
what-if printed, and the role assignment's ID around it. On the changed one, with values of the
shapes `secrets.ps1` writes, it named all 23 resources of the run with the app, and 14 without it.
Since the alert on the workspace is left out by default (step 20), run again that day on the
template as it is now: 22 with the app, three alert rules among them; 23 with
`logVolumeAlert=true`, the fourth rule equal to the one predicted before the change; 20 with that
and `keepLogs=false`, with no workspace, no diagnostic setting and three alert rules.
It refuses an image tag of 39 or 41 characters and an empty address, and names the parameter: "The
provided value for the template parameter 'imageTag' is not valid. Length of the value should be
greater than or equal to '40'." Like a what-if, it leaves a secure value unworked, so an empty
secret passes it. The secrets were tried with `bicep local-deploy`, which is experimental and runs
a deployment on this machine: `app-inputs.bicep` with `targetScope = 'local'` added and nothing
else changed, as a module with `main.bicep`'s condition and parameters, and a second module that
waits for it as the app does. The address and each of the seven secrets left empty, and the tag at
39 and at 41 characters, failed the check's deployment, and the module that waits never ran; with
all nine given both ran; with `deployApp=false` neither ran. The same file without its ten length
decorators let an empty secret through, and the module that waits ran. The local engine's error
named no parameter ("Encountered internal server error"). Where Azure stops such a run, before the
foundation's resources are sent again or only when the check's deployment starts, is not documented
and has not been seen ([Not measured yet](#not-measured-yet)).

**With the demo's switch, on 2026-10-05** (the same Bicep, offline). The compiled template holds
23 resources: the 21 of before, the pool job and its role assignment. Worked out by
`bicep snapshot` with the switch off, it predicts what it predicted before: 22 with the app, 23
with `logVolumeAlert=true`, 14 without the app. With `demo=true`: 24, the pool job and its role
assignment among them, and 25 with the alert; with `demo=true` and no app, 14, since the job is
built only with both. That is what "the merged template creates nothing more until it is asked"
rests on, and it is all it says: with the switch off the template still writes a ninth secret
and four settings on the app
([Changing the infrastructure later](#changing-the-infrastructure-later)). A snapshot works out
no secure value, so the client key's length was not tried by it, and `bicep local-deploy` was
not run again: the eighth secret's 32 characters are read in the compiled check and have not
been refused by any engine.

**With the phone's receiver, on 2026-10-06** (the same Bicep, offline). The compiled template
holds the 23 resources it held and 24 parameters, one more: `alertPushAccount`, a plain string,
empty by default, which `app-inputs.bicep` does not check. The action group's properties are
compiled as one expression, a `union` of the three they were with a fourth that exists only
when the account is given: written as a list that may be empty, the fourth would have been sent
on every run. Worked out by `bicep snapshot`: with no account named, and with an empty one, the
run predicts the 22 resources it predicted, and the group with its three properties and one
mailbox; with an account given, the same 22, the group with one receiver of the Azure mobile app
more, named `owner-phone`, and nothing else of the run different; with an account given and no
app, 14 and no group. That is all "with it empty the template is what it was" rests on. What
Azure does with the receiver is in none of it.

**What the compiler no longer reads of the action group** (seen the same day, on copies). Merged
so, the receivers' fields are not checked when the template is compiled: `emailAddress` misspelt
in the mailbox's receiver, and then in the phone's, built and linted with exit 0 and nothing on
standard error. The plain object the group was until that day put two warnings there for the
mailbox's, BCP035 and BCP089. So "nothing on standard error" says nothing of those fields any
more. What holds them is the test that compares the group's properties, worked out, whole: it
failed on each of the two misspellings.

**With the networks of proxies, on 2026-10-06** (the same Bicep, offline). The compiled template
holds the 23 resources it held and 25 parameters, one more: `proxyNetworks`, a plain list, empty
by default, which `app-inputs.bicep` does not check. The `bff` container's settings are compiled
as one expression: its six, now a variable, and after them a second variable, one setting for
each network. Worked out by `bicep snapshot`: with no network named, and with an empty list, the
run predicts the 22 resources it predicted, and the `bff` is told its six settings, each with
what it held; with one network or with two, the same 22, the `bff` told one setting more for
each, numbered from 0, and nothing else of the run different, the `api` container included; with
the demo on, 24 either way, and neither job is told a network; with networks and no app, 14, and
no network anywhere. And worked out from `main`'s templates, at `c3766e1d`, and from these with
no network named, the run is the same, whole: 22 with the app, 23 with the alert on the
workspace, 24 with the demo on, 14 without the app, 22 with an account for the phone. That is
all "with it empty the template is what it was" rests on. What Azure does with the settings is
in none of it.

**What the compiler still reads of the `bff`'s settings** (seen the same day, on copies). The
two variables are joined with the spread operator and not with `concat`. Joined by `concat`,
the fields of a setting were no longer checked: in the six, a name misspelt, a value misspelt
and a number for a value each built and linted with exit 0 and nothing on standard error, where
`main`'s list in the container puts BCP037 there for the first two and BCP036 for the third.
Joined by spread, the three are reported again, with the same codes, and so are a name and a
value misspelt in a network's setting (BCP037). So here "nothing on standard error" still says
what it said of those fields, unlike the action group's.

`test_deploy.py` tests the deployment script's decisions against invented answers: time is a
counter and no process is started. One thing is read from the real clock: how old an execution
is, against the start time a test gives it (until 2026-10-05 this paragraph did not say so). A
few of its tests open a real connection to a server of their own on `127.0.0.1`, to see what a
dropped connection really raises; since 2026-10-05 a test built on the file's offline base that
opens a connection to any other host fails for it. `test_scripts.py` runs the two
PowerShell scripts for real against a stand-in for the Azure CLI and a stand-in for `sqlcmd`,
reads `sql-principals.sql` as text (the order of its guards and every condition, word for word;
what a server does with them is above), and reads the compiled templates: the role's nine
actions, the federated credential's subject, every rule of the policy, the two identities and the
one each resource carries, that no database credential is anywhere, the workspace and its cap, the
environment's mode and its API version, the app's name and no resource ID that reads a secret,
and the check of the app's values and what waits for it. It also runs `bicep snapshot` on copies
of the templates: every ID worked out with the app, three alert rules by default and the fourth
when it is asked for, and the short or long tag and the empty address refused. The CI job `infra`
runs the same three checks and actionlint on the workflows.

Since 2026-10-05 the two files read more than that. `test_scripts.py` runs `secrets.ps1` against
a stand-in app whose containers carry the demo's flag, and reads the compiled pool job, its role
assignment and the policy's exception. It imports `deploy.py`, sends nothing with it, and asks
the script's own names and shape checks about what a run of the template would send: the app,
the two jobs, each job's container and the demo's flag, so that a name typed on both sides
cannot drift on one. It reads three things of `deploy.py` as text, the pool job's schedule, the
bounds of its timeout and the names of its two secrets, to hold each equal to the template's;
one source file of the backend, `backend/src/AzureBank.Shared/Options/DemoOptions.cs`, to hold
every setting the template writes under the demo's section to a name the backend binds; and this
page, for four quotes of `secrets.ps1` about the demo's switch: three lines of its report, and
its refusal of two containers that disagree. Since 2026-10-06 also for three quotes about the
account of the owner's phone: three of the four lines of its report, the fourth being the one
for the variable, which no step here uses. And, later that day, for five about the networks of
proxies: four of the six lines of its report, the other two being the variable's, and its
refusal of a forwarded-headers setting that was put on the `bff` by hand. For the networks it
reads two more source files of the backend, as text: `ProxyOptions.cs`, to hold the setting the
template writes to a name the BFF binds, and `OptionsValidatorTests.cs`, whose rows of networks
the app takes and refuses are each put to the script's own rule, taken out of `secrets.ps1` by
its name: the app's rule is the authority, and the script's may not let through what it refuses.
It asks `deploy.py` to count the networks on what the template works out, and holds the tool's
name for the setting, the script's and the template's equal.
`test_deploy.py` reads seven source files of the backend as text, never built or run: three of
the BFF, for the page's tag, the route of a registration, and the status and the member of the
refusal that closes it; one of the shared library, for the error code; and three of the tool the
pool job runs, for its exit codes, the counts of its summary line and the name of its command
`recycle`.
And it reads this page and `docs/runbooks/demo-pool.md`: a heading the script names is there; a
refusal that sends its reader to [When something fails](#when-something-fails) has a row there
that quotes it, in words that stand in one sentence of the script and no other; fourteen quotes
that the steps give of what a good run prints are held (twelve until 2026-10-06, when the two
about the networks of proxies that `--check` counts were added), each a run of words that one
sentence of the script prints and that this page holds; every command of the script is told; and the table
of a pool run's exit codes is the script's own. A step's other quotes of `deploy.py` are held by
no test of this folder: among them "Pool run execution ... started." in step 26 and the two
lines that start with "Moving" in step 28. So the tests need the whole checkout, not this folder
alone, and a page that drops one of the quotes that are held fails a test.
