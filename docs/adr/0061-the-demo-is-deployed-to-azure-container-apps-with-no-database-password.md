# ADR-0061: The demo is deployed to Azure Container Apps with no database password

**Status:** Accepted · **Date:** 2026-10-02 · **Amended:** 2026-10-03 (decision 10), 2026-10-05 and
2026-10-06 (ADR-0064) · **Decision Makers:** Vladislav Aleshaev

## Context

Before this decision the app ran from `compose.yaml` only. The subscription is a credit offer: when
the credit is used up everything in it stops, the database included. The SQL server's endpoint is
public and the app has no fixed outbound address, so the sign-in is the only gate. The repository
and the log of every workflow run are public. Runbook: [`infra/README.md`](../../infra/README.md).

## Decision

1. **One resource group in Italy North, created by one template that is run by hand, in two steps**,
   the foundation and then the app, because only the app needs the images and the database users.
2. **One replica, zero to one, with both hosts in it**, because the BFF keeps its sessions in memory
   (ADR-0057). A Deny policy refuses any other shape but one named scheduled job (ADR-0064).
3. **The database is Azure SQL Basic, 5 DTU and 2 GB, with a lock on the database** and not on the
   server, so that the users script of decision 5 can still delete its temporary firewall rule.
4. **The app and the migration job sign in to the database as two user-assigned managed identities,
   the server takes Microsoft Entra sign-ins only, and no database password exists**, because the
   server's sign-in is reachable from every Azure customer.
5. **`infra/sql-principals.sql`, run by `sqlcmd` as the server's Entra administrator, creates the
   two database users, and nothing else is run as administrator in that database.** It refuses a
   database with a trigger or a module not Microsoft's own, because such code runs as administrator.
6. **What the containers print goes to one private Log Analytics workspace, capped at 0.05 GB a day
   and kept 30 days; the public workflow prints a migration's verdict and never its text**, because
   the text can name the server or a caller's address.
7. **One workflow, started by hand from `main`, deploys a commit: the migration, then the app, then
   a check, and a failed check puts the app back.** The schema is never put back. The check ends
   with a sign-in the API must refuse: that answer needs both hosts, the schema and the database.
8. **The deployment identity can move images and start a job, and nothing else**: nine actions, no
   listing of secrets, and GitHub signs in as it only from the environment `demo`. It has no role on
   the database identities, because it could then attach the schema-changing one to the app.
9. **The eight application secrets are secrets of the app, and the PIN pepper also of the pool job**
   (ADR-0064, decisions 2, 3 and 6). The connection strings are secrets too, because that keeps the
   server's name out of the `bff` container, which faces the internet.
10. **Nothing stops the app automatically.** Three alerts on the app (requests, data out, replica
    time) send an e-mail and can notify a phone (ADR-0064, decision 16). None watches the log's
    volume, because the workspace's metric reported nothing in an hour in which 446 rows arrived.
11. **What is done when Azure refuses a step is decided before the step runs**: each switch is
    written with the refusal that triggers it, and anything else that is refused is a stop.

## Rejected

- Rejected: SQL users with stored passwords, because Microsoft's guidance prefers Entra sign-in.
- Rejected: system-assigned identities, because deleting the app would orphan its database user.
- Rejected: `FROM EXTERNAL PROVIDER` by name, because any directory member can create that name.
- Rejected: keeping no logs, because a finished job's output is not promised to be readable.
- Rejected: built-in roles for the deployment identity, because they can list every secret.
- Rejected: a rollback action in the workflow, because it would deploy a tag without its migration.

## Consequences

- No database password exists: none to generate, store, rotate, guess or leak.
- It costs the database's $0.161 a day; the log's worst case under its cap is $4.63 a month.
- Not covered: the BFF container can ask for a database token: the identity is the whole app's.
- Not covered: the log's cap is not a hard bound, and a stranger can fill a day's cap on purpose.
- Not covered: a database error's text can put an e-mail address or a handle in the log (ADR-0017).
- Not covered: whoever can act as the deployment identity can run code as either database identity.
- Not covered: no single step cuts off a stolen token: the app, identities and users are made again.
- Not covered: every refused request is logged, and a deployment names a tag, not a digest.
- Not covered: a cold start may answer one 503. Measured: EF Core's transient list takes neither a
  missing token (error 0, class 20) nor a missing database user (error 18456, class 14).

## Revisit when

- A cost appears on the log meter, or a day goes above twice the cap: the logs go off.
- The first token regularly takes longer than 10 s: the connect timeout, decided in ADR-0058.
- A second replica, or the API in an app of its own: a shared session store first (ADR-0057).

## Verified by

- Offline: `python -m unittest discover -s infra -p "test_*.py"` (scripts, templates, `deploy.py`).
- `ConnectionDefaultsTests`: a managed-identity connection string keeps its sign-in.
- On Azure: the runbook's "Measured on Azure" (a trial on 2026-10-02, the deployment on 2026-10-03).

## Related

ADR-0013, ADR-0017, ADR-0055, ADR-0057, ADR-0058, ADR-0060, ADR-0062, ADR-0064.
