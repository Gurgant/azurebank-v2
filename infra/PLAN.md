# Azure deployment draft: execution plan

Scope: add files only inside `infra/` and `.github/workflows/deploy.yml`. Deliver a
reviewable draft on `deploy-to-azure-container-apps-draft`; push it, without a PR.
No subscription deployment, registry push, or application edits are part of this work.

## Phase 1: establish the contract

### 1.1 Repository and branch audit

1. Check working-tree cleanliness and the requested branch.
   1. Compare its base with local main and fetch remote main.
   2. Fast-forward the clean draft to current remote main, preserving the branch name.
2. Check repository instructions and line endings.
   1. Read applicable instructions before writing.
   2. Match LF in Git and CRLF in this Windows checkout for YAML and Markdown.

### 1.2 Application and platform audit

1. Read compose, both Dockerfiles, CI, and ADRs 0057/0058.
   1. Copy exact environment names and preserve the proxy cluster's hyphen.
   2. Establish loopback isolation, root build contexts, ports, and secret boundaries.
   3. Identify tools-image and migration-budget prerequisites.
2. Check Microsoft resource schemas, role definitions, REST operations, and SQL limits.
   1. Choose stable API versions with local Bicep type support.
   2. Distinguish documented behavior from behavior measured on a subscription.
3. Record contradictions before implementation.
   1. Single revision mode permits temporary old/new overlap.
   2. Initial empty-schema provisioning may require starting migration while creation waits.

## Phase 2: implement the draft

### 2.1 Infrastructure and SQL

1. Define secure parameters with no secret defaults.
   1. Separate SQL admin, runtime user, migrator user, and application key material.
   2. Construct encrypted SQL connections without secret outputs.
2. Declare environment, Basic SQL database, deployment identity, and one federation.
   1. Use Consumption only and explicitly disable log destinations.
   2. Limit identity assignments to the conditional app and migration job.
3. Add the app and manual job behind `deployApp`.
   1. Place BFF and API together; expose only BFF ingress.
   2. Apply BFF probes, resource sizes, and one steady replica.
   3. Bound migrations to one replica, no retry, and a five-connection pool.
4. Write transactional, repeatable contained-user creation and guarded role membership.
5. Add a placeholder parameter file and local ignore rules.

### 2.2 Delivery workflow

1. Offer only explicit build-push and deploy dispatch actions.
   1. Follow CI's major-version action pins and disabled checkout credentials.
   2. Separate package-writing permissions from environment-bound OIDC permissions.
2. Build root-context API/BFF images; build tools only if its Dockerfile exists.
3. Deploy through exact app/job resource endpoints.
   1. Preflight public access to all images for the selected SHA.
   2. Patch the job, await provisioning, reject outstanding migration executions.
   3. Start once and await that execution's success within a fixed time budget.
   4. Patch both app containers together and await the specific new revision.
   5. Require HTTPS SPA HTML and readiness status 200 without accepting redirects.
4. Serialize deployment runs and preserve failures for operator review.

## Phase 3: verify and audit

### 3.1 Local checks

1. Compile Bicep with no errors or warnings; record actual output and version.
2. Run actionlint and record actual output and version.
3. Exercise deployment failure gates with offline tests.
   1. Reject failed, stopped, unknown, and timed-out migration results.
   2. Reject an overlapping job or ambiguous start response.
   3. Ensure failed migration cannot update the app, and an old ready revision cannot pass.
4. Inspect compiled resources for conditions, secret references, scopes, and omitted services.
5. Check the final diff for whitespace, secrets, attribution, and out-of-scope files.

### 3.2 Operator verification plan

1. Explain bootstrap and secret-file handling, including cleanup and principal-password reuse.
2. Map every brief requirement to file and line in `ACCEPTANCE.md`.
3. List all unmeasured Azure checks with primary documentation and expected observations.
4. Explain missing tools, bootstrap, rollout, sessions, SQL permissions, and public-image limits.

## Phase 4: deliver

1. Stage only the new deployment files and inspect the staged diff.
2. Commit with an English subject under 72 characters and a short body, with no attribution.
3. Push the requested branch and verify the remote tip; do not open a PR.
4. Report checks, remaining subscription work, and a link to the branch and evidence.
