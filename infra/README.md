# AzureBank Container Apps deployment draft

This is a reviewable draft, not a subscription-tested deployment. The detailed
[execution plan](PLAN.md) and [acceptance evidence](ACCEPTANCE.md) accompany it.
The baseline is remote main `5b50681`; application code and existing workflows are
unchanged by this draft. Do not deploy until the separate tools-image change lands.

## What it creates

At resource-group scope, `main.bicep` creates:

- `azurebank-env`, a workload-profiles environment with only Consumption and an
  explicit `appLogsConfiguration.destination: 'none'`. No Log Analytics workspace.
- A deterministically named logical SQL server, an Entra administrator, a separate
  SQL administrator, database `AzureBank` on Basic (5 DTU, 2,147,483,648 bytes), and
  the `0.0.0.0` Azure-services firewall rule. No Defender plan is enabled here.
- `azurebank-deploy`, a user-assigned identity with one GitHub federation for
  `repo:Gurgant/azurebank-v2:environment:demo`.
- Only when `deployApp=true`: app `azurebank`, manual job `azurebank-migrate`, and
  two role assignments scoped respectively to those resources.

The app runs BFF (0.25 CPU/0.5 Gi) and API (0.5 CPU/1 Gi) in the same replica.
HTTPS ingress targets BFF port 8080. The API binds only `127.0.0.1:5068`; both BFF
routes use `localhost:5068`, preserving ADR-0057's loopback boundary. Three HTTP
probes are configured on BFF only. There is one steady replica at most and zero
when idle. No database or request-deadline environment overrides are set.

Every application credential is a Container Apps secret referenced by `secretRef`.
The app connection uses `azurebank_app`; only the job receives the separate
`azurebank_migrator` connection. Both require encrypted, certificate-validated SQL.
The SQL administrator password is not given to either workload. The migration
connection explicitly caps the pool at five and supplies ADR-0058's connection
timeout/retry defaults, independently of the future tools host's defaults.

## One-time setup, in order

These steps belong to the subscription owner, using an account authorized to create
the infrastructure and resource-level role assignments. The workflow identity has
no bootstrap permissions. Examples below use Bash, Azure CLI with Bicep support,
Python 3, and sqlcmd with Entra authentication support. Run from the repository root.

### 1. Establish prerequisites and create the resource group

1. Review the two ADRs, the tools change, and the limitations below. The tools
   container must accept `migrate`, read the connection string, exit nonzero on
   failure, and apply migrations without seeding demo data or requiring API keys.
2. Use Azure CLI to sign in interactively and select the intended subscription.
   Register `Microsoft.App`, `Microsoft.Sql`, `Microsoft.ManagedIdentity`, and
   `Microsoft.OperationalInsights` if the environment provider requires it.
   Registration does not create a Log Analytics workspace.
3. Set a nonsecret resource-group name and create it:

   ```bash
   export AZURE_RESOURCE_GROUP='REPLACE_WITH_RESOURCE_GROUP_NAME'
   az group create --name "$AZURE_RESOURCE_GROUP" --location italynorth --output none
   ```

4. Confirm Italy North capacity and the Basic SKU on the target subscription.
   Confirm that no inherited policy silently enables extra paid services.

### 2. Supply secrets privately and deploy the foundation

1. Generate distinct random passwords and keys in the owner's password manager;
   reuse them across the two bootstrap deployments. Use at least 32 random bytes
   per key. For the three SQL passwords use 64-128 ASCII alphanumeric characters,
   including upper/lowercase and digits. This restricted alphabet is required by
   sqlcmd's textual substitution; never insert arbitrary password text in the SQL.
   The BFF service key is deliberately shared by BFF and API; all other keys differ.
2. Make an owner-only parameter file before inserting any real value:

   ```bash
   umask 077
   mkdir -p infra/.local
   chmod 700 infra/.local
   cp infra/main.example.bicepparam infra/main.local.bicepparam
   chmod 600 infra/main.local.bicepparam
   ```

   Edit the local file in a private editor. Replace every placeholder, including
   the 40-character SHA, Entra administrator object ID and display name. Keep
   `deployApp=false`. The template derives the tenant from the active subscription.
   Supply a randomly generated SQL administrator password; Bicep does not generate
   it. None of the secure parameters has a default, even during foundation creation.
3. On Windows, use an owner-only ACL instead of relying on `chmod`; disable inherited
   access before writing secrets. Keep the files outside synced folders and editor
   backups. Do not paste values into a terminal command, `--parameters key=value`,
   sqlcmd `-P`/`-v`, logs, issues, or GitHub variables. Do not use debug tracing.
4. Compile the local file into an equally protected JSON parameter file. This is
   sensitive material, not a build artifact to retain:

   ```bash
   bicep build-params --file infra/main.local.bicepparam \
     --outfile infra/.local/bootstrap.parameters.json
   az deployment group what-if --resource-group "$AZURE_RESOURCE_GROUP" \
     --template-file infra/main.bicep --parameters infra/.local/bootstrap.parameters.json
   az deployment group create --name azurebank-foundation \
     --resource-group "$AZURE_RESOURCE_GROUP" --template-file infra/main.bicep \
     --parameters infra/.local/bootstrap.parameters.json \
     --query properties.provisioningState --output tsv
   ```

   Only file paths, never secret values, appear on the command line. The ignore rule
   covers every `.bicepparam` except the example and the whole `.local/` directory.
   Do not force-add ignored files. Query the foundation's nonsecret `sqlServerFqdn`
   and `deploymentClientId` outputs in the portal or CLI.

### 3. Create the contained SQL principals as the Entra administrator

1. Connect directly to `AzureBank`, not master. Use an Azure-hosted admin terminal
   covered by the Azure-services rule, or temporarily allow the owner's exact public
   IP as a separate rule and remove that rule afterwards. The `0.0.0.0` rule does
   not allow an arbitrary local workstation, and allows other Azure tenants too.
2. Set `AZUREBANK_SQL_HOST` to the nonsecret server FQDN. The following reads the
   same private file and passes the two sqlcmd variables only in process environment:

   ```bash
   python3 - <<'PY'
   import json, os, re, subprocess
   from pathlib import Path
   values = json.loads(Path('infra/.local/bootstrap.parameters.json').read_text())['parameters']
   child_env = os.environ.copy()
   for variable, parameter in [('AppPassword', 'appSqlPassword'),
                               ('MigratorPassword', 'migratorSqlPassword')]:
       password = values[parameter]['value']
       if not re.fullmatch(r'[A-Za-z0-9]{64,128}', password):
           raise SystemExit('Use the documented SQL password alphabet and length.')
       child_env[variable] = password
   result = subprocess.run(['sqlcmd', '-S', 'tcp:' + os.environ['AZUREBANK_SQL_HOST'] + ',1433',
                            '-d', 'AzureBank', '-G', '-N', '-b',
                            '-i', 'infra/sql-principals.sql'], env=child_env)
   raise SystemExit(result.returncode)
   PY
   ```

   Configure sqlcmd's supported interactive Entra authentication for the administrator
   before running this; `-G` alone is not a portable login flow on every OS/version.
   Do not use `-C` (trust-server-certificate) or `-e` (echo input). Do not record the
   session. The SQL script grants reader/writer to both users and DDL admin only to
   the migrator, in a transaction. Existing users/passwords and memberships remain
   unchanged on a second run. This is bootstrap, not a password-rotation script.
3. Delete both private parameter files after this bootstrap phase, including after
   failure. Keep the values in the owner's password manager for step 5. Use a shell
   `trap` or equivalent cleanup so interruption does not leave files behind. Deletion
   is not guaranteed secure erasure on SSDs; use an encrypted private filesystem.

### 4. Publish images and prepare the GitHub environment

1. Have the reviewed deployment workflow available on the repository's default
   branch: [GitHub requires that for manual dispatch](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow). This draft deliberately
   opens no PR and cannot make that manual merge happen.
2. Create environment `demo`, restrict its allowed deployment branches, and configure
   its protection rules. The federated subject trusts this environment, not a branch.
3. Set repository variables `AZURE_CLIENT_ID` (the identity's output),
   `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, and `AZURE_RESOURCE_GROUP`.
   No application secrets go into repository variables.
4. Dispatch `build-push` on the intended commit. BFF/API builds use the repository
   root as context, just like compose. The future tools Dockerfile is searched at
   `backend/tools/Dockerfile` and `backend/tools/AzureBank.Tools/Dockerfile`; exactly
   one may exist. Agree the canonical path with its author before deployment.
   Absence skips tools with a warning; it does not make deployment possible.
5. Make all three GHCR packages public and confirm anonymous pulls for this exact
   SHA. A public repository does not automatically make a new GHCR package public.
   No registry credential is provisioned by this draft. All three images must have
   the same SHA tag; do not move the branch between build-push and deploy dispatches.

### 5. Create the workloads and run the first migration

1. Recreate the owner-only parameter files from step 2 with the same credentials,
   the published SHA, and `deployApp=true`. Review `what-if` again.
2. Submit `az deployment group create` with that file, a distinct deployment name,
   and `--no-wait --output none`. The job and app are created in parallel.
3. An empty database may prevent the app from becoming ready before migrations.
   As the owner, wait for `azurebank-migrate` provisioning to succeed, then start it
   once using `az containerapp job start --name azurebank-migrate --resource-group
   "$AZURE_RESOURCE_GROUP"`. Record the returned execution name, poll that exact
   execution, and require `Succeeded`. Do not wait for app readiness before this
   first migration. If creation has already failed on readiness, inspect the failure,
   migrate, then retry the same template with the same parameters.
4. Wait for the ARM deployment, the job, and the app to succeed. Verify that only
   app/job role assignments exist and allow time for RBAC propagation. As owner,
   inspect roles before retrying; never broaden the deployment identity's scope to
   bypass a failure. Delete all local secret parameter files in a finally/trap block.
5. Dispatch `deploy` on the same commit for the complete automated migration,
   rollout, and smoke-test path. Applying migrations again should be a no-op; verify
   this property of the future tools image. This workflow does not deploy Bicep.

## Subsequent deployments and failure handling

`build-push` has only contents-read and packages-write. `deploy` has contents-read
and id-token-write, runs in environment `demo`, and uses OIDC through `azure/login`.
All action pins follow CI's major-version convention, including checkout's
`persist-credentials: false`. There are no workflow-level permissions.

The deployment job's fixed concurrency group serializes deployments across branches
without cancelling an active run. GitHub may replace an older pending run with a
newer pending run; it is not a FIFO release queue. The script updates the job image,
refuses to start alongside an active/unknown migration execution, starts migration once,
and polls its specific execution. Only success permits a single PATCH updating both
app images. A unique revision suffix and a readiness check prevent testing an old
healthy revision. Smoke requires status 200, HTML with AzureBank's title, React root
and built JS asset, and readiness 200. Redirects do not count as success.

The script uses resource-scoped REST PATCH for image updates, preserving the full
container template and secret references without listing secret values or changing
configuration. Azure CLI handles job start's asynchronous operation. It makes no
environment or resource-group management requests. Each Azure call and polling loop
has a timeout; the workflow has a 75-minute outer bound. `replicaTimeout` defaults
to 600 and is limited to 1,800 seconds to fit that bound.

Failure stops the rollout; it does not undo schema changes. Migrations must be
compatible with the old revision while it is still serving. If a workflow is
cancelled or times out, inspect the server-side execution and stop it if appropriate
before retrying. Do not run manual migrations concurrently with Actions. Session
memory is lost on scale-to-zero, restarts and revision replacement. There is no
automatic database rollback or guarantee of session continuity.

## Deployment roles and scope

- **Container Apps Contributor**, built-in ID
  `358470bc-b998-42bd-ab17-a7e34c199c0f`, at `azurebank` only: chosen for app reads and
  updates. This is broader than image-only updates within that one resource.
- **Container Apps Jobs Contributor**, built-in ID
  `4e3d2b60-56ae-4dc6-a233-09c8e5a82e68`, at `azurebank-migrate` only: chosen for job
  image updates, execution reads and start. Jobs Operator cannot update the image.

The names, IDs and published action lists were checked in the
[Microsoft built-in role reference](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/containers).
**To verify:** actual role definitions and effective permission checks in the target
subscription, especially app PATCH and job start. The roles include environment
actions in their definitions, but app/job-scoped assignments do not grant access to
the sibling environment. If the service requires an environment join permission
even for an image-only PATCH, that conflicts with the brief's scope restriction;
report it for review rather than adding an environment or resource-group grant.

## Conflicts, dependencies and limits

1. **Never two replicas is not guaranteed.** `Single` mode and `maxReplicas=1` cap
   steady-state replication; old and new revisions can overlap during rollout.
   ADR-0058 explicitly budgets two API pools plus the migration pool (12+12+5=29).
   See [revision behavior](https://learn.microsoft.com/en-us/azure/container-apps/revisions).
   An absolute no-overlap requirement needs a different, downtime-bearing rollout
   decision. This draft preserves the requested settings and documents the conflict.
2. **The tools image is external work.** It is absent at the draft's base. Its path,
   entrypoint, root build context, exit behavior, migrations and connection-budget
   handling must be reviewed. The workflow never invents or substitutes a tools image.
3. **Bootstrap can wait on an empty schema.** `deployApp=true` creates both app and
   job; it cannot run migration before creating the app. Step 5 handles that explicitly
   with an owner-run first migration during provisioning. No ARM deployment script
   or additional identity grant is silently added.
4. **Required SQL grants are coarse.** The runtime has writer access to all user
   tables; this script does not implement a table-specific append-only audit role.
   Verify migrations under `db_ddladmin`, including schema ownership and any future
   permission-changing migration. Do not quietly substitute SQL admin credentials.
5. **Draft branch cannot run dispatch until the workflow exists on default main.**
   After review, the owner must arrange that merge. No PR is created by this task.
6. **Probes and cold start require measurement.** The requested 3-second period,
   1-second delay and 10 failures are retained. The default probe request timeout
   may be shorter than a database-dependent readiness request. Measure first-boot
   and cold-start behavior; no API probe can reach a loopback-only listener from
   outside its replica network namespace.
7. **Azure validation is outstanding.** Bicep/actionlint and offline tests cannot
   prove image pulls, RBAC, SKU availability, SQL login behavior or live HTTP results.

## Not verified, because I have no Azure subscription here

| Check | Basis for the draft and required live verification |
| --- | --- |
| ARM `what-if` and deployment | [Bicep what-if](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-what-if): run both parameter variants; confirm no unexpected resources or replacements. |
| Logs-none setting read back | [Environment schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2025-01-01/managedenvironments) and [log destinations](https://learn.microsoft.com/en-us/azure/container-apps/log-options): inspect `properties.appLogsConfiguration.destination` after deploy; confirm no workspace/diagnostic settings. |
| Role names and their actions | [Built-in container roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/containers): inspect both definitions in the subscription and exercise GET/PATCH/start/execution reads with only the two resource-scoped grants. |
| SQL Basic SKU and size names | [Database schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.sql/2023-08-01/servers/databases) and [DTU limits](https://learn.microsoft.com/en-us/azure/azure-sql/database/resource-limits-dtu-single-databases): read back Basic/5 and 2147483648 bytes; check Italy North availability. |
| Probe schema and behavior | [App schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2025-01-01/containerapps) and [health probes](https://learn.microsoft.com/en-us/azure/container-apps/health-probes): confirm BFF-only probes, values and readiness through cold start/SQL outage. |
| Job schema, start and completion | [Job schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2025-01-01/jobs) and [jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs): validate manual trigger, timeout, no retry, image, args, CLI response shape, and exact execution polling. |
| Image PATCH and revision gate | [App PATCH](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps/update?view=rest-resource-manager-containerapps-2025-01-01) and [job PATCH](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/jobs/update?view=rest-resource-manager-containerapps-2025-01-01): verify preservation of environment/probe/secret references and a single new app revision. |
| SQL user creation, repeat run and migrations | [CREATE USER](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-user-transact-sql) and [fixed database roles](https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/database-level-roles): run twice as Entra admin, verify unchanged users/memberships, and migrate as the contained migrator. |
| Secret substitution and Entra SQL login | [sqlcmd variables](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-use-scripting-variables) and [Entra authentication](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-authentication): verify environment substitution and the installed client's interactive token flow. |
| Public GHCR images and GitHub OIDC | [GHCR visibility](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry) and [Azure Login](https://github.com/Azure/login): exercise anonymous image pull, environment protection and federated sign-in. |
| HTTPS smoke and isolation | [Container Apps containers](https://learn.microsoft.com/en-us/azure/container-apps/containers): verify same-replica loopback, no API ingress and actual SPA/readiness HTTP 200 at the deployed URL. |

## Deliberately left out of this draft

- Budgets and alerts.
- An automatic stop.
- Policies.
- A second job for demo data.
- A custom domain.
- Key Vault.
- Log Analytics.

There is also no private endpoint, Defender plan, private-registry credential,
dedicated workload profile, or deployment to a real subscription in this draft.

## Local validation

```bash
bicep build infra/main.bicep
actionlint .github/workflows/deploy.yml
python3 -m unittest discover -s infra -p test_deploy.py -v
git diff --check
```

The compiler's generated `infra/main.json` is ignored. See `ACCEPTANCE.md` for the
captured versions, exit codes, output, and complete requirement-to-line mapping.
