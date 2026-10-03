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
([Measured on Azure](#measured-on-azure)). What ran there before, on 2026-10-02, is a throwaway
trial: a resource group in the same subscription and region, created and deleted that day, in
which requests of the shapes this folder makes were sent by hand, with `az rest` and go-sqlcmd,
and not by this folder's template or scripts. Not every request of this folder was among them.
What the trial saw is under [Measured on Azure](#measured-on-azure) as well. The other facts
marked *measured* were read on those days, from Azure or GitHub with read-only commands, on a
local stack of this code, or on a local SQL Server. What only the rest of the first deployment
can show is listed under [Not measured yet](#not-measured-yet), every value below that no run
has shown is marked as expected, and what to do when Azure refuses a step is written down before
the step runs ([If Azure says no](#if-azure-says-no)).

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
| the policy definition | "AzureBank: one small replica, manual jobs" (`guardrails.bicep`), written at subscription level because a custom definition cannot live in a resource group. It refuses nothing by itself |
| `azurebank-shape` | That policy assigned to the resource group, effect Deny. It refuses, whoever asks: more than one replica, a minimum above zero, several active revisions, plain HTTP, more than two containers, an init container, a container above half a vCPU; and for a job, a trigger other than Manual, parallel runs, an init container, a container above half a vCPU |

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

**With `deployApp=true`: nine more**

| Resource | What it is |
| --- | --- |
| `azurebank` | The app: the BFF (0.25 vCPU, 0.5 GiB) and the API (0.5 vCPU, 1 GiB) in one replica, zero to one replica, single revision, with the identity `azurebank-app` attached. HTTPS ingress to the BFF's port 8080. The API listens on `127.0.0.1:5068` only: nothing outside the replica can reach it. Three probes, on the BFF. Eight secrets, each reaching a container by reference: seven application keys and the connection string, which holds a server name and a client ID and no password. The BFF does not keep its one line per request (`Serilog__MinimumLevel__Override__Serilog=Warning`); its warnings and its 5xx lines stay |
| `azurebank-migrate` | A manual job: the tools image with the argument `migrate`, no retry, 600 s, the identity `azurebank-migrate` attached, one secret (its own connection string, no password) |
| two role assignments | The custom role, to the deployment identity, on the app and on the job and nowhere else. From here the workflow can change the app |
| `azurebank-owner` | An action group with one e-mail receiver, given as a parameter |
| four alert rules | E-mail only. On the app: more than 66,667 requests in an hour; more than 3.3 GiB sent in a day; the replica running more than about 2.2 hours in a day (an average replica count above 0.093). On the workspace: more than 50,000 records ingested in an hour, which are its lines if each measurement of the metric is one record (not yet measured: step 20) |

**And a check that creates nothing.** A run with `deployApp=true` also deploys `app-inputs.bicep`
as the nested deployment `azurebank-app-inputs`. Its parameters are the values the app needs, each
with the length it must have: the image tag exactly 40 characters, the alerts' address and the
seven secrets at least one character (the seven stay secure parameters there too). A value that
does not fit fails that deployment, and the app, the job and the action group wait for it, so none
of them is sent without its values: seen offline and on a local engine
([Checking these files](#checking-these-files)), not yet on Azure. The check used to be the app's
name, through `fail()`. A what-if works out no expression that reads a secure parameter, and on
2026-10-03 it could name neither the app nor the role assignment on it (step 9). Now the name is
the plain `azurebank`, and the same what-if, run again that day, named both.

The environment variables of the two containers are the ones `compose.yaml` sets, plus the one
Serilog setting on the BFF. The connection limits are the hosts' own defaults (ADR-0058); the
template sets none.

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
| Metric alert rules | $0.10 a month each | the first 10; four are used |
| Environment management, private endpoint, planned maintenance; Dedicated plan | $0.13 an hour each; $0.10 an hour | none: this template uses none of them, and they must read 0 |

**Expected each month:** the database, $4.83 to $4.99. The app costs nothing while it stays inside
the free amounts: 0.75 vCPU and 1.5 GiB use both up together after 66.7 hours of a running replica.
Past that, a replica-hour is $0.1134.

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
  A cap of 0.05 GB is about 114,000 lines of that size a day (computed). No line of the app has
  reached a workspace yet: the numbers below are its console bytes, measured locally, with that
  overhead added where it says "computed".

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

**What warns:** the four alert rules, by e-mail. Nothing warns that the cap itself was reached: the
alert Microsoft documents for that is a log search rule, $0.50 a month or more, and is not used.
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

Two people act below. **The owner** signs in, clicks in GitHub's settings, approves a deployment
and reads the mailbox. **The operator** types the commands in a terminal where the owner has run
`az login` and `gh auth login`; it can be the owner. Every step that writes is run on the owner's
word, given for that step.

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
needs the workflow on `main`, and the GitHub environment in place before that.

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
  `azurebank-probe` is the leftover of step 7: delete it.
- **Identities:** before the app exists, nothing is listed. Afterwards `azurebank: azurebank-app`
  and `azurebank-migrate: azurebank-migrate`, and nothing else: each database identity on exactly
  one resource.
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
| The same, asked of the identity | `az identity list-resources -g $group -n azurebank-app`, and for `azurebank-migrate`. On 2026-10-03, before the app, it answered `[]` for both; after step 15 nobody has seen it answer | No resource yet; after step 15, exactly one each. If the call does not answer, it is dropped and `Show-Identities` stands alone |
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
    ./infra/secrets.ps1 -Action New -DeployApp -ImageTag $sha   # seven "generated", thrown away below
    Invoke-Template 'app'                                       # answer "no"
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

Expected in the what-if: nine resources to create (the app, the job, two role assignments, the
action group, four alerts), the four `Modify` lines of step 3, nothing deleted, nothing
`Unsupported`, and no refusal. The check of the app's values, `azurebank-app-inputs`, is a nested
deployment and creates nothing, so it should have no line, as the policy's module had none at
step 3.

On 2026-10-03, on the template as it was then, this what-if listed seven to create (the job, one
role assignment, the action group, the four alerts), the same four `Modify` lines as step 3, and
two lines `Unsupported`: the app, printed as its unworked ID, an expression around `fail(...)`, and
one that `Show-WhatIf` printed as `Microsoft.Authorization/roleDefinitions`. That second one is the
role assignment on the app: its unworked ID holds the app's, and the last `/providers/` in it is
the role definition's, where the function reads a type. The template's check of the app's values
had made the app's name an expression that reads the secure parameters, and a what-if works out
none of those. The check is now in `app-inputs.bicep` and the name is `azurebank`; worked out
offline the same way, the template names all nine ([Checking these files](#checking-these-files)).
Run again on the changed template later that day, and answered "no" again, this what-if listed
what is expected above: nine to create, the same four `Modify` lines, nothing `Unsupported`,
nothing to delete, and no line for the check ([Measured on Azure](#measured-on-azure)).

`secrets.ps1` takes the address the alerts write to from `-AlertEmail`; without it from the
variable `AZUREBANK_ALERT_EMAIL`; without that from the address the deployed alerts already use;
and only then from the signed-in account's own mailbox. Its report names which, never the address.
Example: `-AlertEmail owner@example.invalid`.

#### 10. End of the first session (operator)

When the lines of the probe job are due, look for them: in the portal, the workspace
`azurebank-logs`, Logs, the query
`ContainerAppConsoleLogs | where JobName == 'azurebank-probe' | project TimeGenerated, Log`.
Microsoft's page allows a new diagnostic setting up to 90 minutes before it delivers. In the trial
the first line arrived under nine minutes after the setting was created, and a line could be read
about six and a half minutes after it was written (the median), eight at the most. If the session
ends first, this is the first step of the second one. The probe job restores and compiles, so it
says nothing about a run that lasts seconds: in the trial three such runs each kept their line,
and the first deployments show it for `migrate`.

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
value.

### Second session: with the workflow on `main`

#### 13. Look before writing (operator)

The reads of step 1, `Assert-EnvironmentMode` among them, and the two `gh api` reads of step 11.

#### 14. The images (operator, **writes**; then the owner, in the browser, **cannot be undone**)

```powershell
gh workflow run deploy.yml --ref main -f action=build-push
```

If the run stops with "The registry gave no clear answer" on a package that has never been
published, run it once more with `-f first_publication=true` (see
[When something fails](#when-something-fails)).

The owner then opens each of the three packages, Package settings, Change visibility, **Public**.
A package cannot be made private again; it can only be deleted. Then, with no login at all:

```powershell
$sha = '<the full SHA of the commit that was built>'
$empty = New-Item -ItemType Directory -Path (Join-Path $env:TEMP "docker-$PID")
'api', 'bff', 'tools' | ForEach-Object { docker --config $empty manifest inspect "ghcr.io/gurgant/azurebank-${_}:$sha" > $null; "$_ exit $LASTEXITCODE" }
```

Three times `exit 0`. Measured today, before any package exists: the registry answers `denied` to
an anonymous request for a package that is private or absent.

#### 15. The app (operator, **writes**)

```powershell
try {
    ./infra/secrets.ps1 -Action New -DeployApp -ImageTag $sha   # seven "generated" on the first run
    Invoke-Template 'app'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
Test-Path $folder                                               # False
```

Expected in the what-if: the nine resources of step 9. No database user is touched and no password
is set: the users of step 6 are the ones the app signs in as.

Then read back (expected values), and make two things happen on purpose:

| Claim | Read | Expected |
| --- | --- | --- |
| Zero to one replica, one revision | `az containerapp show -n azurebank -g $group --query properties.template.scale`; `--query properties.configuration.activeRevisionsMode` | 0, 1; `Single` |
| The API is not exposed | `--query properties.configuration.ingress` | external, target port 8080, `allowInsecure` false, no additional port mapping |
| Secrets by name only | `az containerapp secret list -n azurebank -g $group --query '[].name'`; `az containerapp job secret list -n azurebank-migrate -g $group --query '[].name'` | eight names; one name |
| One database identity each | `Show-Identities` | `azurebank: azurebank-app`; `azurebank-migrate: azurebank-migrate` |
| The job | `az containerapp job show -n azurebank-migrate -g $group --query properties.configuration` | `Manual`, retry limit 0, timeout 600, parallelism 1 |
| Roles on the app and the job only | `az role assignment list --assignee <principal id of azurebank-deploy> --all`; the same for the two database identities | exactly two rows, the custom role, scopes ending `/containerApps/azurebank` and `/jobs/azurebank-migrate`; no row for `azurebank-app` or `azurebank-migrate` |
| The alerts | `az monitor metrics alert list -g $group`; `az monitor action-group show -n azurebank-owner -g $group` | four rules, enabled, three on the app and one on the workspace; one e-mail receiver |

1. **The policy must refuse.** As the owner, ask for two replicas. The request must fail with
   `RequestDisallowedByPolicy`. If it is accepted, put 1 back at once: the policy does not work.

   ```powershell
   $app = az containerapp show --name azurebank --resource-group $group --query id --output tsv
   '{"properties":{"template":{"scale":{"minReplicas":0,"maxReplicas":2}}}}' | Set-Content "$env:TEMP\scale.json"
   az rest --method patch --url "https://management.azure.com${app}?api-version=2025-01-01" --body "@$env:TEMP\scale.json"
   Remove-Item "$env:TEMP\scale.json"
   ```

2. **An alert e-mail must arrive.** One test notification, to the address the action group already
   holds; the owner reads the mailbox. The portal's Test button on the action group does the same.

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

If Azure refuses the workflow's change of the job or of the app and names
`userAssignedIdentities/assign/action`, the run stops with one sentence and Azure's words. **No
role is added for it**: see [If Azure says no](#if-azure-says-no).

#### 18. The road back, once (operator, **writes**)

So that it is not first tried on a bad day:

```powershell
python infra/deploy.py --app-only
```

#### 19. The last-resort migration road, once (operator, **writes** only the temporary firewall rule)

[A migration nobody can read](#a-migration-nobody-can-read), with nothing left to migrate.

#### 20. What the alert on the workspace counts (operator; **writes** only to switch it off)

The fourth alert reads the metric `Ingestion Volume` with the aggregation `Count`. Microsoft's page
on the workspace's metrics calls it the number of records ingested into a workspace or a table,
and lists `Count` as its default aggregation
(<https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-metrics/microsoft-operationalinsights-workspaces-metrics>,
2026-07-31). It does not say whether one measurement of the metric is one record. If one stands
for several, `Count` counts measurements, and the rule may never see 50,000 in an hour however many
lines arrive. This step reads the metric and a query of the rows over the same hour: the hour in
which step 16's migration started, if it started ten minutes or more from either end of it, and
otherwise the hour of the first run of step 17. It must have ended more than an hour ago.

```powershell
$start = ([datetimeoffset]'<the hour, as 2026-10-10T14:00:00Z>').UtcDateTime
$utc = { param([int]$Minutes) $start.AddMinutes($Minutes).ToString("yyyy-MM-ddTHH:mm:ss'Z'", [cultureinfo]::InvariantCulture) }
$workspace = az monitor log-analytics workspace show --resource-group $group --workspace-name azurebank-logs --query id --output tsv
$customer = az monitor log-analytics workspace show --resource-group $group --workspace-name azurebank-logs --query customerId --output tsv

# What the alert reads: the metric's Count for that hour, one line.
az monitor metrics list --resource $workspace --metrics 'Ingestion Volume' --aggregation Count --interval PT1H `
    --start-time (& $utc 0) --end-time (& $utc 60) --query 'value[0].timeseries[0].data[].[timeStamp, count]' --output tsv

# What the workspace holds, by table and by the time each row was ingested: in the hour's middle
# forty minutes, in the hour, and in the hour widened by ten minutes at each end.
$between = { param([int]$From, [int]$To) "Ingested >= datetime($(& $utc $From)) and Ingested < datetime($(& $utc $To))" }
@{ query = "union withsource = SourceTable * | extend Ingested = ingestion_time() | where $(& $between -10 70) " +
    "| summarize Middle = countif($(& $between 10 50)), Hour = countif($(& $between 0 60)), Widened = count() by SourceTable" } |
    ConvertTo-Json | Set-Content "$env:TEMP\ingested.json"
$rows = @((az rest --method post --url "https://api.loganalytics.io/v1/workspaces/$customer/query" --resource https://api.loganalytics.io `
    --body "@$env:TEMP\ingested.json" | ConvertFrom-Json).tables[0].rows)
Remove-Item "$env:TEMP\ingested.json"
$rows | ForEach-Object { '{0}: {1}, {2}, {3}' -f $_[0], $_[1], $_[2], $_[3] }
'every table: {0}, {1}, {2}' -f @(1, 2, 3 | ForEach-Object { $column = $_; ($rows | ForEach-Object { [long]$_[$column] } | Measure-Object -Sum).Sum })
```

The query is sent as `deploy.py --job-log` sends its own. Ten minutes is more than the eight the
trial's lines took to be readable. The metric and the query agree if the metric's count is at
least the first of the three totals and at most the last. If the first total is 0, the hour proves
nothing: take the hour of the next run of step 17.

- **They agree:** the alert stays. The metric's count and the three totals go into
  [Measured on Azure](#measured-on-azure).
- **They do not:** the alert does not count lines, and it is switched off:
  `az monitor metrics alert delete --name azurebank-log-volume --resource-group $group`, and every
  later run of the template passes `@('logVolumeAlert=false')` (the file does not remember it:
  [If Azure says no](#if-azure-says-no)). The four numbers and the reason go into
  [Measured on Azure](#measured-on-azure). An alert that does count lines is a log search rule,
  and it is billed: $0.50 a month evaluated every 15 minutes, as this rule is, $1.50 every 5 (the
  Retail Prices API, Italy North). It is the owner's decision, and nothing here creates one.

#### 21. From outside, and the end (operator)

```powershell
$site = az containerapp show --name azurebank --resource-group $group --query properties.configuration.ingress.fqdn --output tsv
curl.exe --silent --head "http://$site/"                                    # a redirect to https
curl.exe --silent "https://$site/health/ready"                              # Healthy
curl.exe --silent --output NUL --write-out '%{http_code}' --request POST "https://$site/api/auth/login"   # 404
curl.exe --silent --max-time 10 "https://${site}:5068/"                     # no connection
az logout
```

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
  The alert's threshold and the "about 65,100" above are then worked again from the app's own
  billed size. The trial read `_BilledSize` and the `Usage` table for its jobs' lines; neither of
  these two queries has been run as it is written here.

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
| The deployment is refused on the **policy definition** | Run it again with the policy off (`Invoke-Template 'foundation' @('denyPolicy=false')`), pass the same override on every later run, and write down that the shape then rests on `deploy.py`'s own check alone. The trial's definition was an earlier one, without four of this rule's conditions; this rule, all of them in it, was accepted at step 2 on 2026-10-03 ([Measured on Azure](#measured-on-azure)) |
| It is refused on the **custom role** or on the **SQL server**; or the **second run** is refused, or its what-if shows the administrator changed or removed | Stop. There is one shape of the server in this folder and no other is written down: sent by hand it was accepted, twice, and so it was from the template at steps 2 and 3 on 2026-10-03. And there is no fallback that keeps the deployment identity away from the secrets |
| It is refused on the **workspace**, the destination, the diagnostic setting or the cap | `./infra/secrets.ps1 -Action New -LogsOff`, run again, and say so: nothing is kept then. If a workspace or a setting was created on the way, [Switching the logs off](#switching-the-logs-off) |
| A read in step 4 differs | A defect in the template: fix it before going on |
| Anything else is refused in either run: the lock, the federated credential, the database, a firewall rule, the policy assignment, the API version `2026-07-01` itself, or with an error no row here names | Stop |

`az sql server ad-only-auth disable` is never run, whatever is refused.

**Two switches are not remembered.** The parameter file keeps what the environment does with its
logs, read from Azure. It does not keep `denyPolicy=false` or `logVolumeAlert=false`: a policy or
an alert that is absent cannot be told from one a run that stopped halfway never got to create,
and a guard must not be switched off by that. If either was left out because Azure refused it, or
the alert because step 20 found that it does not count lines, pass the same override on every
later run of the template; without it the run asks Azure again for what it left out. The what-if
shows it first: a policy assignment, or a fourth alert, to create.

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
| The open took more than 10,000 ms | Nothing changes by itself. The number is recorded and the app's connect timeout stays 10 s: the first sign-in after a cold start may then answer one 503 with `Retry-After: 10`, and the smoke test tries four times. A larger value is the owner's decision, through `Database:ConnectTimeoutSeconds`, with ADR-0058's table worked again: that timeout also bounds each COMMIT. In the trial it took 3,810 ms, once. At step 7 on 2026-10-03: 6,390 ms as the app's identity and 3,253 ms as the migrator's, once each, on a cold replica of the probe, whose string waits 30 s. The app's 10 s stays until its own first sign-in is measured, in the second session |
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
| At step 20 the metric's count is below the rows of the hour's middle or above those of the widened hour | The alert on the workspace does not count lines: it is deleted and left out from then on, as step 20 writes. A log search rule in its place is billed, and the owner's decision |

**The app and the deployments (steps 9 and 15 to 19)**

| If | Then |
| --- | --- |
| The what-if or the deployment with `deployApp=true` is refused on a parameter of `azurebank-app-inputs` (a length) | A value the app needs is missing or the wrong length: run `secrets.ps1 -Action New -DeployApp -ImageTag <the full SHA>` again, as the step does, and read its report. The app, the job and the action group wait for that check, so none of them was sent |
| The what-if lists anything as `Unsupported` | Answer "no" and stop: an ID of the template reads something a what-if cannot work out again, as on 2026-10-03 at step 9 |
| The alert on the workspace is refused | `Invoke-Template 'app' @('logVolumeAlert=false')`, the same override on every later run, and say so. It is one of four rules, and no request for it has ever been sent |
| The policy accepts two replicas | Put 1 back at once and stop: the policy does not work |
| A deployment as the identity is refused naming `userAssignedIdentities/assign/action` | **Stop.** No role is created: a right on the two database identities would let the deployment identity attach the schema-changing one to the app that faces the internet. The deployment from the owner's terminal keeps working meanwhile |
| The raw log of a run holds the server's name, the app's address, one of the five IDs or an address | Stop: the mask or the print is a defect |
| The last-resort road cannot sign in | It is written here as unproven, and its first half (run the job again and read its log) stands alone |

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
2. reads the app and the job, prints what runs now (the three image references and the two revision
   names), and stops if their shape has drifted: scale, revision mode, ingress, the two containers
   and no init container, the job's trigger, parallelism and retry limit, and on each the one
   database identity it must carry and no other. A drift in the identity names the field and the
   ending it should have, never what was found;
3. tries to list the app's secrets and goes on only if Azure refuses;
4. moves the tools image on the migrate job, starts the migration once, and waits for that exact
   execution. When it ends, either way, it prints the verdict: the execution's name, status, start,
   end, length, exit code and a one-word reason. A failed migration stops here: the app is not
   touched;
5. moves both app images in one request, waits up to 15 minutes for the new revision to be ready,
   and reads the app's shape again, as in step 2;
6. waits until the revision that ran before is inactive, so that the answers below come from the
   new code;
7. smoke test: `/` is the built page; `/health/ready` answers `Healthy` (not merely 200: the BFF
   answers 200 `Degraded` when the API is down); one sign-in for an address nobody can register,
   sent to `/bff/auth/login`, must be refused with 401 and the code `INVALID_CREDENTIALS`. That
   answer means the BFF reached the API with its key and the API asked the database. It writes
   nothing. Measured on a local stack of this code: that request answers 401 with that code when
   the schema is there, and 500 on a database with no table while `/health/ready` still says
   `Healthy`. The sign-in is the one check that fails when a migration did not run, and with
   identities it is also the one that fails when the app cannot sign in to the database.

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

**If step 5 or step 7 fails, the script tries to put the app back** on the template it had at
step 2 (if that fails too, the run says the app may be serving a broken revision), under a new
revision, and the run still fails, saying "put back to `<tag>`; the schema stays where the
migration left it". The sign-in is tried up to four times and the last try decides. If the last
try is rate limited (429) or gets no answer (the connection refused, reset, closed or timed out),
nothing is proved either way: the run fails as *unproven* and the new revision is left in place.
While the page or `/health/ready` gets no answer, they are asked again every five seconds for five
minutes; still nothing then is a failure, and the app is put back.

No request a deployment sends carries an identity: a change is a location and a template.

Every deployment ends the sessions held in the replica's memory, and so does every scale to zero.

## Reading the logs

From a terminal where the owner has run `az login`, with the two variables of step 16 set:

```powershell
python infra/deploy.py --job-log                  # the latest migration: its verdict, then what it printed
python infra/deploy.py --job-log '<execution>'    # a named one
python infra/deploy.py --app-log 30               # what the app's two containers printed in the last 30 minutes
```

`--app-log` takes 1 to 1440 minutes. Both commands show at most 5,000 lines, and both are refused
inside GitHub Actions. The other way in is the portal: the workspace `azurebank-logs`, Logs.

- **The cap comes first.** Each command starts by saying what the daily cap is doing: its value,
  `dataIngestionStatus`, and the next reset. `OverQuota` means the workspace has taken no line
  since the cap was reached and takes none until the reset, at an hour Azure picks.
- **An empty answer is not proof that nothing was printed.** A line takes minutes to arrive (in
  the trial a median of 387 s and 398 s for two jobs, 486 s at the most), Microsoft's page allows
  a new diagnostic setting up to 90 minutes, and a capped workspace takes none. The trial's three
  runs that lasted seconds each kept their line: three runs, not a promise.
- **Telling two failures of the migration apart by the run's length**, which is on the verdict
  line: a login refused because the database has no user for the identity ends after about four
  seconds; an identity that gets no token is waited for the whole 60 s. Neither has been seen
  from `migrate` on Azure. The trial's own program got the two answers `migrate` would get: with
  the right identity signing in to `master`, where it had no user, error 18456, class 14, after 41
  to 52 ms; with no token, an error numbered 0, class 20, around
  `Azure.Identity.AuthenticationFailedException`, after 40 to 71 ms.
  The token's failure is quick each time: it is `migrate`'s own wait that makes that run long.
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
# 1. The diagnostic setting, and the alert on the workspace if the app exists.
$environment = az containerapp env show --name azurebank-env --resource-group $group --query id --output tsv
az monitor diagnostic-settings delete --name to-azurebank-logs --resource $environment
az monitor metrics alert delete --name azurebank-log-volume --resource-group $group

# 2. The environment sends its logs nowhere. Add -DeployApp if the app exists.
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

None of these commands has been run yet.

## Stop the app by hand

The portal: the app `azurebank`, **Stop**. From a terminal (the core CLI has no
`az containerapp stop`; measured: the command is not recognised):

```powershell
$app = az containerapp show --name azurebank --resource-group azurebank-demo --query id --output tsv
az rest --method post --url "https://management.azure.com${app}/stop?api-version=2025-01-01"
```

`/start` instead of `/stop` starts it again. The database keeps its daily charge while the app is
stopped. The deployment identity cannot stop or start the app.

## When something fails

| What you see | What it means | What to do |
| --- | --- | --- |
| `build-push`: "The registry gave no clear answer" | The registry did not say "no such manifest". For a package that has never been published it may answer `denied`, the same as for a private one | First publication only: run again with `-f first_publication=true`. Otherwise read the answer printed below the error and run again |
| `deploy`: an image "is not published, or its package is not public" | The check before the Azure sign-in | Run `build-push` at this commit; make the package public |
| "is not in the shape this script deploys onto" | Something changed the app or the job outside the template: its scale, its ingress, its containers, or the identity it carries. The words in brackets say when it was read. "(nothing was changed)": nothing was changed by this run. "(after its images moved)": the app had moved, and the run printed what Azure says about the revision and put the app back, as below. "(after its image moved)": the job runs the new tools image, no migration ran and the app was not touched. "(after the put-back)": the old images run again, and the shape is still wrong | Run the template again ([Changing the infrastructure later](#changing-the-infrastructure-later)). If it is the identity, read [If something was stolen](#if-something-was-stolen) first |
| "This identity can list the secrets" | The identity holds more than the custom role. Nothing was changed | Look at its role assignments: there must be exactly two |
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

**Going back by hand.** As the owner, from a terminal, to any commit whose images are published:

```powershell
$env:AZURE_SUBSCRIPTION_ID = az account show --query id --output tsv
$env:AZURE_RESOURCE_GROUP  = 'azurebank-demo'
$env:IMAGE_TAG             = '<the full SHA to go back to>'
python infra/deploy.py --app-only
```

It moves the app only: no job is touched and no migration runs. It keeps the same checks and the
same put-back. It is refused inside GitHub Actions: a workflow that could deploy any published tag
without the migration would be a second, weaker road.

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
$env:ConnectionStrings__DefaultConnection = "Server=tcp:$fqdn,1433;Database=AzureBank;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False"
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

Three things to know about this road:

- **It signs in as the administrator**, with more rights than the migrator, and it is the one
  exception to "run nothing else as administrator in this database". That is why the users script
  runs first: its first check refuses a database that holds a trigger or a module.
- **It is the source at that commit, not the image.** The image has no `az` inside, so
  `Active Directory Default` has nothing to sign in with there, and a managed identity does not
  exist off Azure.
- **It has not been run.** With `Active Directory Default` the driver walks several credentials
  and is expected to take the `az login` session. Step 19 tries it once with nothing to migrate.

## If something was stolen

There are no passwords to change. If code may have run as one of the two database identities (a
deployment nobody ordered, an image nobody built, an identity on a resource it does not belong
to), the steps are these, in this order. They have not been rehearsed.

1. **Delete the app and the job.** With them go the app's secrets and the two places an identity
   can be used from.

   ```powershell
   az containerapp delete --name azurebank --resource-group $group --yes
   az containerapp job delete --name azurebank-migrate --resource-group $group --yes
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
   `secrets.ps1` generates the seven application secrets anew.

   ```powershell
   try {
       ./infra/secrets.ps1 -Action New -DeployApp -ImageTag '<a known commit>'
       Invoke-Template 'after-a-theft'
   } finally {
       ./infra/secrets.ps1 -Action Remove
   }
   ```

5. **Look at who could run a job in `demo`** ([What each identity can do](#what-each-identity-can-do)),
   then deploy.

While an intruder can still run code in the app or in the job, a new token is one request away:
that is why step 1 comes first. The app and the job are deleted, and not put back on known
images, because nothing else in these files makes the seven application secrets anew.

## Where each secret lives

| Secret | Lives in | Who can read it |
| --- | --- | --- |
| JWT secret, idempotency key, step-up key, service key, audit chain key, audit anchor key, PIN pepper | The secrets of the app `azurebank` | The owner, by listing. The deployment identity cannot list them, and can still reach them by running code: see [What each identity can do](#what-each-identity-can-do) |
| The app's connection string | A secret of the app, referenced by the `api` container only | The same. It holds the server's name and a client ID, and no password |
| The migration connection string | The secret of the job `azurebank-migrate` | The same. No password |
| A database password | **Nowhere: none exists.** The server takes Microsoft Entra sign-ins only | Nobody |
| `parameters.json` | `%LOCALAPPDATA%\AzureBank\deploy` (elsewhere `~/.azurebank-deploy`), open to its owner only, for the minutes of a session. Without `-DeployApp` it holds no secret | Removed in `finally` |
| In GitHub | The three Azure identifiers, as secrets of the environment `demo`. No application secret | A job in `demo` |

The two connection strings are secrets although they hold no password. An identity can be used by
every container of the app, so the string is kept out of the BFF, which faces the internet: that
container is handed neither the server's name nor the client ID. This is not a lock, because
neither is a secret ([What each identity can do](#what-each-identity-can-do)). A secret also stays
out of every read of the app and of an execution, which a plain setting would not.

`secrets.ps1 -Action New` never generates a value twice: each of the seven application secrets
comes from the deployed app if it exists, else from a file left by a run that stopped, else from
the system's random generator. A failed read is never taken for an absent value: the script stops
and writes nothing. `-Action Remove` deletes `parameters.json`, `what-if.json` and `budget.json`
by name, then the folder if it is empty, and nothing else.

Rotating an application secret is not in these files, short of
[If something was stolen](#if-something-was-stolen).

## What each identity can do

**The deployment identity, `azurebank-deploy`.** The custom role lets it read and write the app
and the job, start the job, and read executions, revisions and replicas. It cannot list secrets,
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
- run any image in the job, as the schema-changing identity. So, by running code in the job, it
  can do everything the migrator can do, and **it can leave code in the database that runs as
  whoever next changes users there**: the users file refuses to run on such a database.

It is not expected to be able to attach an identity to a resource: Azure asks for a right on the
identity itself (`assign/action`), which this role does not hold and which is never given to it.
That refusal has not been provoked.

The policy refuses: a second replica, a minimum above zero, several active revisions, plain HTTP, a
third container, any init container, a container above half a vCPU; and for a job, a trigger other
than Manual, parallel runs under any trigger, any init container, a container above half a vCPU. It
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
**It is available to both containers of the app.** Code running in the BFF, which faces the
internet, can therefore ask for a database token. Between that code and the database stands only
what the BFF is not handed: the server's name and the identity's client ID. Both are identifiers,
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

**Who can act as either:** code running in any container of the app, in the job, in the probe job
of the first session while it exists, or in any resource the owner attaches the identity to. In
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
3. Each of the three packages to Public (step 14). It cannot be undone.
4. The merge of the pull request that puts the workflow on `main`, and the required checks.
5. The approval of each `deploy` run, if the reviewer is set.
6. Reading the mailbox: the test e-mail of step 15, and every alert afterwards.
7. Stopping the app when an alert says so. Nothing does it for him.
8. The word for each step that writes, and for each deletion that a rule above allows: the
   workspace and its setting.

## Before renaming or transferring the repository

Delete the federated credential first:

```powershell
az identity federated-credential delete --identity-name azurebank-deploy --resource-group azurebank-demo --name github-demo
```

The credential trusts the repository by name. After a rename or a transfer, whoever then owns
`Gurgant/azurebank-v2` could sign in as the deployment identity.

## Changing the infrastructure later

Edit the template, then run it the same way. Without `-ImageTag` the parameter file takes the tag
the app runs now, the seven secrets it holds now, the address its alerts write to now and what its
environment does with its logs now, so the run leaves all four alone:

```powershell
try {
    ./infra/secrets.ps1 -Action New -DeployApp
    Invoke-Template 'change'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

- Images move through the `deploy` workflow only: once the app exists, `secrets.ps1` refuses
  another `-ImageTag`.
- The server is not changed by a later run: step 3 is where that is seen for the template. The
  `administrators` block is never edited in place.
- The environment's mode is read again (`Assert-EnvironmentMode`) at the start of every session,
  with the reads of step 1. Microsoft's FAQ says that an environment whose features Express
  supports may be moved to it after a notice, and that one with no running app or job and no
  recent activity may be archived; this one has jobs, a second container and Azure Monitor logs,
  which Express does not have (read on 2026-10-03).
- If the policy or the alert on the workspace was left out because Azure refused it, or the alert
  because step 20 found that it does not count lines, the override that left it out is passed
  again: `Invoke-Template 'change' @('denyPolicy=false')`, or `@('logVolumeAlert=false')`. The
  file does not remember it ([If Azure says no](#if-azure-says-no)).
- A job with another trigger needs it added to the parameter `allowedJobTriggers`, or the policy
  refuses it.
- After an identity was deleted and made again, run `./infra/sql-principals.ps1`: the user it left
  matches nothing and is replaced.

## Removing everything

In this order. The role definition and the policy definition are not inside the resource group
and are not deleted with it. The role can be assigned in this group only, so it is removed first,
through the group, while the group still exists: its two assignments, then the definition. Whether
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
    Where-Object { $_.policyType -eq 'Custom' -and $_.displayName -eq 'AzureBank: one small replica, manual jobs' } |
    ForEach-Object { az policy definition delete --name $_.name }
az consumption budget delete --budget-name azurebank-monthly
'AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID' | ForEach-Object { gh secret delete $_ --env demo --repo Gurgant/azurebank-v2 }
```

Then, in GitHub's settings: the environment `demo`, the three packages, the workflow runs. The
database's daily charge stops when the database is gone. The three identities and the two database
users go with the group. On this machine, if it is no longer wanted:
`winget uninstall --id Microsoft.Sqlcmd`.

## What is not here

- **Nothing stops the app automatically, and nothing stops the logs.** Four e-mails warn; the
  owner stops the app and says when the logs go off.
- No alert that the log's cap was reached, and no SQL auditing: a sign-in attempt on the server
  leaves no record.
- One warning line per caller per window from the rate limiter, instead of one per request. It is
  the change that would let the app's own limiter bound the log, and it touches the BFF's security
  logging.
- The API in an app of its own, so that only it can ask for a database token.
- No alert on changes to the role assignments, the federated credential or the job.
- No deployment by image digest; no rollback of the schema.
- No rotation of application secrets, no Key Vault, no private endpoint, no custom domain.
- No scheduled job and no demo data: the database a first deployment leaves has a schema and no
  rows. The app's registration endpoint is not closed by anything in this folder, and the app is
  reachable by anyone who has its address.
- The text of the migration is not shown by the workflow, on purpose.
- The password design this folder first had, the other two shapes of the SQL server and the
  switch that bound a user to its identity's object ID. They were written as steps down in case
  Azure refused the first shape or stored the other ID. In the trial it did neither, and none is
  a step of this runbook any more. They are in the history.

## Measured on Azure

Two parts: a trial on 2026-10-02, and, at the end of this section, the first deployment, begun on
2026-10-03 with this folder's own files.

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
  was an earlier one than this folder's: it had every condition of `guardrails.bicep` but four
  (an init container on an app, an init container on a job, parallel runs under a schedule
  trigger and under an event trigger). Those four were first sent in the definition step 2 of the
  first deployment created on 2026-10-03; none of them has been seen refusing.
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
  decision stands until the app's own first sign-in after a cold start is measured, in the second
  session.
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
  ([What this creates](#what-this-creates)), and offline the changed template names all 23
  resources of the run with the app. On Azure, see step 9 again, below.
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
  an unexpected row. (Measured on 2026-10-02 and not repeated. Since then the two code lists have
  changed, and they now leave one kind of module out, so they can only name less; and a 17th
  migration, `AddDemoCopies`, was added. Read, not run: it adds a table with its primary key, a
  column, five indexes, a foreign key, a default and two CHECK constraints, kinds the first 16
  already create, and no `Sql()` statement.)
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
logs off or elsewhere, and on no answer. On 2026-10-03 the tests also ran on Linux, in WSL
(Ubuntu 24.04, Python 3.12, PowerShell 7.6.6 and Bicep 0.47.16), from an archive of the branch:
all passed, among them the Linux half of two (the folder's and the file's modes, and the
signature check that says it checked nothing), and the two that hold only on Windows were
skipped. actionlint 1.7.12 with ShellCheck 0.11.0 read the run blocks of the three workflows and
found nothing; an unquoted variable planted in a copy is reported.

**Not measured.** The trial sent requests by hand, and the first deployment has run its first
session, steps 1 to 10; read-only commands, offline tests and local stacks cannot show the rest.
Each line is checked at the step named, in the rest of the first deployment.

| What | Where it shows |
| --- | --- |
| **This folder's own files on Azure, past what the first session met** ([Measured on Azure](#measured-on-azure)): `main.bicep`'s second step deployed (nine more resources); `deploy.py`; the workflow `deploy.yml` | steps 15 to 17 |
| Azure refusing a run with a value missing, through `app-inputs.bicep` (seen offline and on a local engine only, under [Checking these files](#checking-these-files)), and whether it refuses before the foundation's resources are sent again or only when the check's own deployment starts (the app, the job and the action group wait for it either way). The what-if run again at step 9 was given every value | not provoked |
| What the template sends and neither the trial nor steps 2 to 4 showed: the four conditions of the policy named above at work (the definition is deployed and assigned), the action group and the four alert rules | step 15 |
| How the app's scale reads back (a value Azure leaves out is read by `deploy.py` as its default). The logs settings read back at steps 3 and 4 | step 15 |
| That a second run of the users file on Azure SQL changes nothing. The second run of step 6 ended with the same lines as the first, and the file prints those lines whether or not it replaced a user | no step reads it |
| The users file dropping and creating a user inside its transaction, which it does when an identity has been made again; its second form, `FROM EXTERNAL PROVIDER WITH OBJECT_ID` | not provoked; the second form only if it is asked for |
| A container of the app that asks for a token and names no identity gets none (read on Microsoft's page: such a request is answered for a system-assigned identity, and the app has none) | not provoked |
| The API as `azurebank_app` under the three real-stack test suites, and a transfer as that user | before step 15, on a local SQL Server |
| **The app itself:** two containers in one replica, its three probes against a cold start (1 s delay, 3 s period, 10 failures; 4 s timeout on readiness), the first database request after it, and scale to zero. Its own first sign-in after a cold start, against its 10 s: the probe program's first open as the app's identity took 6,390 ms at step 7, one cold run with a string that waits 30 s | steps 15 and 16, and the days after |
| The four alert rules are accepted with these metric names (`Requests`, `TxBytes`, `Replicas`, `Ingestion Volume`), the fourth on a workspace and at no cost; a test e-mail arrives; any of the four ever firing | step 15, and the days after |
| That the fourth alert counts lines. Microsoft's page calls `Ingestion Volume` the number of records ingested and lists `Count` as its default aggregation; whether one measurement of it is one record is said nowhere. If it is not, `Count` counts measurements, the rule may never reach 50,000 an hour, and it is switched off | step 20 |
| What the `Replicas` metric reports while the app is scaled to zero: 0, or nothing. If nothing, a day's average is 1 on any day the app ran at all, the alert on replica time fires on any use, and that rule has to count another way | the first days after step 16 |
| Our own template passes the Deny policy; the policy refuses a second replica on a PATCH (the trial saw an earlier rule refuse a job above half a vCPU) | step 15 |
| What the registry answers the workflow's token for a package that does not exist yet (anonymously, measured: `denied`); the digest line | step 14 |
| The smoke test's answers through the Azure ingress, and whether every visitor shares one sign-in limit behind it | step 16 |
| The answers `deploy.py` reads, as it reads them: the job start it sends, the execution states while it polls, the revision's `active` flag, a replica's container states. Where an execution's status, times and exit code sit was seen at step 7, through `Show-Executions`, which reads the same fields on the same API version | step 16 |
| The columns `--app-log` reads (`ContainerAppName`, `ContainerName`); `--job-log` reads `JobName` and `Log`, which the trial saw filled | steps 16 and 17 |
| **As the deployment identity:** GET and PATCH of the app and of the job succeed with these nine actions against resources that carry an identity, with no right on the environment and none on the attached identity; the listing of secrets is refused with `AuthorizationFailed` | step 17 |
| On Azure, as on the local stack: the app becomes ready on an empty database, before the first migration | steps 15 and 16 |
| `migrate` itself, as the migrator's identity (the trial ran the kinds of statement, not the tool) | step 16 |
| The automatic put-back on a real failure. Its trigger is proved by unit tests only; its request and its wait are the ones `--app-only` uses | step 18 proves `--app-only` |
| What the app reads just after the put-back request. If its state still says `Failed`, left by the deployment that failed, `deploy.py` reports a put-back that did not succeed although it may have | a real put-back; not provoked |
| A request to Azure that fails once in the middle of a run. Nothing is asked twice: the run stops, and nothing is put back | not provoked |
| The raw log of a workflow run holds none of the three identifiers, not the app's address, not the server's name, no client ID of a database identity and no address: step 17 counts each | step 17 |
| `migrate` from a checkout, signed in as the owner with `Active Directory Default` | step 19 |
| The meters after 48 hours: the three environment meters and the Dedicated one at 0; whether the free 5 GB of logs apply to this offer; whether the cost view returns a row at all (on the trial's own day it returned none) | after steps 2 and 21 |
| What the workspace bills for a line of the app; how far the cap overshoots; whether an environment set to `none` still feeds a setting that exists | after step 21; the last two are not provoked |
| How long a managed identity's token stays valid for the database | not found in the pages read |
| That the identity is refused a scale change, a delete or a stop. One refusal is provoked on every deployment (the secrets listing); the policy's refusal is provoked as the owner | not provoked |
| Every command under [Switching the logs off](#switching-the-logs-off), [If something was stolen](#if-something-was-stolen) and [Removing everything](#removing-everything). The trial made its own deletions with other commands | the day they are needed |
| The CI job `infra` itself, on GitHub's runner and its versions of the tools, among them whether its Bicep has `snapshot`, which the tests run (Microsoft's page names version 0.41.2 or later). Its checks ran on this machine, on Windows and in WSL (above); the branch has not been pushed | the pull request's first run |

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
shapes `secrets.ps1` writes, it names all 23 resources of the run with the app, and 14 without it.
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

`test_deploy.py` tests the deployment script's decisions against invented answers: time is a
counter and no process is started. A few of its tests open a real connection to a server of their
own on `127.0.0.1`, to see what a dropped connection really raises. `test_scripts.py` runs the two
PowerShell scripts for real against a stand-in for the Azure CLI and a stand-in for `sqlcmd`,
reads `sql-principals.sql` as text (the order of its guards and every condition, word for word;
what a server does with them is above), and reads the compiled templates: the role's nine
actions, the federated credential's subject, every rule of the policy, the two identities and the
one each resource carries, that no database credential is anywhere, the workspace and its cap, the
environment's mode and its API version, the app's name and no resource ID that reads a secret,
and the check of the app's values and what waits for it. It also runs `bicep snapshot` on copies
of the templates: every ID worked out with the app, and the short or long tag and the empty
address refused. The CI job `infra` runs the same three checks and actionlint on the workflows.
