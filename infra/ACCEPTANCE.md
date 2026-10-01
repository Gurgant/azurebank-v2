# Draft acceptance evidence

Validated locally on 2026-10-01, based on remote main `5b50681`.
No Azure resources, SQL users, or GHCR images were created during this task.
The tools-image change is not part of this branch.

## Commands and output

Official release binaries: Bicep CLI `0.47.16 (3f73e1a234)` and actionlint `1.7.12`.
Binaries were downloaded from their publishers' GitHub releases into a temporary
directory. Azure CLI is not installed here; the brief permits standalone Bicep.

```text
$ bicep build infra/main.bicep
```

Exit **0**. Actual stdout and stderr: **empty**. No warnings.

```text
$ actionlint .github/workflows/deploy.yml
```

Exit **0**. Actual stdout and stderr: **empty**. ShellCheck was not available;
actionlint's own workflow, expression and syntax checks ran.

Additional checks:

```text
$ bicep build-params infra/main.example.bicepparam
```

Exit **0**, stdout/stderr empty. The example's all-zero SHA is a placeholder and
must be replaced before deployment; compilation does not make placeholders usable.

```text
$ python -m unittest discover -s infra -p test_deploy.py -v
Ran 15 tests in 0.015s
OK
```

Exit **0**; the test summary above is excerpted. Tests use mocked Azure/HTTP calls.
They exercise failure gates, exact execution selection, ambiguous starts, overlap,
timeouts, atomic image changes, exact ready revision selection, and SPA validation.
They do not constitute cloud, container-build, or SQL execution tests.

Compiled-template inspection found 10 resources: environment, SQL server/database/
firewall rule, managed identity/federation, app/job, and two role assignments. Only
app, job and their role assignments are conditional on `deployApp`. All ten secret
parameters compile to secureString without defaults. Role assignment scopes are
only the app and job. The app contains BFF/API together, and API has no probes.
There is no workspace, Defender plan, Key Vault, private endpoint, custom domain,
or Dedicated profile. Exact compose environment names and secretRef mappings were
compared with the source. Private parameter ignore rules were checked, including
that the placeholder example remains trackable.

`git diff --check` passed. Final staged scope, line endings and commit/push status
are verified at handoff. Application tests were not rerun: no application changes
are included relative to the fetched main baseline.

## Requirement → file:line

Line references identify the start of the relevant declaration or procedure; a row
may be implemented across the following lines. For negative requirements, the full
resource inventory and final diff are also part of the evidence.

| Requirement | File:line |
| --- | --- |
| Requested branch, push, no PR; plain English commit without attribution | [infra/PLAN.md:82](PLAN.md#L82) |
| Only new infra files and the new deploy workflow; no application/test/existing-workflow edits | [infra/PLAN.md:3](PLAN.md#L3) |
| Read compose, Dockerfiles, CI, ADR-0057 loopback and ADR-0058 Preconditions | [infra/PLAN.md:18](PLAN.md#L18) |
| No secret defaults or literal subscription/tenant/client IDs; IDs supplied by parameters/variables | [infra/main.bicep:15](main.bicep#L15) |
| Match repository line endings | [infra/PLAN.md:16](PLAN.md#L16) |
| Resource-group scope | [infra/main.bicep:1](main.bicep#L1) |
| Location defaults to italynorth; full SHA; deployApp defaults false | [infra/main.bicep:3](main.bicep#L3) |
| Entra administrator object ID and login parameters | [infra/main.bicep:9](main.bicep#L9) |
| All ten secret inputs are secure and required | [infra/main.bicep:15](main.bicep#L15) |
| Consumption-only workload profiles; no Dedicated | [infra/main.bicep:43](main.bicep#L43) |
| Explicit logs destination none; no workspace | [infra/main.bicep:41](main.bicep#L41) |
| No private endpoint or custom domain | [infra/README.md:290](README.md#L290) |
| One conditional azurebank app, single revision mode | [infra/main.bicep:116](main.bicep#L116) |
| BFF GHCR image, 0.25 CPU, 0.5 Gi | [infra/main.bicep:144](main.bicep#L144) |
| API sidecar GHCR image, 0.5 CPU, 1 Gi | [infra/main.bicep:173](main.bicep#L173) |
| API URL binds loopback 127.0.0.1:5068 | [infra/main.bicep:181](main.bicep#L181) |
| External ingress targets 8080 and disallows insecure HTTP | [infra/main.bicep:124](main.bicep#L124) |
| Scale zero to one; absolute no-overlap conflict documented | [infra/main.bicep:193](main.bicep#L193) |
| BFF startup/liveness live; readiness ready; 3s period, 1s delay, 10 failures; no API probe | [infra/main.bicep:156](main.bicep#L156) |
| Production environment on BFF and API | [infra/main.bicep:151](main.bicep#L151) |
| BFF BackendApi__BaseUrl uses localhost:5068 | [infra/main.bicep:152](main.bicep#L152) |
| BFF proxy destination retains backend-api hyphen and localhost:5068 | [infra/main.bicep:153](main.bicep#L153) |
| BFF and API share ServiceCredential__BffKey via secretRef | [infra/main.bicep:154](main.bicep#L154) |
| API ConnectionStrings__DefaultConnection is a secretRef | [infra/main.bicep:182](main.bicep#L182) |
| API Jwt__Secret is a secretRef | [infra/main.bicep:183](main.bicep#L183) |
| API Idempotency__HashKey is a secretRef | [infra/main.bicep:184](main.bicep#L184) |
| API StepUp__BindingKey is a secretRef | [infra/main.bicep:185](main.bicep#L185) |
| API Audit__ChainKey is a secretRef | [infra/main.bicep:187](main.bicep#L187) |
| API Audit__AnchorKey is a secretRef | [infra/main.bicep:188](main.bicep#L188) |
| API Security__PinPepper is a secretRef | [infra/main.bicep:189](main.bicep#L189) |
| No Database__ or RequestDeadline__ variables | [infra/main.bicep:181](main.bicep#L181) |
| One conditional manual azurebank-migrate job with tools SHA image and migrate arg | [infra/main.bicep:201](main.bicep#L201) |
| Migration secret separate from app; encrypted connection; max pool 5 | [infra/main.bicep:114](main.bicep#L114) |
| Job parallelism/completion 1, zero retries, configurable timeout default 600 | [infra/main.bicep:208](main.bicep#L208) |
| Tools Dockerfile is external work, not created by this draft | [infra/README.md:243](README.md#L243) |
| SQL logical server, Entra admin, generated password as secure input | [infra/main.bicep:52](main.bicep#L52) |
| AzureBank SQL Basic, capacity 5, max size 2 GiB | [infra/main.bicep:71](main.bicep#L71) |
| Azure-services 0.0.0.0 firewall rule; no Defender plan | [infra/main.bicep:85](main.bicep#L85) |
| App encrypted SQL connection, certificate validation required | [infra/main.bicep:112](main.bicep#L112) |
| One user-assigned deployment identity | [infra/main.bicep:94](main.bicep#L94) |
| One GitHub federation: exact issuer, demo subject and audience | [infra/main.bicep:99](main.bicep#L99) |
| Only resource-scoped app and job assignments | [infra/main.bicep:244](main.bicep#L244) |
| Exact built-in role names, IDs, rationale and verification caveat | [infra/README.md:217](README.md#L217) |
| Contained app user with sqlcmd variable password | [infra/sql-principals.sql:12](sql-principals.sql#L12) |
| Contained migrator with separate sqlcmd variable password | [infra/sql-principals.sql:15](sql-principals.sql#L15) |
| Both reader/writer, migrator additionally DDL admin, guarded membership changes | [infra/sql-principals.sql:17](sql-principals.sql#L17) |
| Idempotent user/password handling, wrong database guard and transaction | [infra/sql-principals.sql:5](sql-principals.sql#L5) |
| Example parameters contain placeholders only | [infra/main.example.bicepparam:1](main.example.bicepparam#L1) |
| Real parameter files ignored; example retained | [infra/.gitignore:2](.gitignore#L2) |
| workflow_dispatch only; choice action build-push or deploy | [.github/workflows/deploy.yml:4](../.github/workflows/deploy.yml#L4) |
| Job-level build permissions: contents read, packages write | [.github/workflows/deploy.yml:21](../.github/workflows/deploy.yml#L21) |
| Job-level deploy permissions: contents read, id-token write; demo environment | [.github/workflows/deploy.yml:68](../.github/workflows/deploy.yml#L68) |
| Build/push API and BFF, root context, SHA tags, fixed GHCR names | [.github/workflows/deploy.yml:39](../.github/workflows/deploy.yml#L39) |
| Build/push tools conditionally on checked-out Dockerfile; absent dependency disclosed | [.github/workflows/deploy.yml:49](../.github/workflows/deploy.yml#L49) |
| OIDC azure/login with repository client, tenant, subscription variables | [.github/workflows/deploy.yml:90](../.github/workflows/deploy.yml#L90) |
| Set migration image to SHA and await provisioning | [infra/deploy.py:159](deploy.py#L159) |
| Start migration once, poll that execution, fail on any non-success or timeout | [infra/deploy.py:78](deploy.py#L78) |
| Only then atomically update both app containers to SHA | [infra/deploy.py:164](deploy.py#L164) |
| Wait for exact new ready revision before smoke | [infra/deploy.py:165](deploy.py#L165) |
| Smoke GET / requires 200 and SPA HTML; GET /health/ready requires 200 | [infra/deploy.py:109](deploy.py#L109) |
| Fixed deploy concurrency group, no active-run cancellation | [.github/workflows/deploy.yml:70](../.github/workflows/deploy.yml#L70) |
| Major-version action pins match CI; checkout credential persistence off | [.github/workflows/deploy.yml:24](../.github/workflows/deploy.yml#L24) |
| README inventory and one-time owner steps in required order | [infra/README.md:35](README.md#L35) |
| Owner-only parameter files, no secrets in command arguments, deletion after bootstrap/deploy | [infra/README.md:61](README.md#L61) |
| Explicit first-boot empty-schema handling | [infra/README.md:166](README.md#L166) |
| All seven deliberately omitted features | [infra/README.md:280](README.md#L280) |
| Bicep/actionlint actual versions, exit codes and output | [infra/ACCEPTANCE.md:7](ACCEPTANCE.md#L7) |
| Full requirement-to-line table | [infra/ACCEPTANCE.md:60](ACCEPTANCE.md#L60) |
| Required unverified Azure list, with documentation basis for every item | [infra/README.md:264](README.md#L264) |
| Wrong/impossible requirements and external dependencies stated plainly | [infra/README.md:235](README.md#L235) |
| Phased plan with subphases, steps and substeps | [infra/PLAN.md:7](PLAN.md#L7) |
