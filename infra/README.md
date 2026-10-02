# AzureBank on Azure Container Apps

How the demo is created, deployed, stopped and removed. Everything here is run by hand: the
templates from a terminal, the deployment from one workflow. Commands are PowerShell 7.

**State of this document.** The templates compile and the scripts are tested offline against
stand-ins. The facts marked *measured* were read on 2026-10-02, from Azure or GitHub with read-only
commands or on a local stack of this code. Nothing in this folder has been deployed yet. What only
a deployment can show is listed under [Not measured yet](#not-measured-yet), and every expected
value below is marked as expected.

- [What this creates](#what-this-creates)
- [What it costs, and what bounds it](#what-it-costs-and-what-bounds-it)
- [What you need](#what-you-need)
- [Create it once](#create-it-once)
- [Deploy a commit](#deploy-a-commit)
- [Stop the app by hand](#stop-the-app-by-hand)
- [When something fails](#when-something-fails)
- [Where each secret lives](#where-each-secret-lives)
- [What the deployment identity can do](#what-the-deployment-identity-can-do)
- [Who can reach the database server](#who-can-reach-the-database-server)
- [Before renaming or transferring the repository](#before-renaming-or-transferring-the-repository)
- [Changing the infrastructure later](#changing-the-infrastructure-later)
- [Removing everything](#removing-everything)
- [What is not here](#what-is-not-here)
- [Not measured yet](#not-measured-yet)
- [Checking these files](#checking-these-files)

## What this creates

One resource group, `azurebank-demo`, in Italy North. `main.bicep` is run twice: once with
`deployApp=false` (the foundation, which needs no image) and once with `deployApp=true`.

**The foundation**

| Resource | What it is |
| --- | --- |
| `azurebank-env` | A Container Apps environment with the Consumption profile only. Logs go nowhere (`destination: 'none'`): there is no Log Analytics workspace |
| `azurebank-<letters>` | A SQL logical server, TLS 1.2. Its SQL administrator password is random, new at every run of the template and kept nowhere: nothing signs in with it |
| the server's Microsoft Entra administrator | The owner's account, as its own resource so that a later run can set it again |
| `AzureBank` | The database: Basic, 5 DTU, 2 GB, locally redundant backups |
| `keep-the-database` | A lock (cannot delete) on the database. On the database and not on the server, so that the temporary firewall rule below can still be removed |
| `AllowAzureServices` | The firewall rule `0.0.0.0`: see [Who can reach the database server](#who-can-reach-the-database-server) |
| `azurebank-deploy` | A managed identity with one federated credential: GitHub may sign in as it only from the environment `demo` of `Gurgant/azurebank-v2`. Until the second run it holds no role on anything |
| `AzureBank deploy <letters>` | A custom role of nine actions: read and write the app and a job, start a job, read its executions, read the app's revisions and replicas. It cannot list secrets, delete or stop |
| `azurebank-shape` | A policy assignment on the resource group, effect Deny (definition in `guardrails.bicep`, written at subscription level because a custom definition cannot live in a resource group). It refuses, whoever asks: more than one replica, a minimum above zero, several active revisions, plain HTTP, more than two containers, a container above half a vCPU; and for a job, a trigger other than Manual, parallel runs, a container above half a vCPU |

**With `deployApp=true`**

| Resource | What it is |
| --- | --- |
| `azurebank` | The app: the BFF (0.25 vCPU, 0.5 GiB) and the API (0.5 vCPU, 1 GiB) in one replica, zero to one replica, single revision. HTTPS ingress to the BFF's port 8080. The API listens on `127.0.0.1:5068` only: nothing outside the replica can reach it. Three probes, on the BFF. Eight secrets, each reaching a container by reference |
| `azurebank-migrate` | A manual job: the tools image with the argument `migrate`, no retry, 600 s, its own secret (the schema-changing login) |
| two role assignments | The custom role, to the deployment identity, on the app and on the job and nowhere else. From here the workflow can change the app |
| `azurebank-owner` | An action group with one e-mail receiver |
| three alert rules | E-mail only, on the app: more than 66,667 requests in an hour; more than 3.3 GiB sent in a day; the replica running more than about 2.2 hours in a day (an average replica count above 0.093) |

The environment variables of the two containers are the ones `compose.yaml` sets. The connection
limits are the hosts' own defaults (ADR-0058); the template sets none.

Three container images, public in GHCR, tagged with the full commit SHA: `azurebank-api`,
`azurebank-bff`, `azurebank-tools`.

## What it costs, and what bounds it

Prices are from the Azure Retail Prices API for Italy North, in USD, read on 2026-10-02. The
free amounts of Container Apps are the ones its pricing page documents; they are not in that API.

| Meter | Price | Free each month |
| --- | --- | --- |
| SQL Database Basic | $0.161 a day | none |
| Container Apps, vCPU while active | $0.000034 a vCPU-second | 180,000 vCPU-seconds |
| Container Apps, memory while active | $0.000004 a GiB-second | 360,000 GiB-seconds |
| Container Apps, requests | $0.40 a million | 2 million |
| Data out to the internet | $0.087 a GB | the first 100 GB |
| Environment management, private endpoint, planned maintenance; Dedicated plan | $0.13 an hour each; $0.10 an hour | none: this template uses none of them, and they must read 0 |

**Expected each month:** the database, $4.83 to $4.99. The app costs nothing while it stays inside
the free amounts: 0.75 vCPU and 1.5 GiB use both up together after 66.7 hours of a running replica.
Past that, a replica-hour is $0.1134.

**What bounds the spending, meter by meter.** The demo's subscription is a credit offer with a
spending limit: no bill is possible, and when the credit (86 at the time of writing) is used up the
whole subscription is disabled, the database included.

| Meter | What bounds it | The whole credit is gone after |
| --- | --- | --- |
| vCPU and memory | one replica, by the template and by the Deny policy | 36 days of a replica that is never idle |
| Requests | **nothing** | 217 million requests: 25 days at 100 a second, 2.5 days at 1,000 |
| Data out | **nothing** | 1,089 GB: 30 hours at 10 MB a second |

Requests are counted at the ingress whatever the app answers. The BFF's own rate limiter does not
bound them: the page's files are served before it, the two health paths are excluded from it, and a
refused request is still a request. Any request at least every five minutes keeps the replica up.

**What warns:** the three alert rules, by e-mail. **What stops the app:** the owner, by hand
([Stop the app by hand](#stop-the-app-by-hand)). Nothing stops it automatically.

## What you need

- The Azure CLI and the Bicep CLI (measured here: 2.90.0 and 0.47.16), PowerShell 7, Python 3.12 or
  later, Docker, the GitHub CLI.
- Owner of the subscription, and the account that will be the database's Microsoft Entra
  administrator: `secrets.ps1` takes both from `az login`.
- Admin of the repository, for the environment `demo`, its secrets and the three packages.
- The resource providers `Microsoft.App`, `Microsoft.Sql`, `microsoft.insights`,
  `Microsoft.ManagedIdentity`, `Microsoft.Authorization` registered (measured: all five are).

Two people act below. **The owner** signs in, clicks in GitHub's settings, approves a deployment
and reads the mailbox. **The operator** types the commands in a terminal where the owner has run
`az login` and `gh auth login`; it can be the owner.

Rules for every command in this file:

- No secret is typed, printed, or put on a command line. The one file that holds them is written by
  `secrets.ps1` outside the repository and removed in a `finally`.
- Never `--debug`, and no deployment debug setting that stores request content.
- No `( ) & |` inside an argument to `az`: on Windows `az` is a `.cmd` file and `cmd.exe` reads
  them. Filter JSON in PowerShell instead of with `--query`.

## Create it once

Every step that writes is marked **writes**. Run from the repository root.

```powershell
$group  = 'azurebank-demo'
$folder = Join-Path $env:LOCALAPPDATA 'AzureBank\deploy'   # where secrets.ps1 writes

# The type of each resource a what-if would change, never its name or the subscription.
function Show-WhatIf([string]$File) {
    (Get-Content -LiteralPath $File -Raw | ConvertFrom-Json).changes | ForEach-Object {
        $parts = ($_.resourceId -split '/providers/')[-1] -split '/'
        $type = @($parts[0]) + @(for ($i = 1; $i -lt $parts.Count; $i += 2) { $parts[$i] })
        '{0,-12} {1}' -f $_.changeType, ($type -join '/')
    }
}

# One run of the template: what-if into the protected folder, its change types, then the deployment.
# The answer of the deployment is one word; without --query az prints the parameters back.
function Invoke-Template([string]$Name) {
    az deployment group what-if --resource-group $group --template-file infra/main.bicep `
        --parameters "@$folder\parameters.json" --no-pretty-print --only-show-errors > "$folder\what-if.json"
    if ($LASTEXITCODE -ne 0) { throw 'The what-if failed.' }
    Show-WhatIf "$folder\what-if.json"
    if ((Read-Host 'Deploy this? (yes/no)') -ne 'yes') { return }
    az deployment group create --name $Name --resource-group $group --template-file infra/main.bicep `
        --parameters "@$folder\parameters.json" --query properties.provisioningState --output tsv
}
```

### 1. Look before writing (operator)

```powershell
az account show --query name --output tsv
az group exists --name $group
az sql server list --resource-group $group --query '[].name' --output tsv |
    ForEach-Object { az sql server firewall-rule list --resource-group $group --server $_ --query '[].name' --output tsv }
```

Start every later session the same way. The firewall rules must be `AllowAzureServices` and nothing
else: a rule named `owner-while-creating-users` is the leftover of a run that died, and the users
script deletes it first.

### 2. The resource group and the foundation (operator, **writes**; the database's daily charge starts here)

```powershell
az group create --name $group --location italynorth --query properties.provisioningState --output tsv
try {
    ./infra/secrets.ps1 -Action New          # four parameters; one secret, the SQL administrator password
    Invoke-Template 'foundation'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
Test-Path $folder                            # False
```

Expected in the what-if: eleven resources to create and nothing to change or delete. If the
deployment is refused on the **policy definition**, run it again with the policy off
(add `denyPolicy=false` after the parameter file: `--parameters "@$folder\parameters.json" denyPolicy=false`)
and write down that the shape then rests on `deploy.py`'s own check alone. If it is refused on the
**custom role** or on the **SQL server**, stop: there is no fallback that keeps the deployment
identity away from the secrets.

Read back what was created. These are the expected values, not observed ones:

| Claim | Read | Expected |
| --- | --- | --- |
| Consumption only | `az containerapp env show -n azurebank-env -g $group --query properties.workloadProfiles` | one entry, `Consumption` |
| Logs go nowhere | `az containerapp env show -n azurebank-env -g $group --query properties.appLogsConfiguration`; `az resource list -g $group --query '[].type'` | no destination, or `none`; no `Microsoft.OperationalInsights` type |
| The database | `az sql db show -g $group -s <server> -n AzureBank` | `Basic`, capacity 5, 2147483648 bytes, `Local` |
| The server | `az sql server firewall-rule list`; `az sql server ad-admin list`; `az sql server show --query minimalTlsVersion` | one rule; one administrator; `1.2` |
| One federated credential | `az identity federated-credential list --identity-name azurebank-deploy -g $group` | one: the GitHub issuer, the subject ending `:environment:demo`, the audience `api://AzureADTokenExchange` |
| The role, unassigned | `az role definition list --custom-role-only true`; `az role assignment list --assignee <principal id> --all` | nine actions, no data action; no assignment yet |
| The lock | `az lock list -g $group` | one, `CanNotDelete`, on the database |
| The policy | `az policy assignment list -g $group` | `azurebank-shape`, enforcement `Default` |

### 3. A budget, if the offer allows one (operator, **writes**; optional)

`az consumption budget create` cannot set a notification (its help lists no such argument), so a
budget that warns is one REST call with a body file. The body holds the address the e-mails go to,
taken from the variable `AZUREBANK_ALERT_EMAIL`; the file is written into the session's folder and
removed with it. Whether a credit offer accepts a budget at all is not measured, and neither is
this body: a refusal here changes nothing else.

```powershell
if (-not $env:AZUREBANK_ALERT_EMAIL) { throw 'Set AZUREBANK_ALERT_EMAIL first.' }
try {
    $null = New-Item -ItemType Directory -Path $folder -Force
    $notify = { param($percent, $kind) @{ enabled = $true; operator = 'GreaterThanOrEqualTo'; threshold = $percent
                                           thresholdType = $kind; contactEmails = @($env:AZUREBANK_ALERT_EMAIL) } }
    @{ properties = @{ category = 'Cost'; amount = 20; timeGrain = 'Monthly'
        timePeriod = @{ startDate = (Get-Date -Day 1).ToString("yyyy-MM-'01T00:00:00Z'") }
        notifications = @{ 'actual-30' = & $notify 30 'Actual'; 'actual-50' = & $notify 50 'Actual'
                           'actual-100' = & $notify 100 'Actual'; 'forecast-30' = & $notify 30 'Forecasted' } } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$folder\budget.json"
    $scope = az account show --query id --output tsv
    az rest --method put --body "@$folder\budget.json" --output none `
        --url "https://management.azure.com/subscriptions/$scope/providers/Microsoft.Consumption/budgets/azurebank-monthly?api-version=2023-05-01"
} finally {
    ./infra/secrets.ps1 -Action Remove
}
```

### 4. The GitHub environment (owner in the browser, **writes**; then the operator reads it back)

Settings, Environments, New environment, **`demo`** (lower case: Azure matches the name exactly).
Deployment branches and tags: Selected, `main`. Required reviewers: the owner, so that every
deployment waits for a click.

```powershell
gh api repos/Gurgant/azurebank-v2/environments/demo --jq '.name, .protection_rules[].type'
gh api repos/Gurgant/azurebank-v2/environments/demo/deployment-branch-policies --jq '.branch_policies[].name'
```

Expected: `demo`, a `required_reviewers` rule, and exactly `main`.

**This step comes before step 7 and before the workflow file is on `main`.** A workflow that names
an environment that does not exist creates it with no rule at all, and step 7 is what gives the
identity its two role assignments. Read the environment back again just before step 7.

### 5. The three Azure identifiers, as secrets of `demo` (operator, **writes**)

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

The workflow's first step fails, without printing anything, if one of the three is missing or is
not the shape of an identifier (a stray space or newline).

### 6. The images (operator, **writes**; then the owner, in the browser, **cannot be undone**)

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

### 7. The database users and the app (operator, **writes**)

```powershell
try {
    ./infra/secrets.ps1 -Action New -DeployApp -ImageTag $sha   # nine "generated" on the first run
    ./infra/sql-principals.ps1                                  # azurebank_app and azurebank_migrator
    Invoke-Template 'app'
} finally {
    ./infra/secrets.ps1 -Action Remove
}
Test-Path $folder                                               # False
```

`secrets.ps1` takes the address the alerts write to from `AZUREBANK_ALERT_EMAIL` if that variable
is set, else from the signed-in account's own mailbox. Its report names which, never the address.

`sql-principals.ps1` signs in as the Microsoft Entra administrator with a token from `az`, allows
this machine's address for the length of the run (a firewall rule named `owner-while-creating-users`,
with the address the server itself reports), runs `sql-principals.sql` with the two passwords as
bound parameters, removes the rule and reads the rule list back. It can be run any number of
times: the users are created if absent and altered if present.

Expected in the what-if: eight resources to create (the app, the job, two role assignments, the
action group, three alerts); the SQL server may show as modified, because its administrator
password is new at every run.

Then read back (expected values), and make two things happen on purpose:

| Claim | Read | Expected |
| --- | --- | --- |
| Zero to one replica, one revision | `az containerapp show -n azurebank -g $group --query properties.template.scale`; `--query properties.configuration.activeRevisionsMode` | 0, 1; `Single` |
| The API is not exposed | `--query properties.configuration.ingress` | external, target port 8080, `allowInsecure` false, no additional port mapping |
| Secrets by name only | `az containerapp secret list -n azurebank -g $group --query '[].name'`; `az containerapp job secret list -n azurebank-migrate -g $group --query '[].name'` | eight names; one name |
| The job | `az containerapp job show -n azurebank-migrate -g $group --query properties.configuration` | `Manual`, retry limit 0, timeout 600, parallelism 1 |
| Roles on the app and the job only | `az role assignment list --assignee <principal id> --all` | exactly two rows, the custom role, scopes ending `/containerApps/azurebank` and `/jobs/azurebank-migrate` |
| The alerts | `az monitor metrics alert list -g $group`; `az monitor action-group show -n azurebank-owner -g $group` | three rules, enabled, on the app; one e-mail receiver |

1. **The policy must refuse.** As the owner, ask for two replicas. The request must fail with
   `RequestDisallowedByPolicy`. If it is accepted, put 1 back at once: the policy does not work.

   ```powershell
   $app = az containerapp show --name azurebank --resource-group $group --query id --output tsv
   '{"properties":{"template":{"scale":{"minReplicas":0,"maxReplicas":2}}}}' | Set-Content "$env:TEMP\scale.json"
   az rest --method patch --url "https://management.azure.com${app}?api-version=2025-01-01" --body "@$env:TEMP\scale.json"
   Remove-Item "$env:TEMP\scale.json"
   ```

2. **An alert e-mail must arrive.** The owner opens the action group `azurebank-owner` in the
   portal, presses Test, and reads the mailbox.

### 8. The first deployment (operator, **writes**; the owner watches and approves)

First as the owner, from this machine: the first migration creates every table, and its log can be
watched only while it runs (the portal, the job `azurebank-migrate`, its execution, Log stream).

```powershell
$env:AZURE_SUBSCRIPTION_ID = az account show --query id --output tsv
$env:AZURE_RESOURCE_GROUP  = $group
$env:IMAGE_TAG             = $sha
python infra/deploy.py
```

Then the same road as the deployment identity, twice (the second run proves that a migration with
nothing to do and a new revision of the same images both work):

```powershell
gh workflow run deploy.yml --ref main -f action=deploy
```

In the log of the workflow run, look for "the listing was refused", the line of the smoke test,
and no Azure identifier and no address of the app.

Then the road back, once, so that it is not first tried on a bad day:

```powershell
python infra/deploy.py --app-only
az logout
```

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
   names), and stops if their shape has drifted: scale, revision mode, ingress, the two containers,
   the job's trigger, parallelism and retry limit;
3. tries to list the app's secrets and goes on only if Azure refuses;
4. moves the tools image on the migrate job, starts the migration once, and waits for that exact
   execution. A failed migration stops here: the app is not touched;
5. moves both app images in one request and waits up to 15 minutes for the new revision to be ready;
6. waits until the revision that ran before is inactive, so that the answers below come from the
   new code;
7. smoke test: `/` is the built page; `/health/ready` answers `Healthy` (not merely 200: the BFF
   answers 200 `Degraded` when the API is down); one sign-in for an address nobody can register,
   sent to `/bff/auth/login`, must be refused with 401 and the code `INVALID_CREDENTIALS`. That
   answer means the BFF reached the API with its key and the API asked the database. It writes
   nothing. Measured on a local stack of this code: that request answers 401 with that code when
   the schema is there, and 500 on a database with no table while `/health/ready` still says
   `Healthy`. The sign-in is the one check that fails when a migration did not run.

**If step 5 or step 7 fails, the app is put back** on the template it had at step 2, under a new
revision, and the run still fails, saying "put back to `<tag>`; the schema stays where the
migration left it". A sign-in that is only rate limited (429) or unanswered proves nothing either
way: the run fails as *unproven* and the new revision is left in place.

Every deployment ends the sessions held in the replica's memory, and so does every scale to zero.

## Stop the app by hand

The portal: the app `azurebank`, **Stop**. From a terminal (the core CLI has no
`az containerapp stop`; measured in `az containerapp -h`):

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
| "is not in the shape this script deploys onto" | Something changed the app or the job outside the template. Nothing was changed by this run | Run the template again ([Changing the infrastructure later](#changing-the-infrastructure-later)) |
| "This identity can list the secrets" | The identity holds more than the custom role. Nothing was changed | Look at its role assignments: there must be exactly two |
| "Execution ... is Running: a migration may still be running" | An execution blocks every later deployment until it ends, and the deployment identity cannot stop it | The owner: `az containerapp job stop --name azurebank-migrate --resource-group azurebank-demo --job-execution-name <name>` |
| "The migration did not succeed" | The job runs the new tools image; some migrations may be applied; the app still runs the old images | Read the execution's log stream in the portal while a run is alive: start the deployment again and watch. `migrate` is safe to run again |
| "the new revision never became ready", then "put back to ..." | The run printed the revision's state and each container's state and restart count, then put the app back | Read those lines; fix; deploy again |
| "Smoke test failed", then "put back to ..." | The page, the readiness answer or the sign-in answer was wrong on the new revision | The same |
| "Smoke test unproven" | Only 429 or no answer in four tries, 65 s apart after a 429. Sign-ins are limited to 10 a minute, and behind the ingress every visitor may share that limit. The new revision is serving | Deploy again later, or check a sign-in by hand |
| "was still active after 180 s" | The old revision did not go inactive, so the smoke test was not run and nothing was put back | Look at the app's revisions in the portal; deploy again |
| "The put-back ... did not succeed. The app may be serving a broken revision" | Both the deployment and the way back failed | Go back by hand, below |

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

**A migration failure nobody can read.** With logs set to none, a finished execution may leave no
log. The last resort is the owner running the same tools image on this machine, where its output
is on the screen: the image `ghcr.io/gurgant/azurebank-tools:<sha>` with the argument `migrate`
and the one variable `ConnectionStrings__DefaultConnection` in an environment file inside the
protected folder, while this machine's address is allowed on the server.

## Where each secret lives

| Secret | Lives in | Who can read it |
| --- | --- | --- |
| JWT secret, idempotency key, step-up key, service key, audit chain key, audit anchor key, PIN pepper, the app's connection string | The secrets of the app `azurebank` | The owner, by listing. The deployment identity cannot list them, and can still reach them by running code: see the next section |
| The migration connection string | The secret of the job `azurebank-migrate` | The same |
| The two SQL users' passwords | Nowhere else: the database holds their hashes | Nobody |
| The SQL administrator password | Nowhere. New at every run of the template, used by nothing | Nobody; the Microsoft Entra administrator can reset it |
| `parameters.json` | `%LOCALAPPDATA%\AzureBank\deploy` (elsewhere `~/.azurebank-deploy`), open to its owner only, for the minutes of a session | Removed in `finally` |
| In GitHub | The three Azure identifiers, as secrets of the environment `demo`. No application secret | A job in `demo` |

`secrets.ps1 -Action New` never generates a value twice: each of the nine application secrets
comes from the deployed app or job if they exist, else from a file left by a run that stopped,
else from the system's random generator. A failed read is never taken for an absent value: the
script stops and writes nothing. `-Action Remove` deletes `parameters.json`, `what-if.json` and
`budget.json` by name, then the folder if it is empty, and nothing else.

Rotating a secret is not in these files.

## What the deployment identity can do

The custom role lets it read and write the app and the job, start the job, and read executions,
revisions and replicas. It cannot list secrets, delete or stop anything, or touch the environment,
the database, the identity or any role. The Deny policy on the resource group is the one guard it
cannot change.

Writing the app is more than moving an image. Within what the policy allows, whoever acts as this
identity can:

- run any public image, with any command, with every secret of the app in its environment, so the
  secrets can leave in one request;
- overwrite a secret with a value of their choice;
- change the ingress: another target port, more port mappings, address rules;
- run any image in the job, with the schema-changing login in its environment.

The policy refuses: a second replica, a minimum above zero, several active revisions, plain HTTP, a
third container, a container above half a vCPU, a scheduled or parallel job.

So the boundary is **who can run a job in the environment `demo`**: its branch rule (`main` only),
its required reviewer, and whoever holds a GitHub token that can edit the environment or merge to
`main`. Any workflow file in this repository that names `environment: demo` gets the same identity;
the subject of the federated credential names the environment, not the workflow.

`deploy.py`'s shape check catches drift (a click in the portal, a half-applied change). It does not
stop someone who can edit the script; the policy does.

Images are deployed by tag. `build-push` never overwrites a tag, but someone with push rights to
the packages can; deploying by digest would close that and is not done here.

## Who can reach the database server

The firewall rule `AllowAzureServices` admits every address Azure owns: any customer's virtual
machine, function or container app in any tenant, and GitHub's hosted runners. What holds the line
is the passwords (two random 48-character passwords for the two users, a third for the SQL
administrator that is thrown away at every run) and the Microsoft Entra administrator's own
sign-in. Failed sign-ins are recorded nowhere: there is no auditing and no log destination, so
guessing would be invisible. The three login names are in this repository.

The temporary rule `owner-while-creating-users` exists only while `sql-principals.ps1` runs. If the
terminal is closed or the machine sleeps, it stays until the next run of the script, which deletes
it first, or until someone reads the rule list: every session starts by reading it (step 1).

## Before renaming or transferring the repository

Delete the federated credential first:

```powershell
az identity federated-credential delete --identity-name azurebank-deploy --resource-group azurebank-demo --name github-demo
```

The credential trusts the repository by name. After a rename or a transfer, whoever then owns
`Gurgant/azurebank-v2` could sign in as the deployment identity.

## Changing the infrastructure later

Edit the template, then run it the same way. Without `-ImageTag` the parameter file takes the tag
the app runs now and the nine secrets it holds now, so the run leaves the images and the secrets
alone:

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
- Every run sets a new SQL administrator password. That is intended.
- A job with another trigger needs it added to the parameter `allowedJobTriggers`, or the policy
  refuses it.

## Removing everything

In this order. The role definition and the policy definition outlive the resource group.

```powershell
az lock list --resource-group azurebank-demo --query '[].id' --output tsv | ForEach-Object { az lock delete --ids $_ }
az group delete --name azurebank-demo
az role definition list --custom-role-only true --output json | ConvertFrom-Json |
    Where-Object roleName -like 'AzureBank deploy *' | ForEach-Object { az role definition delete --name $_.name }
az policy definition list --output json | ConvertFrom-Json |
    Where-Object { $_.policyType -eq 'Custom' -and $_.displayName -eq 'AzureBank: one small replica, manual jobs' } |
    ForEach-Object { az policy definition delete --name $_.name }
az consumption budget delete --budget-name azurebank-monthly
'AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID' | ForEach-Object { gh secret delete $_ --env demo --repo Gurgant/azurebank-v2 }
```

Then, in GitHub's settings: the environment `demo`, the three packages, the workflow runs. The
database's daily charge stops when the database is gone.

## What is not here

- **Nothing stops the app automatically.** Three e-mails warn; the owner stops it.
- No alert on changes to the role assignments, the federated credential or the job.
- No deployment by image digest; no rollback of the schema.
- No log workspace: the only logs are the portal's live streams.
- No rotation of secrets, no Key Vault, no private endpoint, no custom domain.
- No scheduled job and no demo data: the database a first deployment leaves has a schema and no
  rows. The app's registration endpoint is not closed by anything in this folder, and the app is
  reachable by anyone who has its address.
- The log of the migration is not shown by the workflow.

## Not measured yet

**Measured on a local stack of this code** (`compose.yaml`, Production images, SQL Server 2022,
on 2026-10-02), not on Azure:

- The smoke test's three answers, and the real `deploy.py` smoke test passing against them. A
  sign-in for an unknown address: 401 `application/json` with `"errorCode":"INVALID_CREDENTIALS"`.
  The proxied `/api/auth/login`: 404, empty. Once the shared sign-in limit is spent: 429 with
  `Retry-After: 60` and `"errorCode":"RATE_LIMIT_EXCEEDED"`.
- Before any migration the app starts and `/health/ready` says `Healthy`; the sign-in answers 500
  on a database with no table and 503 with no database.
- `sql-principals.sql`, with its passwords bound as the runner binds them: the first run creates
  the two users, a second run changes nothing, a run with new passwords alters them (the old
  password is then refused, the new one accepted); each run reports the same two role lists; a
  password with a quote or of 20 characters is refused (50001), and so is another database (50000).
- The API as `azurebank_app` (`db_datareader` and `db_datawriter` only): it starts, the smoke test
  passes, a seeded user signs in, reads the accounts, and the statement that takes `sp_getapplock`
  succeeds. The three real-stack test suites and a transfer were not run as that user.
- The nine secrets in the shapes `secrets.ps1` generates are accepted by the API at start.

**Not measured.** Read-only commands, offline tests and a local stack cannot show any of this.
Each line is checked at the step named, on the first deployment.

| What | Where it shows |
| --- | --- |
| The subscription's offer accepts a SQL server, a custom role, a custom policy definition, the lock, and the administrator as its own resource | step 2 |
| How "logs: none" and the app's scale read back (a value Azure leaves out is read by `deploy.py` as its default) | steps 2 and 7 |
| A what-if shows the policy definition, which is deployed at another scope | step 2 |
| The offer accepts a budget | step 3 |
| The temporary firewall rule can be deleted with the lock on the database in place | step 7 |
| Azure SQL accepts `sql-principals.sql` as SQL Server 2022 does; `System.Data.SqlClient` signs in with the token; the server names this machine's address in its refusal (error 40615) | step 7 |
| The API as `azurebank_app` under the three real-stack test suites, and a transfer as that user | before step 7, on a local SQL Server |
| The three alert rules are accepted with these metric names (`Requests`, `TxBytes`, `Replicas`), and a test e-mail arrives | step 7 |
| Our own template passes the Deny policy; the policy refuses a second replica on a PATCH | step 7 |
| What the registry answers the workflow's token for a package that does not exist yet (anonymously, measured: `denied`); the digest line | step 6 |
| The smoke test's answers through the Azure ingress, and whether every visitor shares one sign-in limit behind it | step 8 |
| Real answer shapes: the job start, the execution states, the revision's `active` flag, a replica's container states | step 8 |
| As the identity: GET and PATCH of the app and of the job succeed with these nine actions and no right on the environment; the listing of secrets is refused with `AuthorizationFailed` | step 8 |
| On Azure, as on the local stack: the app becomes ready on an empty database, before the first migration | steps 7 and 8 |
| The automatic put-back on a real failure. Its trigger is proved by unit tests only; its request and its wait are the ones `--app-only` uses | step 8 proves `--app-only` |
| The raw log of a workflow run holds none of the three identifiers and not the app's address | step 8 |
| Whether a finished migration's log can still be read | step 8 |
| Cold start against the probes (1 s delay, 3 s period, 10 failures; 4 s timeout on readiness) | after step 8 |
| The meters after 48 hours: the three environment meters and the Dedicated one at 0 | after step 8 |
| That the identity is refused a scale change, a delete or a stop. One refusal is provoked on every deployment (the secrets listing); the policy's refusal is provoked as the owner | not provoked |
| `deploy.yml` and `ci.yml` under actionlint | the first run of the CI job `infra` |

## Checking these files

```powershell
bicep build infra/main.bicep --stdout > $null          # and guardrails.bicep; a warning is on standard error
bicep lint infra/main.bicep
python -m unittest discover -s infra -p "test_*.py"
```

`test_deploy.py` tests the deployment script's decisions against invented answers: time is a
counter and no process is started. `test_scripts.py` runs the two PowerShell scripts for real
against a stand-in for the Azure CLI, and reads the compiled templates. The CI job `infra` runs
the same three checks and actionlint on the workflows.
