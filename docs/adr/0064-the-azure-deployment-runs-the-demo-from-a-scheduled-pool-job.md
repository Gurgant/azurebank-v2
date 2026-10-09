# ADR-0064: The Azure deployment runs the demo from a scheduled pool job

**Status:** Accepted · **Date:** 2026-10-05 · **Amended:** 2026-10-06 (decision 16), 2026-10-07
(decision 2) · **Amends:** ADR-0058, ADR-0060, ADR-0061, ADR-0062, ADR-0063

## Context

ADR-0062 builds the pool of demo copies and ADR-0063 the claim behind `Demo:Enabled`; the deployment
of ADR-0061 had neither a pool job nor the flag. The order matters: with the flag on the job alone,
visitors register beside the pool, every `recycle` exits 13, and the only way out is a new database.

## Decision

1. **The template has one switch, `demo`, off by default, and the deployed app is what remembers
   it**, so that no later run of the template turns the demo off by forgetting an override.
2. **With the switch on, both containers are told from one place**: `Demo__Enabled` on both, the
   client key on the `api` alone, and no number of the demo, so that no two can differ.
3. **One job, `azurebank-pool`, runs `recycle` every four hours as the app's database identity**,
   with copies of the app's connection string and PIN pepper as its two secrets. No retry, because
   one is expected to repeat a run that ended with a signal (10 to 15).
4. **The policy lets a job run on a schedule only by its name**: `scheduledJobs` holds
   `azurebank-pool`, because `Schedule` in `allowedJobTriggers` would open it to every job.
5. **The deployment identity's role is assigned a third time, on the pool job**, because a
   deployment reads that job and moves its image.
6. **The eighth secret, the client key, is generated for a deployed app only while that app's demo
   is off**, because an app deployed before the demo never held one.
7. **`deploy.py` reads from the app whether the demo is on, and asks for the pool job only then**;
   nothing is read as "there is no pool job", because Azure's answer for a missing job is not known.
8. **The shape the script holds the pool job to includes its schedule**, because the policy has no
   rule on a schedule's expression or on a timeout.
9. **Nothing is started beside a pool run**: a deployment reads the job's executions once, before
   any change, and stops while one runs, because the migration changes the schema that run works on.
10. **With the demo on, a deployment asks the address two things more and claims nothing**: the
    page's demo tag and a registration answered 403 `REGISTRATION_CLOSED`; neither spends a copy.
11. **Three commands run from a terminal only**: `--pool-run`, `--pool-log` and `--check`, which
    moves nothing and compares the job's secrets with the app's, because a wrong pepper is silent.
12. **A pool run by hand ends by its exit code, never by its status alone**: 0, 10, 11 and 15 end
    the command well, because a signal is a run that finished; any other code, or none, fails it.
13. **No alert is built on a failed pool run or on the database's size**, because neither metric has
    been read, and the one rule built on an unread metric was deleted (ADR-0061, decision 10).
14. **The job's connect timeout stays the tool's 10 s**, because a run that could not open the
    database exits 1, which is safe to run again.
15. **No switch turns the demo off, and after the pool job's first execution there are two ways
    back**: stop the app, or recreate the database with the job deleted first. Deleting the job
    alone is not one, because decision 7 then refuses every deployment.
16. **The alerts may also reach a phone, through one optional receiver of the Azure mobile app**:
    `owner-phone`, built only when `alertPushAccount` names an account. The signed-in account is
    never taken for it, because whether the mobile app was set up with that account is not known.

## Rejected

- Rejected: a workflow action that refills the pool, because it is one more public door to that job.
- Rejected: a claim on every deployment, because it spends a copy and its answer holds a password.
- Rejected: a switch that turns the demo off, because after the first fill it reopens registration.
- Rejected: a database user of the job's own, because nothing here needs to tell its writes apart.

## Consequences

- The policy's exception is not behind the switch: it stands with the demo off too.
- With the demo on, a pool job that cannot be read stops every workflow deployment until it is back.
- Not covered: between two deployments nothing notices a changed schedule or timeout (decision 8).
- Not covered: a run the schedule starts after a deployment's one read is not seen (decision 9).
- Not covered: a run of the template and a deployment must not overlap; no code keeps them apart.
- Not covered: only a browser proves that a visitor is handed a copy and that its PIN is taken.

## Revisit when

- The pool is found empty twice in a week with nobody at the terminal: a workflow action refills it.
- Two exits 1 in a row at the job's first open: a longer connect timeout for the job.
- The job's writes must be told from the app's, or the job revoked alone: a database user for it.

## Verified by

- Offline: `python -m unittest discover -s infra -p "test_*.py"` (scripts, templates, `deploy.py`).
- On Azure: `deploy.py --check`; "Turn the demo on" and "Measured on Azure" in
  [the runbook](../../infra/README.md).

## Related

ADR-0013, ADR-0058, ADR-0060, ADR-0061, ADR-0062, ADR-0063.
