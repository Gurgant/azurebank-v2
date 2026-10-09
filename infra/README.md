# AzureBank on Azure Container Apps

How the demo is created, turned on, deployed, read, operated and removed. Everything is run by
hand: the templates and the scripts from a terminal, a deployment from one workflow. Commands are
PowerShell 7, from the repository root. Why it is built this way:
[ADR-0061](../docs/adr/0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md),
[ADR-0064](../docs/adr/0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md).

## What this creates

One resource group, `azurebank-demo`, in Italy North, from `main.bicep`, in three runs. **No
database password exists**, in any file, secret or parameter: the server has no SQL
administrator login, and the app and the jobs sign in as managed identities, each bound to a
user of the database by its client ID (`sql-principals.ps1`).

| Run | What it creates |
| --- | --- |
| The foundation (`deployApp=false`): fourteen resources | `azurebank-env`, a Container Apps environment in the mode `WorkloadProfiles`, Consumption profile only. `azurebank-logs`, a Log Analytics workspace kept 30 days with a daily cap of 0.05 GB, and one diagnostic setting: console and system logs, not the ingress log. A SQL logical server that takes Microsoft Entra sign-ins only, its database `AzureBank` (Basic, 5 DTU, 2 GB), the lock `keep-the-database` and the firewall rule `AllowAzureServices`, which admits every address Azure owns: what holds the line is the sign-in. Three managed identities: `azurebank-deploy` with the federated credential `github-demo`, `azurebank-app` and `azurebank-migrate`. A custom role of nine actions. A Deny policy definition and its assignment `azurebank-shape`, which refuses, whoever asks: an app with more than one replica, a minimum above zero, several active revisions, plain HTTP, a third container, an init container or a container above half a vCPU; a job with parallel runs or any trigger but `Manual`, except a schedule on `azurebank-pool` |
| The app (`deployApp=true`): eight more | `azurebank`: the BFF (0.25 vCPU, 0.5 GiB) and the API (0.5 vCPU, 1 GiB) in one replica, zero to one replica, single revision, HTTPS ingress to the BFF's port 8080; the API listens on `127.0.0.1:5068` only. Nine secrets, each reaching a container by reference. `azurebank-migrate`, a manual job that runs `migrate`. Two assignments of the custom role to the deployment identity, on the app and on that job. The action group `azurebank-owner` and three alert rules on the app, which notify and stop nothing: more than 66,667 requests in an hour, more than 3.3 GiB sent in a day, a replica running more than about 2.2 hours in a day. The run also deploys `app-inputs.bicep`, which creates nothing: it refuses an image tag that is not 40 characters, an empty address for the alerts and an empty secret. The `bff` container believes no forwarded header unless a run names networks of proxies (`proxyNetworks`); the framework's own switch for forwarded headers is written nowhere, and `--check` refuses an app that carries it: with it on, the BFF believed whatever a caller wrote |
| The demo (`demo=true`): two more | `azurebank-pool`, a job on the schedule `0 */4 * * *` (UTC) that runs `recycle` as the app's database identity ([`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md)), and a third role assignment, on that job. Both containers of the app carry `Demo__Enabled=true`: registration is closed, and a visitor claims a prepared copy (ADR-0063) |

## What it costs

| Meter | Price (USD, Italy North, read on 2026-10-02) | Free each month | What bounds it |
| --- | --- | --- | --- |
| SQL Database Basic | $0.161 a day | none | fixed: $4.83 to $4.99 a month |
| Container Apps, vCPU and memory while active | $0.000034 a vCPU-second, $0.000004 a GiB-second | 180,000 vCPU-seconds, 360,000 GiB-seconds: 66.7 hours of a running replica | one replica, by the template and by the Deny policy |
| Container Apps, requests | $0.40 a million | 2 million | nothing |
| Data out to the internet | $0.087 a GB | the first 100 GB | nothing |
| Log Analytics, logs ingested | $2.99 a GB | the first 5 GB, not confirmed for this offer | the daily cap of 0.05 GB, which is not a hard bound |

The pool job is computed at 1.65 % of the free amounts: 180 runs a month of 66 s. The BFF's rate
limiter bounds neither the requests nor the log: a refused request is counted, and logged.

## Before you start

- Tools: the Azure CLI with no extension, the Bicep CLI on `PATH` (0.47.16), PowerShell 7,
  Python 3.12 or later, Docker, the GitHub CLI, the .NET SDK 10, and go-sqlcmd 1.10.0 or later
  at `C:\Program Files\sqlcmd\sqlcmd.exe` (`winget install --id Microsoft.Sqlcmd -e`).
- Rights: an account with the Owner role on the subscription, which also becomes the database's
  Microsoft Entra administrator, and admin of the repository.
- No secret is typed, printed or put on a command line: `secrets.ps1` writes the one parameter
  file outside the repository and reports where each value came from, never what it is.
- Never `--debug`, and never `az sql server ad-only-auth disable`.
- One writer at a time: no run of the template while a `deploy` run of the workflow is queued,
  waiting or in progress, or while `deploy.py` runs in a terminal.

Start signed in (`az login`, `gh auth login`) and with the first three lines below. The last two
are **a run of the template**: a step names the arguments of `secrets.ps1` and the run's name.

```powershell
. ./infra/runbook.ps1                        # $group, $folder and Invoke-Template
$env:AZURE_SUBSCRIPTION_ID = az account show --query id --output tsv
$env:AZURE_RESOURCE_GROUP  = $group
try { ./infra/secrets.ps1 -Action New -DeployApp; Invoke-Template 'change' }   # a what-if, then "yes" deploys
finally { ./infra/secrets.ps1 -Action Remove }
```

## Create it once

1. Create the resource group, `az group create --name $group --location italynorth --query
   properties.provisioningState --output tsv`, then the foundation: a run of the template with
   `-Action New` alone, as `'foundation'`. Expected: fourteen resources to create, `Succeeded`.
2. Create the two database users: `./infra/sql-principals.ps1`. Expected at the end:
   `azurebank_app: db_datareader, db_datawriter; ID as asked: 1`, the line of
   `azurebank_migrator`, which adds `db_ddladmin`, and `Firewall rules now: AllowAzureServices.`
   Run nothing else as administrator in this database: a trigger there would run as the
   administrator, so the file refuses a database that holds code (`Msg 50003`, `Msg 50004`).
3. Prove that a SQL-password sign-in is refused:
   `./infra/sql-principals.ps1 -ProveSqlSignInRefused`. Expected: a line that starts "Proved:".
   That each identity signs in as its own user was proved once, by a throwaway probe job.
4. Create the GitHub environment `demo` before the workflow first runs, or the workflow creates
   it with no rule at all: deployment branches `main` only, and a required reviewer.
5. Store the three Azure identifiers as secrets of `demo`, through a pipe: no log prints them.
   ```powershell
   az identity show --resource-group $group --name azurebank-deploy --query clientId --output tsv | gh secret set AZURE_CLIENT_ID --env demo --repo Gurgant/azurebank-v2
   az account show --query tenantId --output tsv | gh secret set AZURE_TENANT_ID --env demo --repo Gurgant/azurebank-v2
   az account show --query id --output tsv | gh secret set AZURE_SUBSCRIPTION_ID --env demo --repo Gurgant/azurebank-v2
   ```
6. Publish the images, then check with no login that they can be pulled: `exit 0` three times.
   ```powershell
   gh workflow run deploy.yml --ref main -f action=build-push
   $sha = '<the full SHA of the commit that was built>'
   $empty = New-Item -ItemType Directory -Path (Join-Path $env:TEMP "docker-$PID")
   'api', 'bff', 'tools' | ForEach-Object { docker --config $empty manifest inspect "ghcr.io/gurgant/azurebank-${_}:$sha" > $null; "$_ exit $LASTEXITCODE" }
   ```
7. Create the app: a run of the template with `-DeployApp -ImageTag $sha`, as `'app'`.
   Expected: eight resources to create, `Succeeded`. Verify the alerts' address when asked.
8. Deploy for the first time, from the terminal: `$env:IMAGE_TAG = $sha`, then
   `python infra/deploy.py`. Expected: a verdict with exit code 0, and at the end "Smoke passed".

## Turn the demo on

Needs the app deployed on images that read the demo's flag, as `main`'s do. Start more than 60
minutes before the pool job's next run, at minute 0 of the hours 0, 4, 8, 12, 16 and 20 UTC.

1. Turn it on: a run of the template with `-DeployApp -DemoOn`, as `'demo-on'`; add
   `-AlertPushAccount <address>` for alerts through the Azure mobile app. Expected in the report:
   `demo: true, asked for with -DemoOn` and `alertPushAccount: from -AlertPushAccount`, or
   `alertPushAccount: not written, the template's default applies`.
2. Check at once: `python infra/deploy.py --check`. Expected: "no other revision is active: what
   answers now is that revision", "The pool job's PIN pepper and connection string are the
   app's", "It is the public demo: the page carries the demo's tag, and a registration with an
   empty body was refused as closed." If it does not pass, act before the job's next run:
   delete the job (`az containerapp job delete --name azurebank-pool --resource-group $group
   --yes`), or if it ever ran stop the app ([Operating it](#operating-it)). Then run step 1 again.
3. Fill the pool, or leave it to the schedule: `python infra/deploy.py --pool-run`. Expected:
   "Starting the job azurebank-pool once." and "The pool run ended well". Then claim a copy in a
   browser and send a transfer with its PIN: that proves the job's PIN pepper is the API's.
4. Read which address the app counts a visitor by: send sign-ins until one is refused with 429,
   and eight minutes later read the rate limiter's warnings with `--app-log 15`. With no network
   named they name the platform's own addresses: every visitor then shares the limits.
   ```powershell
   $site = az containerapp show --name azurebank --resource-group $group --query properties.configuration.ingress.fqdn --output tsv
   python -B -c "import json, sys; sys.path.insert(0, 'infra'); import deploy; print(json.dumps(deploy.SMOKE_LOGIN))" | Set-Content "$env:TEMP\sign-in.json"
   $signIn = { param([string]$Named)
       $headers = @('--header', 'Content-Type: application/json')
       if ($Named) { $headers += '--header', "X-Forwarded-For: $Named" }
       curl.exe --silent --output NUL --write-out '%{http_code}' --request POST @headers --data-binary "@$env:TEMP\sign-in.json" "https://$site/bff/auth/login" }
   1..12 | ForEach-Object { & $signIn }                         # twelve statuses
   ```
5. Name the ingress's network: a run of the template with `-DeployApp -ProxyNetworks <networks>`
   (CIDR, separated by commas), as `'proxy-networks'`. It must hold every address step 4 read and
   nothing a visitor can connect from: Microsoft's page on Container Apps networking names the
   ranges an environment reserves for its own infrastructure. Expected:
   `proxyNetworks: from -ProxyNetworks`, and from `--check` a line ending "No network was shown."
6. Prove it before the link is published, not sooner than two minutes after `--check`: the
   twelve sign-ins again and, 90 seconds later,
   `1..12 | ForEach-Object { & $signIn "203.0.113.$_" }`, each naming another address as its own.
   Expected both times: 401 ten times, then 429 twice, and every warning naming the caller's own
   address. Twelve times 401 is a stop: take the networks out with `-ProxyNetworks none`.

## Deploy a commit

```powershell
gh workflow run deploy.yml --ref main -f action=build-push
gh workflow run deploy.yml --ref main -f action=deploy
```

Approve the `deploy` run on GitHub: the environment `demo` waits for its reviewer. `build-push`
pushes the commit's three images, tagged with its SHA. `deploy` runs `infra/deploy.py` as the
deployment identity. The script reads the app, says "Running now" with the images to go back
to, and stops if a shape drifted or a pool run may be in progress; with the demo off it says
"The app says the demo is off: the job azurebank-pool is not read and not moved." It
proves that its own identity is refused the app's secrets ("the listing was refused"), runs the
migration and prints its verdict, never its text: the log of a public repository is public.
Then it moves the pool job's image, with the demo on, and the app's images, waits until only
the new revision answers, and smoke-tests the address: the page, `/health/ready`, and a sign-in
for an address nobody can register, refused with 401 `INVALID_CREDENTIALS`, the check that
fails when a migration did not run; on the demo also the page's tag and a closed registration.
On a failure it puts the app back, and the run still fails; the schema is never put back.

## Reading the logs

```powershell
python infra/deploy.py --job-log '<execution>'    # a migration: its verdict, then what it printed; no name: the latest
python infra/deploy.py --pool-log '<execution>'   # a run of the pool job, the same way
python infra/deploy.py --app-log 30               # what the app's two containers printed in the last 30 minutes
python infra/deploy.py --check                    # read the running app and check it: nothing is moved
python infra/deploy.py --pool-run                 # start the pool job once, by hand, and wait for that run
```

- From a terminal, signed in: each is refused inside GitHub Actions. A log command first says
  what the daily cap is doing, and an empty answer is not proof that nothing was printed: a line
  takes minutes to arrive. Never paste a log's text anywhere: it can hold a caller's address.
- `--check` is the read-back of a run of the template and of anything else that could change
  the app or the pool job: a deployment, a start of the job by hand, a stop and a start. With
  the demo off it says "The app says the demo is off: the job azurebank-pool is not read and no
  secret is listed."; until a run names networks, "The bff container names no network of
  proxies". It ends "Checked: ...; the smoke test passed; nothing was moved."
- `--pool-run` ends by the exit code of the run's container, never by the execution's status:
  Azure shows `Failed` for a run that exited 10. Its last line names the count to read on the
  run's summary line, which starts `pool:`; when Azure reports no code, that line decides.

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

## When something fails

| What you see: each row quotes what a script prints | What it means, and what to do |
| --- | --- |
| `build-push`: "The registry gave no clear answer"; `deploy`: an image "is not published, or its package is not public" | For packages that were never published, run `build-push` again with `-f first_publication=true`. For the second, run `build-push` at this commit; if the check of [Create it once](#create-it-once), step 6, still answers `denied`, set that package to Public in its settings, which cannot be undone |
| "is not in the shape this script deploys onto" | Something changed the app or a job outside the template; the words in brackets say when it was read. Run the template again ([Operating it](#operating-it)). If it is the identity, read "If something was stolen" there first |
| "This identity can list the secrets"; "Azure asked for a right on a database identity" | For the first, the deployment identity holds more than its two role assignments, three with the demo on. For the second, stop: no role is added, since that right would let the deployment identity attach the schema-changing identity to the app that faces the internet |
| "Execution ... is Running: a migration may still be running"; "a pool run may still be in progress" | A run blocks the next deployment or start until it ends: wait for the job's timeout. Still refused: `az containerapp job stop --name azurebank-migrate --resource-group azurebank-demo --job-execution-name <name>`, or the same with `azurebank-pool` |
| "The migration did not succeed" | The app still runs the old images. Exit code 1: running it again is safe. Exit code 2: the configuration must change. Read `python infra/deploy.py --job-log` when its lines are due. With no text there, start the job again, `az containerapp job start --name azurebank-migrate --resource-group azurebank-demo`, or migrate by hand, below |
| "the new revision never became ready" or "Smoke test failed", then "put back to ..." | The app was put back and the schema stays. Read the lines the run printed, then `python infra/deploy.py --app-log 30`. A sign-in answered 500 or 503 where 401 was expected can be the database sign-in. "The page does not carry the tag of the public demo": the BFF's image does not read the flag |
| "Smoke test unproven", also "in 4 tries the registration probe got 429 (rate limited) last"; after either smoke test's sentence, "This was --check, not a deployment" | The last of four tries was a 429 or got no answer: nothing was proved, and the new revision stays. Deploy again later, or run `--check`. The last sentence is a check's own: read "check again" where the one before it says "deploy again" |
| "the registration probe expected 403 REGISTRATION_CLOSED", then "put back to ..." | `python infra/deploy.py --check` on the revision that was put back. If it fails the same way, registration is open beside the pool: stop the app |
| "The put-back to ... did not succeed (...). The app may be serving a broken revision"; "Interrupted. Nothing is put back and nothing is stopped by this" | Read what was moved or started with `--check`, `--job-log` and `--pool-log`; then deploy again, or go back by hand, below |
| `sql-principals.ps1` cannot sign in, or the server refuses `WITH SID ..., TYPE = E` | For the first: `-AuthenticationMethod ActiveDirectoryDefault`, then the older ODBC `sqlcmd` with `-SqlcmdPath` and `-OdbcSignInName`. For the second: `-CreateForm ExternalProvider` |
| "Demo__Enabled is true in ['bff'] and not in ['api']", or "Demo__Enabled in the container ... is something the template never writes" | A setting was changed by hand, and no side is chosen. `secrets.ps1` refuses such an app too: "The two containers of the deployed app disagree about Demo__Enabled. Nothing was written." Put the setting right on the app, then run the template and `--check`. If the pool job ever ran, the right value is on |
| "the settings about forwarded headers in the container 'bff' are not what the template writes"; from `secrets.ps1`, "The bff container of the deployed app carries a forwarded-headers setting this template never writes." | Set by hand. One run of the template writes the `bff`'s settings whole: with `-ProxyNetworks` and the networks that are meant, or with `none` |
| "The app says the demo is on, and the job azurebank-pool could not be read"; "The executions of the job azurebank-pool could not be read, so whether a pool run is in progress is not known"; `--pool-run`: "infra/main.bicep writes that job only with the demo on" | A deployment and `--check` need that job and its executions. Run it again; if the job is gone, a run of the template creates it, and until then only `--app-only` moves the app. Refused again: read the role assignment on the pool job. Where the demo was never turned on there is no such job: [Turn the demo on](#turn-the-demo-on) first |
| `--check`: "not in a state to be checked"; "did not show ... answering alone (...)"; "came with a link to a next page"; "The app is in shape and reports no address" | An answer could still come from another revision, or there is no address to ask: nothing was proved, nothing was moved. Look at the app's revisions and its ingress, then check again |
| `--check`: "The pool job's PIN pepper (its secret pin-pepper) differs from the app's. Nothing was moved, and no value was shown." | No copy that job built takes a PIN, and every status stays good. Stop the app; run the template, which writes the job's secrets from the app's; start the app; check again. A free copy built with the other pepper is handed out until it is too old to count, 44 hours by default |
| `--check`: "The pool job's secrets could not be compared with the app's"; "The secrets of ... could not be listed" | Listing secrets needs an account with the Owner role on the subscription. With one: read the secret names of both, run the template, check again |
| `--pool-run`: "was asked to start once"; "failed while its end was waited for"; "Timed out waiting ... (the job's timeout and two minutes)" | Whether a run began, or ended, is not known, and the start is not sent again. `python infra/deploy.py --pool-log` before any second start |
| `--pool-run`: "The pool run did not end well"; "Azure reported no exit code for it"; "and the read of its exit code failed" | By its code: the table under [Reading the logs](#reading-the-logs) and [`docs/runbooks/demo-pool.md`](../docs/runbooks/demo-pool.md); after exit code 1, no second start before the run's last line has been read. With no code, the run may have ended well: its `pool:` summary line, read with `--pool-log <the execution>`, is the verdict |

**Going back by hand**, to a commit whose images are published: `$env:IMAGE_TAG = '<its full
SHA>'`, then `python infra/deploy.py --app-only`, which moves the app only. With the demo on,
only to a commit that reads the demo's flag: `git merge-base --is-ancestor e5107f0f '<its full
SHA>'` must exit 0. The schema has no road back: `migrate` refuses a database ahead of the build.

**A migration by hand, as a last resort**, when the job leaves no text to read: from a checkout of
the deployed commit, after `./infra/sql-principals.ps1`, with this machine let in by a temporary
firewall rule: the first try is refused and names its address. It is the one exception to "nothing
else as administrator". Run `dotnet run --project backend/tools/AzureBank.Seeder --configuration
Release -- migrate --wait-seconds 300` with `ConnectionStrings__DefaultConnection` set to
`Server=tcp:<the server's host name>,1433;Database=AzureBank;Authentication=Active Directory
Default;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60`.

## Operating it

- **Stop the app by hand**: nothing else stops it, and the deployment identity cannot. `/start`
  starts it again. Its state is `properties.runningStatus`, and a stopped app answers 404.
  ```powershell
  $app = az containerapp show --name azurebank --resource-group azurebank-demo --query id --output tsv
  az rest --method post --url "https://management.azure.com${app}/stop?api-version=2025-01-01"
  ```
- **Changing the infrastructure later**: edit the template, make a run of it with `-DeployApp`,
  then `python infra/deploy.py --check`. The parameter file keeps what is deployed, and the
  report says so: `demo: kept from the deployed resource`,
  `alertPushAccount: kept from the deployed resource`,
  `proxyNetworks: kept from the deployed resource`; with nothing deployed to read,
  `demo: not written, the template's default applies` and
  `proxyNetworks: not written, the template's default applies`. `-ProxyNetworks none` takes the
  networks out: `proxyNetworks: none, asked for with -ProxyNetworks`. An override is not
  remembered: pass it at every run, as in `Invoke-Template 'change' @('denyPolicy=false')`. A
  run that changes the app makes a new revision, which signs every visitor out. Its what-if also
  lists the `bff`'s probes as changed and properties the template never writes as to be
  deleted: neither is a reason to answer "no".
- **Switching the logs off**, when a cost shows on the log ingestion meter: `keepLogs=false`
  alone deletes nothing. Delete the diagnostic setting `to-azurebank-logs`, make a run of the
  template with `-LogsOff` (and `-DeployApp` if the app exists), then delete the workspace.
- **Turning the demo back**: no switch turns the demo off. The pool job's first run creates the
  roles a registration needs: after it, an app whose flags are off registers whoever asks,
  beside the pool. If the job never ran (`python infra/deploy.py --pool-log`): delete it, then a
  run of the template with `-DeployApp` as `Invoke-Template 'demo-off' @('demo=false')`. Once
  it has run, two roads: stop the app; or a new, empty database, in this order: delete the pool
  job; remove the lock `keep-the-database` and delete the database; the same run with
  `@('demo=false')`; `./infra/sql-principals.ps1`; the workflow's `deploy`; `--check`.
- **If something was stolen**: no password exists to change. Delete the app and both jobs;
  delete the identities `azurebank-app` and `azurebank-migrate` and create them again; run
  `./infra/sql-principals.ps1`, which replaces both users; make a run of the template with
  `-DeployApp -ImageTag <a known commit>`, and `-DemoOn` if the pool job ever ran; then deploy.
- **Before renaming or transferring the repository**, delete the federated credential
  `github-demo` of `azurebank-deploy`: it trusts the repository by name.

## What each identity can do

| Identity | Who acts as it | It can | It cannot |
| --- | --- | --- | --- |
| `azurebank-deploy` | A job of the GitHub environment `demo` | Read and write the app and each job it is assigned on, start such a job, read executions, revisions and replicas. Writing the app is more than moving an image: it can run any public image with the app's secrets in its environment, as the app's database identity | List secrets, delete or stop anything, attach an identity, read the logs, touch the environment, the database server, the workspace or a role. It has no user in the database |
| `azurebank-app` | Both containers of the app, and the pool job | Read and write every row | Change the schema. It holds no role on any Azure resource |
| `azurebank-migrate` | The migrate job | The same, and change the schema; it can create a trigger, which is why the users file is guarded | Create a user or make itself an owner of the database. It holds no role on any Azure resource |

- The boundary is who can run a job in the environment `demo`: its branch rule (`main` only),
  its required reviewer, and whoever can edit the environment or merge to `main`.
- The Deny policy is the one guard the deployment identity cannot change. `deploy.py`'s shape
  check catches drift, and it alone reads the pool job's schedule and timeout.

## Measured on Azure

One run each unless a row says otherwise. What is dated 2026-10-02 is from a throwaway group.

| What | Value | Date | What it proves |
| --- | --- | --- | --- |
| The foundation, from the template | The what-if listed fourteen resources to create and nothing else; `Succeeded` after 17 minutes; a second run created and deleted nothing. A request for an environment that named no mode was taken as Express and refused, `ExpressEnvironmentFeatureNotSupported`; with `WorkloadProfiles` named, that mode read back | 2026-10-02, 2026-10-03 | The template creates what this page lists and can be run again; why it names the environment's mode |
| A SQL server with no SQL administrator login | Accepted, and accepted unchanged when sent a second time. A SQL-password sign-in was refused: "Azure Active Directory only authentication is enabled" | 2026-10-02, 2026-10-03 | No database password exists to guess or to steal |
| A sign-in as each managed identity | Each signed in as its own user, made from the identity's client ID, with the roles expected. A token for the other identity was refused: error 0, class 20, around `Azure.Identity.AuthenticationFailedException`. An identity signing in where it had no user: error 18456, class 14. The first open on a cold replica took 6,390 ms and 3,253 ms | 2026-10-02, 2026-10-03 | The two identities are isolated, and the first sign-in fits the app's 10 s connect timeout |
| A what-if and secure parameters | With the app's name read through a secure parameter, the what-if listed the app and its role assignment as `Unsupported`. With the name a plain value it named every resource of the run | 2026-10-03 | Why the check of the app's values is in `app-inputs.bicep` |
| The Deny policy | It refused two replicas on the app, a job named `azurebank-pool-2` on a schedule and two runs at once on the pool job, each with `RequestDisallowedByPolicy`, and accepted the pool job on its schedule | 2026-10-03, 2026-10-06 | The shape holds whoever asks, and the exception is by the whole name |
| What Azure reports of a job's execution | Status, times, the container's exit code and a reason, on API version `2026-07-01`. A run made to exit 7 read `Failed` with `BackoffLimitExceeded`, and so did a pool run that exited 10, the signal of a run that finished. Three of the first four scheduled runs carried no exit code, and an older execution no longer carried its own | 2026-10-02 to 2026-10-07 | The exit code decides, read when the run ends; when Azure reports none, the run's `pool:` line does |
| The app from outside | `http://` answered 301 to `https://`; `/health/ready` answered `Healthy`; `POST /api/auth/login` answered 404; port 5068 gave no connection | 2026-10-03 | Only the BFF is exposed |
| A deployment's times | The migration job 24 to 41 s; the new revision ready 35 to 48 s after the migration's verdict; the workflow's `deploy` about two and a half minutes, four runs | 2026-10-03 to 2026-10-07 | What a deployment takes |
| The deployment identity | Refused the listing of the app's secrets at every run. It changed the app, the migrate job and the pool job, and Azure asked for no right on the identity each carries. Five counts of identifiers in the raw log of a run: 0 each | 2026-10-03, 2026-10-06 | The nine actions are enough, and a public log holds no identifier |
| Log lines in the workspace | Readable about six and a half minutes after they were written, eight at the most; a later run's were there five minutes after it ended. A read by the job's name and a period also printed the next execution's lines: each line carries its execution's name in `ContainerGroupName`. The workspace's metric `Ingestion Volume` had no time series for an hour in which 446 rows were ingested | 2026-10-02 to 2026-10-06 | An empty read proves nothing; why a log is read by execution; and why the alert on that metric, which cannot count lines, is built only when asked for |
| `migrate` by hand, from a checkout | No token within the tool's own connect timeout of 10 s. With `Connect Timeout=60` in the string it signed in and ended with exit code 0, nothing to migrate | 2026-10-03 | The last-resort road works as it is written |
| The demo turned on | One run of the template, under two minutes: nine secret names, two jobs and three role assignments read back, and `--check` passed with the job's two secrets the app's, the page tagged and a registration refused as closed. The first fill built 50 copies in 66 s, with exit code 0 and `foreignUsers=0` | 2026-10-06 | One run turns both containers, creates the scheduled job and closes registration; the pool is built with no password |
| The pool job on its schedule | The first scheduled run began at 20:00:00Z; runs of 26 s and 30 s with nothing to build, 44 s with five copies. One of the first four failed: the request for the identity's token was cancelled at its first open of the database, with no summary line, no exit code, no retry and no warning. A read found it seven hours later, and the next run built the pool back to 50 | 2026-10-06, 2026-10-07 | `0 */4 * * *` is read in UTC and starts unattended; nothing warns of a failed run |
| Azure's lists and its activity log | Lists of 3, 7 and 10 entries came with no link to a next page; a job that never ran answered an empty list; a job on a schedule was also started by hand. The activity log held two events for each start sent by hand or by the workflow, and none for a scheduled run | 2026-10-06 | The reads `deploy.py` makes hold at this length; an alert on start events would not see the schedule |
| A browser on the deployed demo | A copy claimed, a transfer and a deposit answered 201, the PIN checked with `verified` true; no token-shaped value in any body or in `localStorage`; no sideways scroll at 375 px. Over 30 days of logs no line names a registration | 2026-10-06, 2026-10-07 | The API obeys the flag and takes a PIN hashed by the job; no user exists outside a copy |
| A cold start, a stop and a start | From zero replicas the first request answered after 27.0 s, and on another day after 24.6 s; the first sign-in after it in 6.0 s, against 0.6 s warm, with no 503. Stopped by hand, `runningStatus` read `Stopped` after 14 s and `/health/ready` answered 404; started, it read `Running` after 22 s | 2026-10-06, 2026-10-07 | A visitor waits about 25 s once, and the database sign-in fits its timeout; the two calls of [Operating it](#operating-it) work |
| The address the app counts a visitor by | With no network named: twelve sign-ins in under eight seconds all answered, and the warnings named two internal addresses. With the ingress's network named: ten sign-ins a minute answered and the eleventh refused, five times; a forged `X-Forwarded-For` ignored; another network served in the same minute; 179 of 179 warnings named the caller's own address | 2026-10-06, 2026-10-07 | Behind the ingress the limits are one visitor's own only with `proxyNetworks` |
| An alert that fired | The rule on replica time, severity 2: a day's average of 0.0985 against the threshold 0.093. Its e-mail arrived in the same minute and its push notification within a few minutes. The `Replicas` metric reported 0, not nothing, for idle hours | 2026-10-07 | The alerts arrive both ways, and that rule can be computed on an app that scales to zero |
| The cap of copies for one address in any 24 hours | After the run of the template that took an override of 1,000 out: 12 settings on `api` where there were 13, 3 on the pool job where there were 4, 7 on `bff` as before, and the next deployment kept them. A refused eleventh claim was not observed | 2026-10-07 | The application's default of 10 applies |
| Cost and log volume | Month to date, three days after the database was created: SQL Database 0.5018 EUR; log ingestion, alert rules and e-mails 0.0000; no row for any Container Apps meter. Logs: 0.19 and 0.22 MB a day of the 50 MB cap, a console line billed 845 bytes on average | 2026-10-03, 2026-10-06 | The database is the one cost so far; the Container Apps meters were absent, not read at 0 |

## Not measured yet

- A visitor refused an eleventh copy within 24 hours, 429 `DEMO_DAILY_LIMIT`: read in the code.
- A registration on a database that only `migrate` has touched. On a local stack (2026-10-05) it
  answered 500 and left no user and no role; after `seed`, the same body answered 201.
- Whether the free 5 GB of logs apply to this offer, and what a pool run is billed.
- The automatic put-back on a real failure, and a workflow run cancelled in mid-run.
- A list longer than ten entries: `deploy.py` reads one answer and follows no link to a next page.
- No road that deletes has been run: [Operating it](#operating-it), Removing everything.

## What is not here

- Nothing stops the app or the logs automatically: the alerts only notify. None is on a failed
  run of the pool job, on the database's size or on the log's volume, and a test notification is
  refused on this subscription offer. No SQL auditing: a sign-in attempt leaves no record.
- No switch that turns the demo off, and nothing that removes a user from the database. Until
  the demo is turned on, a first deployment has a schema, no rows and its registration open.
- No rotation of an application secret, no Key Vault, no private endpoint, no deployment by
  image digest, no rollback of the schema, and the API in no app of its own.
- The networks of the ingress are written in no file: a run names them, and a deployment made
  anew starts with none, so that its visitors share the limits and the cap of copies.

## Removing everything

In this order; then, in GitHub's settings, the environment `demo` and the three packages.

```powershell
az lock list --resource-group $group --query '[].id' --output tsv | ForEach-Object { az lock delete --ids $_ }
az monitor log-analytics workspace delete --resource-group $group --workspace-name azurebank-logs --force --yes
$principal = az identity show --resource-group $group --name azurebank-deploy --query principalId --output tsv
az role assignment list --assignee $principal --all --query '[].id' --output tsv | ForEach-Object { az role assignment delete --ids $_ }
az role definition list --custom-role-only true --resource-group $group --output json | ConvertFrom-Json |
    Where-Object roleName -like 'AzureBank deploy *' | ForEach-Object { az role definition delete --name $_.name --resource-group $group }
az group delete --name $group
az policy definition list --output json | ConvertFrom-Json |
    Where-Object { $_.policyType -eq 'Custom' -and $_.displayName -like 'AzureBank: one small replica*' } | ForEach-Object { az policy definition delete --name $_.name }
```

## Checking these files

```powershell
bicep build infra/main.bicep --stdout > $null          # and the other two templates; a warning is on standard error
bicep lint infra/main.bicep
python -m unittest discover -s infra -p "test_*.py"
```

Both Bicep commands must write nothing to standard error. The tests are offline, against
stand-ins for the Azure CLI and `sqlcmd`. They also read this page and `docs/runbooks/demo-pool.md`:
their headings, quoted sentences, commands and exit codes are held to the scripts.
