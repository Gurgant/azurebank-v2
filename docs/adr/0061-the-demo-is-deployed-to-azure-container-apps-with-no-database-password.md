# ADR-0061: The demo is deployed to Azure Container Apps with no database password

**Status:** Accepted · **Date:** 2026-10-02 · **Decision Makers:** Vladislav Aleshaev

**Where the code's citations point.** No code cites this record by number. The runbook is
`infra/README.md`: it is the text that changes when a step changes, and its section "If Azure says
no" holds what is done when Azure refuses a step. This record holds the reasons, what was
measured, and what is known and left as it is.

**None of the files this record describes has run on Azure.** The templates compile, the scripts
are tested against stand-ins and, where a local engine can run them, for real. The resource group
does not exist (`az group exists` answers `false`; the same command answers `true` for a group
that is there). What did run on Azure, on 2026-10-02, is a throwaway trial: a resource group in
the same subscription and region, created and deleted that day, in which requests of the shapes
these files make were sent by hand: not by these files, and not every one of them. What it
measured has a table of its own below. Every other sentence about what Azure does is marked as
read or as not measured.

## Context

Until this decision the app ran from `compose.yaml` and nowhere else. The migration had become a
one-shot the day before (ADR-0060), which is what a deployment needs.

**The money.** The subscription is a credit offer with a spending limit: no bill is possible, and
when the credit is used up everything in the subscription stops, the database included. The credit
was 86 when this was written.

**The design was approved once, and then three of its choices were reopened.** The first design
signed in to the database with two SQL users and generated passwords, created those users from a
PowerShell script holding a token, and sent the containers' output nowhere. On 2026-10-02 the owner
asked why, for each of the three. The answer was the same three times: nobody had weighed the
alternative. "Generated passwords" and "no logs" came down from the first plan, and each later
pass carried them; the script had been chosen because it needed no install. So each was weighed,
with the measurements and the reading below, and each changed.

**Measured on 2026-10-02**, on this repository's code, on local stacks and with read-only commands.
One run per row unless it says otherwise.

| What was run | What happened |
|---|---|
| A connection string with `Authentication=Active Directory Managed Identity` and a client ID, through the registration every host uses | The rewrite that fills in the connection limits keeps the sign-in and the client ID; SqlClient accepts the result when a connection is created. The build resolves Microsoft.Data.SqlClient 6.1.1 and Azure.Identity 1.14.2, and SqlClient reports a provider for that sign-in. No code change was needed |
| On a local SQL Server 17, as a user with the migrator's three roles | `CREATE USER` refused (Msg 15247), joining `db_owner` refused (Msg 15151), **a DDL trigger accepted** |
| The users file with its two guards switched off, that trigger in place, run as the database's owner | Exit 0, and afterwards the migrator and a user the trigger planted were in `db_owner` |
| The users file as it is, that trigger in place | Msg 50003, exit 1, nothing changed |
| The same with only the first check off, and then with a trigger that grants `CONTROL` and removes itself | Msg 50004, exit 1, everything rolled back; the self-removing trigger passes a check of users and role members alone and is caught by the permission list |
| The app's 16 migrations on an empty database | 0 triggers, 0 modules, and no row the file's lists do not expect |
| go-sqlcmd with a variable holding a GUID, a quote and a statement | The statement ran: `-v` is text substitution. What it created was still there after the users file refused the run, because it ran before the file's transaction began. Without `-b`, a file that stops on an error exits 0 |
| The provider's policy aliases, searched for the identity block of an app or of a job | 0 of 1,058; the alias for the replica count is found by the same search |
| The BFF on the built image, the same 31 requests, without and with `Serilog__MinimumLevel__Override__Serilog=Warning` | 26 request lines at Information became 0. The 2 request lines of 5xx answers stayed, the 11 warnings stayed kind by kind, the API's own lines were untouched |
| 400 requests to `/api/accounts` with no session, in 0.9 s, on a build of the BFF from source with that setting | 100 answered 401 and 300 were rejected by the rate limiter, and **400 warning lines** were written, 209,200 bytes. On the built image the two warnings are 538 and 510 bytes |
| One sign-in while the database is unreachable | About 28.7 KB of console text in the two hosts |
| 8 registrations at once with one address, then 8 with one handle | The address was printed whole, 4 times in 2 lines of the API's console; the handle 28 times in 14 lines. The BFF's console held neither |
| `az bicep version` on the machine that will deploy | No Bicep of its own is found. The Bicep CLI on `PATH` is 0.47.16, the one the tests use |

**Measured on Azure on 2026-10-02**, in the throwaway trial. The requests were sent by hand, with
`az rest` and go-sqlcmd (its version was not recorded), on resources of the trial's own. Each row
comes from a run that was recorded as it went, but one, the environment's, whose row says so. The
runbook's section "Measured on Azure" has each line in full. One run per row unless it says
otherwise.

| What was sent | What happened |
|---|---|
| A SQL server with the `administrators` block, Entra-only, and no SQL administrator login, on API `2023-08-01` | Accepted, the operation done after 57 s, read back as Entra-only, with a SQL administrator name the service made up (no password was sent for it). **The same request a second time: accepted, and the server read back exactly as before.** The administrator and the Entra-only switch sent again as child resources: accepted |
| go-sqlcmd as the owner, through the `az login` session | Signed in, as `dbo`. The first try was refused for the caller's address, and a firewall rule for it let the next one in after 19 s |
| A SQL-password sign-in for a login that does not exist | Refused, with "Reason: Azure Active Directory only authentication is enabled." |
| `CREATE USER [<the identity's name>] FROM EXTERNAL PROVIDER`, by the owner, who holds no directory role | Accepted. **The ID the server stored is the identity's client ID** |
| `CREATE USER ... WITH SID = <the client ID>, TYPE = E`, for two users | Accepted, and each stored ID compared equal to the client ID |
| A program on SqlClient 6.1.1 in a Container Apps job at 0.5 vCPU, signing in as each of two identities with a string of the app's shape | Each signed in as its own user. **The first `Open()` on a cold replica took 3,810 ms**, against the 10 s timeout, measured once; 1,065 ms in the job that had asked the token endpoint when it started |
| The same program, as the identity that only reads and writes | `CREATE TABLE` refused (error 262), `ALTER TABLE` refused (1088); an application lock, a locked read and an update allowed |
| The same, as the identity that may change the schema | Thirteen kinds of statement that migrations use: allowed. `CREATE USER`: refused (15247) |
| A token asked for an identity the job does not carry, and for one that does not exist | A `SqlException` with number 0 and **class 20**, around `Azure.Identity.AuthenticationFailedException`, after 40 to 71 ms |
| The right identity, where the database has no user for it | Error 18456, class 14, after 41 to 52 ms |
| An execution of a job, read through API `2026-07-01` | Its status and its container's exit code were filled: `Succeeded` and 0, `Failed` and 7 |
| An environment whose request names no mode, on API `2025-01-01` | **Refused: HTTP 400, `ExpressEnvironmentFeatureNotSupported`.** With `environmentMode: 'WorkloadProfiles'` on API `2026-07-01`: accepted, sent by hand between two recorded runs, so that answer is a note made at the time and not part of the record. Recorded afterwards: the environment, read back with the logs destination and the Consumption profile, and two jobs that ran in it. No recorded read shows its mode |
| A workspace with a daily cap of 0.05 GB and key access off, and one diagnostic setting | Accepted, and read back with that cap |
| What two jobs printed | 45 lines in the workspace. The first arrived 8 min 38 s after the setting was created. A line could be read 387 s and 398 s after it was written (the median of each job), 486 s at the most. Three runs that lasted seconds each kept their line. Billed: 438 bytes a line, for 194 characters of text |
| A custom role with the nine actions | Accepted, found when asked at the group and at the subscription, deleted |
| A custom policy definition with the effect Deny, assigned to the resource group | Accepted, and **it refused at once**: a job at 0.75 vCPU got `RequestDisallowedByPolicy` on the first try. The rule was an earlier one: four conditions of this record's rule were not in it |
| A budget of 20 a month with four e-mail notifications to an outside mailbox | Accepted and read back |
| The cost view, on the same day | No row for the resource group |

**Read on Microsoft's pages on 2026-10-02.** Each is paraphrased; the date is the page's own.

- Azure SQL's security guidance and the Well-Architected guide for SQL Database both say to prefer
  Microsoft Entra authentication and to disable SQL authentication where possible
  (<https://learn.microsoft.com/en-us/azure/azure-sql/database/secure-database>, 2026-09-10;
  <https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-sql-database>,
  2026-08-19). A page that recommends a stored SQL password for an app hosted on Azure was looked
  for and not found.
- The firewall rule "Allow Azure services" admits services in other customers' subscriptions
  (<https://learn.microsoft.com/en-us/azure/azure-sql/database/firewall-configure>, 2026-05-28),
  and a Consumption-only environment has no fixed outbound address
  (<https://learn.microsoft.com/en-us/azure/container-apps/networking>, 2025-06-25). The sign-in
  is therefore the only gate on a public endpoint.
- A server can be created with Entra-only authentication and no SQL administrator login; the
  example template gives none
  (<https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-azure-ad-only-authentication-create-server>,
  2026-03-18). The template reference for API `2023-08-01` says the `administrators` block is for
  creation only, and still lists the administrator password as required
  (<https://learn.microsoft.com/en-us/azure/templates/microsoft.sql/2023-08-01/servers>). The two
  pages do not agree.
- There is no management API for database users: they are created over a connection to the
  database (<https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-overview>,
  2026-05-15). The same page recommends a connection timeout of 30 seconds for Entra sign-ins.
- `CREATE USER ... WITH SID = ..., TYPE = E` creates a user without asking the directory, and for
  a managed identity the ID is the client ID
  (<https://learn.microsoft.com/en-us/sql/t-sql/statements/create-user-transact-sql>, 2026-02-23).
  One page names the object ID for the same statement
  (<https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-azure-ad-user-assigned-managed-identity>,
  2026-03-05). The other forms make the server look the identity up in the directory, which can
  need a directory role the owner's account does not hold (measured: it is a member with none).
  The trial settled both: the server stores the client ID, and it accepted the look-up from this
  owner.
- For a user-assigned identity the connection string's `User ID` is the client ID, and from
  SqlClient 7.0 the Entra sign-in providers are in a second package
  (<https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/azure-active-directory-authentication>,
  2026-09-21).
- A managed identity is available to every main container of an app; a request for a token must
  name a user-assigned identity, and one that names none is answered for the system-assigned
  identity, whether the app has one or not; and whoever may start a job with a template of their
  own can use the identity the job carries
  (<https://learn.microsoft.com/en-us/azure/container-apps/managed-identity>, 2025-06-03;
  <https://learn.microsoft.com/en-us/azure/container-apps/jobs>, 2026-09-16). The platform caches
  managed-identity tokens for about a day
  (<https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/managed-identities-faq>,
  2025-02-27). How long such a token stays valid for the database: not found.
- For Container Apps, the Well-Architected guide says to send console and system logs to a
  workspace
  (<https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-container-apps>,
  2026-03-17), and the observability page files the live log stream under development and test
  (<https://learn.microsoft.com/en-us/azure/container-apps/observability>). A new diagnostic
  setting can take up to 90 minutes to deliver
  (<https://learn.microsoft.com/en-us/azure/container-apps/migrate-logs-azure-monitor>, 2026-08-12).
- A workspace's daily cap is not exact: some data gets past it, more when it arrives fast, and
  that excess is billed; the reset hour cannot be chosen; the alert the page describes is a log
  search rule (<https://learn.microsoft.com/en-us/azure/azure-monitor/logs/daily-cap>, 2025-08-29).
  The first 5 GB a month of a billing account are free on the Analytics plan
  (<https://azure.microsoft.com/en-us/pricing/details/monitor/>); that this holds for the credit
  offer is stated nowhere that was read.
- From API version `2026-07-01` an execution of a job carries its container's exit code and a
  reason (<https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/jobs-executions/list>).
- Container Apps *express* has no sidecar containers, no jobs, no Azure Monitor logging and no
  workload profiles. Its FAQ says that a template which creates a new environment creates a
  standard one by default, and that an environment with no running app or job and no recent
  activity may be archived
  (<https://learn.microsoft.com/en-us/azure/container-apps/express-overview> and
  <https://learn.microsoft.com/en-us/azure/container-apps/express-faq>, both 2026-09-21; read on
  2026-10-03). On the first point this subscription did otherwise: see the table above. The
  runbook reads the environment's mode at the start of every session.
- The log of a workflow run in a public repository can be read by anyone signed in to GitHub
  (<https://docs.github.com/en/actions/how-tos/monitor-workflows/use-workflow-run-logs>), and
  GitHub does not promise that a masked value is always hidden
  (<https://docs.github.com/en/actions/reference/security/secure-use>).

Prices are from the Azure Retail Prices API for Italy North, read the same day: the database
$0.161 a day; log ingestion $2.99 a GB past the free 5 GB.

## Decision

**1. One resource group in Italy North, created by one template that a person runs, in two
steps.** The first step needs no image: the environment, the log workspace, the SQL server and
database, three identities, the custom role and the policy, fourteen things. The second adds the
app, the migration job, two role assignments, the action group and four alerts, nine things. The
workflow never creates or changes infrastructure. The environment names its mode,
`WorkloadProfiles`, on an API version that has the property: on this subscription a request that
names none is taken as Express, which has neither jobs nor a second container, and is refused
(`ExpressEnvironmentFeatureNotSupported`).

**2. One replica, zero to one, with both hosts in it.** The BFF answers on port 8080 behind an
HTTPS ingress; the API listens on `127.0.0.1:5068` and nothing outside the replica can reach it
(ADR-0055). One replica because the BFF keeps its sessions in memory (ADR-0057) and the pool is
sized for it (ADR-0058). A Deny policy on the resource group refuses any other shape, whoever
asks: a second replica, a minimum above zero, several active revisions, plain HTTP, a third
container, an init container, a container above half a vCPU, a job that is not manual. The
deployment identity cannot change a policy.

**3. The database is Azure SQL Basic, 5 DTU and 2 GB, with a lock on the database.** On the
database and not on the server, so that the temporary firewall rule of decision 5 can still be
deleted.

**4. The app and the migration job sign in to the database as two user-assigned managed
identities, the server takes Microsoft Entra sign-ins only, and no database password exists.**
`azurebank-app` reads and writes rows; `azurebank-migrate` may also change the schema. Each
connection string says how to sign in and which identity to ask a token for. Neither identity
holds a role on any Azure resource. See [the first reopened choice](#1-how-the-app-signs-in-to-the-database).

**5. The two database users are created by `infra/sql-principals.sql`, run by Microsoft's `sqlcmd`
as the server's Microsoft Entra administrator, and the file guards itself.** It refuses a database
that holds a trigger or a module, and before it commits it compares every user, role, membership,
permission and schema owner with what it expects. **Nothing else is run as administrator in that
database.** See [the second reopened choice](#2-how-the-two-database-users-are-created).

**6. What the containers print goes to one private Log Analytics workspace, capped at 0.05 GB a
day and kept 30 days, and the public workflow prints a migration's verdict and never its text.**
The BFF's one line per request is not kept. See
[the third reopened choice](#3-where-the-logs-go).

**7. A commit is deployed by one workflow, started by hand from `main`: the migration first, then
the app, then a check, and a failed check puts the app back.** The migration is ADR-0060's
one-shot, as a manual job. The check is the built page, a readiness answer of `Healthy`, and one
sign-in for an address nobody can register, which must be refused with the API's own error code:
that answer needs the BFF, the API, the schema and the database sign-in together. If the new
revision never gets ready or the check gets a wrong answer, the app goes back to the template it
had. The schema is never put back.

**8. The deployment identity can move images and start the job, and nothing else.** GitHub signs
in as it only from the environment `demo`, which the owner sets to allow `main` only and to ask for
a reviewer. Its custom role has nine actions: read and write the app and the job, start the job,
read executions, revisions and replicas. It cannot list secrets, and on every run it tries and
goes on only if Azure refuses. It has no user in the database and no right on the workspace. **It
gets no role on the two database identities**: if Azure asks for one before it accepts a change,
the run stops and the refusal goes to the owner.

**9. The seven application secrets exist only as secrets of the app.** They are generated on the
owner's machine into one file outside the repository, which is removed when the session ends. The
three Azure identifiers are secrets of the GitHub environment, so that a public log never prints
them. The two connection strings are secrets too, although they hold no password, and only the
`api` container and the job reference them: the BFF is handed neither the server's name nor the
client ID. That keeps both out of the container that faces the internet. It is not a lock:
neither is a secret.

**10. Nothing stops the app automatically, and the owner accepts that.** Four alerts send an
e-mail: requests, data out and replica time on the app, and log lines on the workspace. The owner
stops the app by hand. The logs are switched off by rule: any cost on their meter, a day above
twice the cap, or no line ever arriving.

**11. What Azure accepts was measured before the first run, and what to do when it refuses is
decided before it too.** The tool has three ways to sign in, the user creation a second form, the
logs an off switch, the policy and the fourth alert a switch each; each is written with the
refusal that triggers it. Anything else that is refused is a stop. The server has one shape and a
user carries one kind of ID, its identity's client ID: the trial showed that the shape is
accepted and which ID the server stores, so the two other shapes of the server and the switch for
the object ID that this record first carried were taken out.

## The three reopened choices

### 1. How the app signs in to the database

**What had been approved:** two SQL users with generated passwords, kept as secrets of the app and
the job; a SQL administrator password made new at every run and used by nothing.

**Why it changed.**

- Microsoft recommends the opposite on every page read, and stored SQL passwords on none.
- The server's sign-in is reachable from every Azure customer, and the app's address cannot be
  pinned. With Entra-only there is no password there to guess or to steal.
- Three secrets stop existing, with their generation, their rotation step and their read-back
  from the deployed app. The compiled template went from 10 secure parameters to 7, and the word
  "password" from 11 occurrences to 0.
- The application's code did not have to change (the first row of the measurements).

**Where the approved choice was better.**

| | With passwords | With identities |
|---|---|---|
| Rehearsal | The sign-in and the users script ran on the local stack | Neither can run off Azure: the local engine refuses `TYPE = E`, and a workstation has no managed identity. Both were seen on Azure once, by hand, in the trial |
| Which container holds the credential | Only `api` had the string | Every main container of the app can use the identity. Code in the BFF, which faces the internet, could ask for a database token. It is handed neither the server's name nor the client ID, and neither is a secret |
| A migration nobody can read | The owner could run the tools image locally with the password | No managed identity off Azure: the last resort is the source at that commit, signed in as the owner |
| Cutting off a thief | Change both passwords | No single step: an incident procedure, in the runbook |

**Measured on Azure, by hand.** API `2023-08-01` accepts a server request that gives no SQL
administrator login (the service named one of its own, and no password was sent for it), and the
same request a second time leaves the server alone. A user created from the client ID signs in,
and the client ID is what the server stores. The first open on a cold replica took 3,810 ms
against the 10 s connect timeout, once. When no token comes the driver throws a `SqlException` of
class 20, which the API's handler answers as the 503 of a database it cannot reach; with no user
for the identity it throws error 18456, class 14, which that handler leaves a 500 (the two errors
were measured; the two answers were read in `ServiceUnavailableExceptionHandler.cs`, not run). A
job carrying one identity gets no token for the other. The server names Entra-only in its refusal
of a SQL sign-in.

**Not measured.** The same things done by these files: the template's own second run, and the two
users the script makes. That a request for a token which names no identity gets none. Whether a
change that touches no identity needs a right on the attached one.

**If Azure refuses.** The server refused, or disturbed by a second run of the template: a stop.
This record first listed two more shapes of the server as steps down; they are gone, because the
first shape was accepted, twice. A slow first token: nothing automatic. The timeout stays 10 s,
one 503 after a cold start is accepted, and a larger value is a decision in ADR-0058, because that
timeout also bounds each commit.

### 2. How the two database users are created

**What had been approved:** about a hundred lines of PowerShell around the SQL: a token from `az`,
the older ADO.NET provider, the passwords as bound parameters. It had been chosen because nothing
had to be installed.

**Why it changed.**

- "It needs an install" is not a reason against a tool, by the owner's rule; and the bound
  parameters existed for the passwords, which are gone.
- A reviewer now reads the SQL itself and one command line, the script holds no token, and it is
  the tool Microsoft's own pipeline action runs.
- The form of `CREATE USER` that asks the directory nothing is the only one that does not depend
  on a directory role the owner's account does not hold. (Measured since, in the trial: the form
  that looks the identity up was accepted from this owner as well. What stays is the other
  reason: the form used trusts no name.)

**What the measurements added.** That form has a price, and the first version of the file did not
pay it. The file runs as the owner of the database, and so does a DDL trigger its statements fire.
The migrator may create such a trigger, and through it a statement that asks the directory nothing
could make anyone in the directory an owner (the second and third rows of the measurements). So
the file refuses a database that holds any code, and inside its transaction, before the commit, it
lists everything that can hold a right. A check of users and role members alone was not enough: a
trigger that grants a permission and removes itself got past it, and the permission list is what
catches it.

**Where the approved choice was better.** It ran for real on the local SQL Server; its bound
parameters could not become SQL, where `-v` is text; and it read the firewall refusal as an error
number, where a tool gives text. The second is answered by the runner, which parses each ID as a
GUID and passes on what it parsed. The file cannot answer it: it builds each statement from a
typed value, but text that `sqlcmd` put in would run above its transaction (measured). The third
stays: the refusal is known by its sentence, because `sqlcmd` prints no number for an error at
sign-in (measured with a refused login on a local server). The first is lost: on a local engine
the file ran with stand-in users, and the file itself has run on no Azure SQL database.

**Measured on Azure, by hand.** Azure SQL runs `CREATE USER ... WITH SID, TYPE = E`. go-sqlcmd
signs in through the `az login` session, and the server's refusal of an address names that
address.

**Not measured.** The file itself on Azure SQL: that a new database there has no trigger, no
module and only the rows the file expects, and that dropping and creating a user inside its
transaction works there. Its second form, `FROM EXTERNAL PROVIDER WITH OBJECT_ID`: the trial
looked the identity up by its name.

**If Azure refuses.** The sign-in method fails: a second method, then the older ODBC `sqlcmd`
with a sign-in window, the same file each time. The lists refuse a database nobody has touched:
the file's expected rows are corrected, which is a defect and not a choice. The statement is
refused: `FROM EXTERNAL PROVIDER WITH OBJECT_ID`, by ID and never by a name. That is refused too:
a stop.

### 3. Where the logs go

**What had been approved:** logs to nowhere, and the owner watching the portal while the first
migration ran. The workflow printed nothing of the migration.

**Why it changed.**

- "Nowhere" had been ratified on one number, an uncapped workspace at 10 GB a day. A capped one
  had never been priced.
- "The owner watches the portal" rested on a screen that is not documented for a job, and a
  finished execution is not promised to be readable.
- Microsoft's guidance is one-sided: keep console and system logs in a workspace.
- It answers both halves of the question, the migration's output and the running app's, and adds
  no secret and no right: the Azure Monitor destination needs no workspace key, and the
  deployment identity gets nothing on the workspace.

**What stayed from the approved choice:** the workflow does not print the migration's text. That
half was right. The text can name the server, a caller's address, and a value from a database
error (the measurements), and the log is public. The workflow prints one line: the execution's
name, status, times, exit code and a reason, and inside GitHub Actions the reason only if it is
one plain word.

**Where the approved choice was better, and it is not small.** "Nowhere" costs nothing with
certainty and gives a stranger nothing to fill. The workspace is a meter:

- 0.05 GB a day is 1.50 to 1.55 GB a month, under the free 5 GB. If the free amount does not apply
  to the offer, the worst case under the cap is 1.55 GB at $2.99: **$4.63 a month**.
- The cap is not a hard bound, by how much is not stated, and the excess is billed.
- The app's own rate limiter does not bound the log: a rejected request writes a warning too.
  Computed from what the trial's lines were billed (about 244 bytes a line on top of the text): a
  day's cap is about 65,100 such requests, and past the free amount each million costs about
  $2.30 on the log meter on top of $0.40 on the request meter, nearly seven times faster. By the
  built image's console bytes alone (524 a warning) the two numbers would be about 95,400 and
  $1.57.
- A stranger can fill the day's cap on purpose, and the log is then dark until its reset.

The request line went and the warnings stayed for that reason: the request line was written for
every page and file, which are served before the rate limiter, and was most of what a day writes;
the warnings are the security record, the rate limiter's rejections among them (ADR-0013).

**Measured on Azure, by hand.** The offer accepts the workspace, the destination, the setting and
a cap of 0.05. With access by key off the platform still delivers: 45 lines arrived, the first
under nine minutes after the setting was created. The lines of three runs that lasted seconds
reached the table. A line of a job was billed 438 bytes. API `2026-07-01` fills the exit code in
Italy North. And a line could be read about six and a half minutes after it was written (the
median), eight at the most: the workflow could not print a migration's text in time even if that
were wanted.

**Not measured.** That the free 5 GB apply: the cost view had no row on the same day. That the
alert on the workspace is accepted, and at no cost. What a line of the app is billed. How far the
cap overshoots.

**If Azure refuses.** The offer refuses any part: the logs are switched off and nothing is kept, as
first approved, with the verdict line still printed. No line arrives: access by key is allowed
again and the read repeated; still none, the logs go off. Any cost on the meter, or a day above
twice the cap: the logs go off. The alert is refused: it is left out, one of four.

## Rejected

- **System-assigned identities.** The users could be created only after the app exists, and
  deleting the app would orphan its user. User-assigned identities are created with the first
  step, so the users are made once, with their final IDs.
- **`Authentication=Active Directory Default` in the deployed strings.** The driver walks several
  credentials and can pick another when the host changes; Microsoft's page says to name the mode
  in production. It is used in one place only, the last-resort migration from the owner's
  checkout.
- **A role for the deployment identity on the two database identities**, if Azure asks for one. It
  would let that identity attach the schema-changing identity to the app that faces the internet.
  A policy rule that would make such a role safe cannot be written: there is no alias for it.
- **A second connect timeout of 30 s when the first token is slow.** It would rewrite ADR-0058's
  sums without anyone deciding to.
- **A `DENY` for the app's identity on the migrations history table.** The table does not exist
  when the users are created, so it would be one more run as administrator after the first
  migration, for an identity that already reads and writes every row.
- **Creating the users from the workflow, or from a deployment script in the template.** The first
  makes the workflow an administrator of the database; the second costs a storage account and a
  container instance while they exist and widens the deployment identity's role.
- **Creating the users in the portal by hand.** It cannot be repeated by a command, and nothing of
  it is in the repository.
- **`FROM EXTERNAL PROVIDER` with the identity's name.** It trusts a public name in a directory
  where any member can create an identity of that name.
- **Reading the migration's live stream from the workflow, or running the migration on GitHub's
  runner.** Both print the text in a public log and leave the running app with no logs. The first
  needs a token that Azure describes as good for opening a console as well; the second puts the
  schema-changing sign-in on the runner.
- **Printing the migration's text after scrubbing it.** The server's name and an address are not
  registered secrets, and a scrubber would be a promise nobody can test.
- **The older log destination with the workspace's shared key.** A new secret in the template, on
  a road the product's team announced as legacy in August 2026.
- **An alert that the cap was reached.** The documented one is a log search rule, $0.50 a month or
  more.
- **Built-in roles for the deployment identity.** The ones that can deploy can also list every
  secret and delete.
- **A rollback action in the workflow.** A workflow that deploys any published tag without the
  migration is a second, weaker road. Going back by hand is `deploy.py --app-only`, from the
  owner's terminal, refused inside GitHub Actions.

## Consequences

**Positive**

- No database password exists: none to generate, store, rotate, guess or leak.
- A reviewer reads the SQL that creates the users, and the template says in words how the app
  signs in.
- A failed migration leaves a verdict in public and its text in private, and the running app has
  logs for 30 days.
- The deployment identity cannot list a secret, and a refusal is made to happen on every run.
- ADR-0060's condition, that the login a deployment gives `migrate` cannot create a database, is
  met: the migrator is a user inside one database, with no right on the server.

**Negative: known, and left as it is**

- **The BFF container can ask for a database token.** The identity belongs to the app, not to a
  container. What the BFF is not handed, the server's name and the client ID, are identifiers and
  not secrets. The real fix is the API in an app of its own; it is not done here.
- **The log's cap is not a hard bound**, and the rate limiter does not bound what a stranger can
  write. One warning per caller per window, instead of one per request, would let it; that change
  touches the BFF's security logging and is not made here.
- **Nothing stops the app automatically.** Requests and data out have no bound but the credit.
- **The sign-in and the user creation cannot be rehearsed off Azure.** The trial saw both there
  once, with users of its own. The first session makes both happen for the real users before any
  image exists, with a throwaway job that signs in as each identity.
- **Whoever can act as the deployment identity can run code as either database identity**, by
  writing the app or the job. As the migrator it can leave code in the database that runs as
  whoever next changes users there; the users file refuses such a database. The boundary is who
  can run a job in the environment `demo`.
- **No single step cuts off a stolen token.** How long one stays valid was not found. The incident
  procedure deletes the app and the job, makes the two identities again, drops both database
  users through the users file, and only then runs the template.
- **The log can hold what ADR-0017 keeps out of the application's own messages**: an e-mail
  address or a handle inside a database error's text (measured), and by the same road any other
  unique value. It is read by the owner and by an administrator of the directory.
- **The workspace is a debugging record, not an audit trail.** Whoever wants to act unseen can
  fill the cap first. Sign-in attempts on the server are still recorded nowhere.
- **The app's identity can edit the migrations history table.**
- **One account** is the only administrator of the database and the only Owner of the
  subscription.
- A first sign-in after a cold start may answer one 503. In the trial the first open took
  3,810 ms, measured once.
- ADR-0058's first precondition, one replica, is what the template sets and what the policy is
  expected to refuse to exceed. Until the policy is seen refusing a second replica, that record's
  sentence that no code enforces it stands. The trial saw an earlier form of the rule refuse a job
  above half a vCPU; it did not ask for a second replica.

**Neutral**

- Local runs and CI are unchanged: `compose.yaml` and the jobs keep `sa` and a password, because
  no managed identity exists off Azure.
- The roles of the two database users are the ones the first design had.
- The policy, the lock and the custom role are the ones the first design had.
- The owner's machine needs Microsoft's `sqlcmd` (go-sqlcmd 1.10.0 or later). The script starts
  it by its full path and checks its version and signature first.

## Validation

**Tests.**

- `backend/tests/AzureBank.Tests`, `Unit/Data/ConnectionDefaultsTests`: the limits rewrite keeps
  the sign-in of a managed-identity string (with the keyword removed after the rewrite, the test
  fails); SqlClient carries the provider for that sign-in, so that a move to SqlClient 7 without
  its second package fails here instead of at the first open on Azure.
  `Unit/Tools/SeederCommandTests`: `migrate`'s line for a refused login names both ways to sign
  in.
- `infra/test_scripts.py` and `infra/test_deploy.py`, 259 tests: the two PowerShell scripts run
  for real against a stand-in for the Azure CLI and a stand-in for `sqlcmd`; the users file is
  read as text, to keep each guard, every `WHERE` and every `IF` where it is; the compiled
  templates are read (the two identities and the one each resource carries, no database
  credential anywhere, only the `api` container and the job handed a connection string, the
  workspace and its cap, the environment's mode and API version, the four alerts, the role's nine
  actions, every rule of the policy);
  `deploy.py`'s decisions against invented answers. While they were written, single changes were
  made to the scripts, the users file and `deploy.py`, and the suite had to fail: 108, none left
  uncaught. That count had not tried enough: a review loosened conditions of the users file by
  one word each, and of thirteen such changes ten left the tests green. Every condition of the
  file is pinned now. With the changes made for the later fixes that is 32 more, and all 32 fail.
  So do the nineteen made on 2026-10-03: to the environment's mode and API version, to what was
  left when the switch for the object ID went, and to how the verdict is read.

**Measured**, beyond the Context table.

- The compiled template: 20 resources, 19 parameters of which 7 secure and 2 required, 8 outputs,
  none secure; 14 resources without the app and 9 more with it. `bicep build` and `bicep lint`
  exit 0 with nothing on standard error for both templates; an unused parameter puts a warning
  there. One warning is silenced, on one line: BCP081, because Bicep 0.47.16 has no types for the
  environment's API version. Without that line the warning is back.
- The users file on a local SQL Server 17 with go-sqlcmd 1.10.0, with three substitutions (the
  database's name, a user made from a disabled SQL login whose ID is the 16 bytes asked for, and
  the user type that goes with it): it commits on a clean database and a second run changes
  nothing; an identity made again replaces its own user and no other; thirteen single oddities
  are each refused with the offending name printed, among them a grant to `public` on a table, a
  `DENY` for `public` and `CONTROL` for the migrator. Run again on 2026-10-03, on the file
  without the switch for the object ID.
- The runner's tool checks against the real programs: go-sqlcmd passes, the ODBC `sqlcmd` is
  refused as the default tool and accepted on its own road, and a program validly signed by
  someone else is refused.
- The last-resort migration's command line, with `Active Directory Default`, against a server name
  that does not resolve: the tool reads the string, tries once and exits 1 with the last answer on
  its last line. No token was asked for.
- The runbook: its 41 PowerShell blocks parse; every Azure CLI command and flag, every script
  parameter and every `deploy.py` option it names is in that tool's own help; the functions that
  list the identities, create the sign-in probe and read the environment's mode ran against a
  stand-in for the CLI. The sign-in probe's program ran against a local SQL Server and gave each
  of its exit codes but 3, which needs a token refused on Azure.

**Not measured:** everything these files themselves do on Azure, since none has run there; what
is listed under "Not measured" in the three sections above; the app with its two containers, its
probes and its scale to zero; the deployment identity's nine actions against an app that carries
an identity; the automatic put-back on a real failure; that the policy refuses a second replica;
any alert firing; the meters after 48 hours; every command of the runbook's sections on switching
the logs off, on a theft and on removal. The runbook's "Not measured yet" lists each with the
step where it shows.

## What would change this

- **Azure refuses a step:** the switch written for it where there is one (the sign-in method, the
  second form of user creation, the logs, the policy, the fourth alert), a stop otherwise, and a
  dated note here saying which.
- **A cost on the log meter, or a day above twice the cap:** the logs go off, and decision 6
  becomes "nothing is kept".
- **The first token regularly takes longer than 10 s:** the connect timeout, decided in ADR-0058
  with its table worked again.
- **A second replica, or the API in an app of its own:** a shared session store first (ADR-0057),
  the pool (ADR-0058), and the identity then belongs to the API's app alone.
- **A move to SqlClient 7:** its second package in the same change; a test fails until then.
- **The link becomes public:** one rate-limit warning per caller per window; deployment by image
  digest; a GitHub token on the owner's machine that cannot edit the environment.

## Related

- [ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md): the one-shot the
  migration job runs, its exit codes, and the rule that it never creates a database on Azure SQL.
- [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md): the connection limits
  the hosts open with, the one-replica precondition, and what a database that does not answer
  turns into.
- [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md): why the BFF runs
  as one replica.
- [ADR-0055](0055-the-api-serves-one-client-the-bff.md): the API on loopback, behind the BFF's key.
- [ADR-0017](0017-pii-redaction-codeql-barrier.md): what a log line of the application may name.
- [ADR-0013](0013-registration-user-enumeration.md): the rate limiter whose rejections the log
  keeps.
- `infra/README.md`: the runbook, what the trial measured on Azure, what is done when Azure
  refuses, and what is not measured yet.
