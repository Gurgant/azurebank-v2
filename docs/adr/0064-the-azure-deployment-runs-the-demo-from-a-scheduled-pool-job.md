# ADR-0064: The Azure deployment runs the demo from a scheduled pool job

**Status:** Accepted · **Date:** 2026-10-05 · **Amends**
[ADR-0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md)
(decision 1: a switch adds two resources to the second step; decision 2: one job, by name, may
run on a schedule; decision 7: a deployment reads whether the demo is on, moves a second job and
asks the address two things more; decision 8: a third role assignment; decision 9: an eighth
application secret, and two secrets on a second job; decision 10: the three alerts may also
notify the owner's phone),
[ADR-0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) decision 13 (the job
that runs `recycle` on Azure is in the template),
[ADR-0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md) decision 14 (what
turns the demo on there is in the template),
[ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md) decision 5's note (the
same job) and [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md), its first
precondition (a deployment does not start beside a pool run)

**Where the code is.** `infra/main.bicep` (the switch `demo`, the pool job, its role assignment,
the two variables *(2026-10-07: one since that day, the pool job's schedule; the other, the cap
on one client's claims, is taken out, decision 2's note of that date)*, and the phone's
receiver in the action group), `infra/guardrails.bicep` (the
exception by name), `infra/app-inputs.bicep` (the client key's length), `infra/secrets.ps1`
(`-DemoOn`, the eighth secret, and `-AlertPushAccount`),
`infra/deploy.py` (`demo_of`, `pool_drift`, `check`, `pool_run`, the two checks of the smoke test)
and `.github/workflows/deploy.yml` (its time limit). The runbook is `infra/README.md`: its third
session, "Turn the demo on", is steps 22 to 33, and its section "Turning the demo back" holds the
ways back. No code cites this record by number.

**Nothing of this has run on Azure.** This record and the files it describes were written with no
Azure access: the templates compile, the scripts and `deploy.py` are tested offline against
stand-ins and invented answers, and one thing was measured on a local stack. Every sentence
below about what Azure does is what the code and the earlier records lead to expect. The
runbook's third session is where each is to be seen, and a later change records what it
measured. *(2026-10-07: one thing of it has been measured on Azure since, what the BFF sees as
a visitor's address behind the ingress, at the runbook's step 30 on 2026-10-06 and on
2026-10-07: decision 2's note of that date. This record holds no other measurement on Azure.)*

## Context

**Two records left this change its work.** ADR-0062 built the pool: `recycle` tops it up and
deletes the copies whose time is over, as a job of the tools image. Its decision 13 said that on
the Azure deployment no job runs it yet, and named what adding one touches: the Deny policy,
which refuses a job that is not manual; the PIN pepper, which becomes a secret of the job too;
the read-backs that expect each database identity on one resource; and the deployment identity's
role, held on the app and the migrate job and nowhere else. ADR-0063 built the claim, the sign-in
gate and closed registration, behind `Demo:Enabled`. Its decision 14 said that on Azure the demo
is still off, that the change which adds the pool's job is the one that turns it on, and what
that change has to set.

**The deployment as it stands** (ADR-0061; last read on 2026-10-03): one app with the BFF and the
API, one manual job that migrates, two database identities, a policy that refuses any other
shape, a database with a schema and no row, and registration open. Nothing under `infra/` set a
value of the demo.

**Three things make the order matter.**

- **A flag on the job alone is the harmful state.** With the flag on the job and on neither
  container of the app, visitors register beside the pool and every `recycle` exits 13
  (ADR-0063, Consequences). No file removes a user, so that state has one way out: a new
  database.
- **The first run of the job is what makes a registration possible.** On a database that only
  `migrate` has touched there is no role, and a registration cannot commit: measured on a local
  stack on 2026-10-05, it answered 500 and left no row (Validation). The pool's first run creates
  the two roles. From then on an app whose flags are off registers whoever asks.
- **The images on Azure may not read the flag.** The last tag recorded on the app is
  `8552f935`, and at that commit neither host binds the demo's settings
  (`git grep -n DemoOptions 8552f935 -- backend/src/AzureBank.Api backend/src/AzureBank.Bff`
  prints nothing). A flag set on those images is read by nobody.

## Decision

**1. The template has one switch, `demo`, off by default, and the app is what remembers it.**
With it off the template creates the resources it created before and no other; what its next
run changes on them all the same, the policy's exception among it, is under Consequences.
`infra/secrets.ps1` writes the switch the way it writes what the environment does with its
logs: `true` with `-DemoOn`, else what the
deployed app's two containers say, else nothing, and the template's default applies. Two
containers that disagree stop the script, and so does a container that carries the setting any
other way than the template writes it. So no later run turns the demo off by forgetting an
override, and none turns it on by accident. **With no app deployed nothing remembers the
switch**: the script's report says so in one line, and the runbook says when `-DemoOn` must be
passed again.

**2. With the switch on, both containers are told, from one place.** `Demo__Enabled` is on the
`bff` and on the `api` container, written as `true` or `false` from the switch. The app holds a
ninth secret, the demo's client key, which the `api` container alone is handed, whether the demo
is on or off; the check of the app's values asks 32 characters of it, because the API refuses to
start with the demo on and a shorter one. ~~`Demo__Claim__MaxPerClientPerDay` is 1,000 on the
`api` container and on the job, from one variable, as ADR-0063's decision 14 asks until the
address the BFF sees behind the ingress has been measured. Every other number of the demo stays
at its default on all three, so no two can differ.~~ *(struck 2026-10-07: the template writes
no number of the demo any more. The variable is gone, every number is at the application's
default on all three, 10 for the cap on one client's claims, so no two can differ, and a test
holds that no container of the template carries a setting under `Demo__Claim__`. What
ADR-0063's decision 14 waited for was measured at the runbook's step 30. On 2026-10-06, before
any network was named, twelve sign-ins sent from one connection in under eight seconds were all
answered, where the limit is ten a minute for one caller, and the limiter's warnings for nine
later refusals named two internal addresses, neither of them the caller's. On 2026-10-07 one
run of the template named the ingress's network on the `bff` container, and the proof passed:
from one connection, twelve sign-ins in under four seconds were answered ten times and refused
at the eleventh and at the twelfth, four times over, two of the four with an `X-Forwarded-For`
header that named another address in each request; the limiter's 52 warnings all named that
caller's own public address, none an address of the ingress and none an address a header had
named. Not run: the run of the template that takes the setting out on Azure. Until it is made
the deployed `api` container and the deployed job are expected to carry the 1,000: on
2026-10-07 that container read back 13 settings, the template's count with the cap
(`infra/README.md`, step 30).)* No container carries a key id of the pepper, a
previous pepper ~~or a forwarded-headers setting~~ *(struck 2026-10-06, ADR-0013's note of that
day: a run that names networks of proxies writes one setting for each on the `bff` container,
`ForwardedHeaders__KnownIPNetworks__0` and on, so that the BFF counts a visitor by the visitor's
own address behind the ingress. The parameter, `proxyNetworks`, is empty by default: a run that
names none tells the `bff` the six settings it was told, which a test compares worked out, and
no run has named any)*.

**3. One job, `azurebank-pool`, runs `recycle` on a schedule as the app's database identity.**
The tools image, one container, the argument `recycle`, the identity `azurebank-app`. No second
job for a first fill: `recycle` on a database with no row builds the pool and exits 0
(ADR-0062). No third database user, and nothing run as administrator. The job is built only when
both the app and the switch are, and it waits for the app in the template.

- **Every four hours**, `0 */4 * * *`, a variable of the template and not a parameter: an
  override could make the interval shorter than the job's timeout, and two runs at once can
  build up to twice the pool's target. Four hours bounds how long a pool stays empty.
- **A timeout of its own**, 600 s, from 60 to 840, and a test holds the interval longer than the
  longest of them. **No retry**: with one, Azure is expected to take a run that ended with a
  signal, 10 to 15, for a failed one and to run it again, and 13 would loop. **One run at a
  time.**
- **Two secrets of its own, worked out by the template**: the app's connection string and the
  app's PIN pepper, from the expressions the app's own secrets are written from, so that a run
  of the template leaves them equal. Never the client key. `secrets.ps1` asks nothing of a job.
- **Its own flag is the plain word `true`**: the template writes the job only with the demo on,
  and the pool's commands refuse to run with it off.

**4. The policy lets a job run on a schedule only by its name.** A second parameter,
`scheduledJobs`, and one exception in the rule on a job's trigger: `Schedule` is allowed to a job
whose name is in that list. `main.bicep` hands over `azurebank-pool`. `allowedJobTriggers` stays
`Manual` for every job, so the migrate job, which signs in as the identity that changes the
schema, stays manual by policy and not only by `deploy.py`'s check. The policy is renamed
"AzureBank: one small replica, manual jobs, the pool job scheduled", and the removal command
finds it by its first words. **If Azure does not take a rule on a job's name, that is a stop**:
`allowedJobTriggers` with `Schedule` in it is not used to get round it, because it widens the
rule for every job and no file remembers it.

**5. A third assignment of the same role, on the pool job.** The deployment identity must read
that job and move its image with the commit. No new action and no fourth identity. The start
right it brings reaches nothing new: the job's two secrets and its database identity are already
within reach of whoever may write the app (`infra/README.md`, "What each identity can do").

**6. The eighth secret is generated for a deployed app only while that app's demo is off.** The
script never generates a value a deployed app should hold, and that rule stands for the seven.
The app the first deployment created has no client key and never needed one, so for that one
name a value may be generated against a deployed app, and only while its demo is off. An app
whose demo is on and which lacks the key stops the script, as a missing one of the seven does.

**7. `deploy.py` reads from the app whether the demo is on, and asks for the pool job only
then.** Both containers carrying the flag as `true` is on; neither is off; anything else stops
the run and names the containers, never a value. With the demo off the pool job is neither read
nor moved, and whether it exists is not asked. With it on the job must be readable and in shape,
or the run stops before any change. **No answer of Azure's is ever read as "there is no pool
job"**: the deployment identity's role is on each resource by itself, and what Azure answers it
for a job that is not there has not been seen. This breaks a circle: the script cannot require a
job that must not exist before the app runs images that read the flag.

**8. The shape the script holds the pool job to includes its schedule.** The trigger, the
expression the template writes, one run at a time, no retry, a timeout inside the template's
bounds, no init container, one container named `pool` whose arguments are `recycle` and which
has no command, and the app's identity and no other. It is checked before any change and again
after the job's image moved. The policy has no rule on a schedule's expression or on a timeout,
and the deployment identity may write the whole job: this check, at a deployment and at a
`--check`, is the only read of either on a deployed job.

**9. Nothing is started beside a pool run.** A deployment reads the pool job's executions once,
before any change, and stops while one is running or may be: the migration is about to change
the schema that run works on, and the job itself is about to be moved. A run that the read
missed stays inside the database's 30 logins all the same: 12 + 5 + 5 beside the migration, and
2 × 12 + 5 = 29 while a revision is replaced, because the migration has ended by then
(ADR-0058, the note of this date). A start by hand refuses the same way, and reads the pool job
alone: it does not look for a migration in progress, so the runbook keeps it away from a
deployment. A run the schedule starts after that one read is not seen, and nothing holds the
schedule back.

**10. A deployment asks the address two things more with the demo on, and claims nothing.** The
smoke test keeps its refused sign-in. With the demo on, the page must carry the demo's tag, and
with it off it must not; and one registration with the body `{}` must be answered 403
`REGISTRATION_CLOSED`. With the demo off no registration is ever sent. Neither check spends a
copy. Both are answered by the BFF alone: what they do not prove is said in decision 11.

**11. Three commands for the owner's terminal, refused inside GitHub Actions.**

- **`--check` moves nothing.** A run of the template is expected to make a revision outside
  `deploy.py`, with no migration, no smoke test and no put-back: this is its read-back. It
  waits until the app's latest revision is its latest ready one and no other revision is
  active, since "ready" is not "the one before has stopped answering". With the demo on it
  checks the pool job's shape, then
  lists the secrets of the app and of the job as whoever is signed in and compares the job's PIN
  pepper and connection string with the app's. It says they are the app's, or which one
  differs, and shows no value, no part of one and no length. Then the smoke test. It is the one
  mode that lists a secret; a workflow run goes on proving that its identity is refused that
  listing.
- **`--pool-run` starts the job once and waits for that run**: the first fill, or a refill by
  hand. The start is never sent twice.
- **`--pool-log` prints what a run of the pool job printed**, as `--job-log` does for a
  migration.

**Why the comparison, and what still needs a browser.** A wrong pepper is silent: a PIN that is
not taken is answered 200 with `verified` false, so no status, alert or exit code shows it; and
whoever may write the app can overwrite the app's secret while the job keeps its copy. The
comparison costs nothing and can follow every step. That a visitor is handed a copy, and that a
PIN is taken end to end, is proved from a browser, once in the session and after any change of
the job's secrets.

**12. A pool run by hand ends by its exit code, never by its status alone.** 0, 10, 11 and 15
end the command well, the last three with a signal: the run finished and found the pool short.
1, 2, 12, 13 and 14 fail it, and so does a code Azure did not report. A read of the code that
Azure refuses or fails ends the command as failed too, in a sentence of its own with Azure's
words: it is not said to be a run with no code. How Azure words an
execution whose container exits 10 has not been seen, and an older execution was once read
without its code (`infra/README.md`, "Measured on Azure", step 16).

**13. No alert is built here**: none on a failed pool run, none on the database's size. The one
rule this deployment built on a metric nobody had seen report was created and then deleted
(ADR-0061, decision 10). Until those two metrics have been read, a pool run is read on demand:
its exit code, its line, and what a visitor is answered.

**14. The job's connect timeout stays the tool's 10 s.** A run that could not open the database
exits 1, which is safe to run again. Two such exits in a row are the owner's decision on a
longer one, as a change of the template.

**15. No switch turns the demo off, and after the pool job's first execution there are two ways
back.** While the job has no execution, which is read and not assumed, the switch may go off by
an override, the job deleted first. After any execution: stop the app, or recreate the database,
the job deleted first, then one run with the switch off on the empty database. **"The job
deleted and the app left up" is not a third way.** In that state decision 7 refuses every
workflow deployment, a security fix included; the next run of the template, for any reason,
creates the job again, since the script keeps the switch; up to 50 free copies are still handed
out; and nothing sweeps. The job is deleted only as the first step of a road that ends with the
demo off.

**16. The alerts may also reach the owner's phone, through one optional receiver of the Azure
mobile app.** Decided on 2026-10-06, a day after the fifteen above. The three alert rules wrote
to one action group with one mailbox, and the owner asked for the same warnings on his phone.

- **One parameter of the template, `alertPushAccount`, empty by default**: the e-mail address
  the Azure mobile app on the phone was set up with. Given, the action group gains one receiver
  of that app with that address, under a name of its own, `owner-phone`, since a receiver's name
  must be unique in its group. Empty, the group is what it was: worked out offline, a run
  predicts the same resources with the same properties. The address comes from the parameter
  and is written in no file of the repository.
- **`infra/secrets.ps1` finds the account as it finds the alerts' mailbox**, so that no later
  run forgets it: `-AlertPushAccount`, else the variable `AZUREBANK_ALERT_PUSH_ACCOUNT`, else
  the one such receiver the deployed group holds. None of the three is no account, which is not
  an error. **The signed-in account is never taken for it**: whether the sign-in name the
  template knows as `entraAdminLogin` is the address the app was set up with is not known. Two
  such receivers on the deployed group stop the script, and so does the argument without the
  app.
- **The account travels in the run that turns the demo on**, the third session's one run of
  the template with the app (the runbook's step 25). Before it, by the owner's own hands: the
  app installed, signed in, and allowed to notify. After `--check` has passed, one test
  notification names both receivers, and the read is what the owner sees, on the phone and in
  the mailbox, with the delay of each written down; the answer of the request alone is not the
  read.
- **It warns and stops nothing.** The phone is a second road for the same three warnings: no
  rule is added, decision 13 stands, and so does decision 10 of ADR-0061, that nothing stops the
  app automatically.

Not known, and not claimed: what Azure does with a push for an account that has no app; what
such a notification costs on this offer; and whether the offer takes a test notification at
all, since it refused the first deployment's on 2026-10-03 (ADR-0061, "What would change
this"). If it refuses again, the phone's road is proved by the first alert that fires and not
before.

## Rejected

- **`Schedule` in `allowedJobTriggers`.** It is one list for every job of the group, so it would
  let the schema-changing job be put on a schedule by whoever may write it, and only
  `deploy.py`'s check would notice, at the next deployment.
- **A workflow action that refills the pool.** The job refills by itself every four hours, and a
  run that found the day's ceiling builds nothing. It would be one more public door that starts
  a job as the app's database identity. Worth a change of its own if the pool is found empty
  twice in a week with nobody at the terminal.
- **A claim on every deployment.** It spends a copy and leaves audit rows at each deployment, and
  a claim's answer holds a copy's password, in a script whose output is a public log.
- **A command that claims a copy and sends a PIN, for the owner.** It spends a copy and audit
  rows at each use and brings a copy's credential into the script, where the comparison of two
  secrets spends nothing and can run after every step.
- **An alert built before its metric was seen** (decision 13).
- **A switch in the script that turns the demo off.** After the first fill, off reopens
  registration on the pool's database.
- **A third way back, the job deleted with the app left up, and a switch of the template to hold
  that state.** More code, and a branch of `deploy.py`, for a state that stops nothing at once.
- **A second job for the first fill, or a start with other arguments.** `recycle` does the first
  fill.
- **A database user of the job's own.** ADR-0062 says what would give it one; nothing here
  needs it.
- **A longer connect timeout for the job now** (decision 14).
- **The signed-in account taken for the phone's**, as its mailbox is taken for the alerts' when
  nobody names one. What Azure does with a push for an address the app is not registered with
  is not known, and it may be nothing anybody sees: an account nobody named is no account
  (decision 16).
- **The phone's receiver written as a list that may be empty.** With no account it would send a
  fourth property of the action group on every run, where the group is to stay what it was.

## Consequences

- **Nothing on Azure changes with the merge, and the next run of the template changes the app
  whatever it is for.** With the switch off the template still writes the ninth secret and four
  settings on the app *(2026-10-07: three since that day, the cap on one client's claims being
  written no more: decision 2's note of that date)*, the flag as `false` on both containers
  among them: that is expected to
  make a new revision, and a new revision ends every session. The role definition's description
  changed too, so a run without the app shows a change on it. Nothing is created or deleted.
- **The same run changes the policy, with the switch off as with it on.** Its definition gets
  the new name, the new description, a second parameter and the exception in the rule; its
  assignment gets the new name and `scheduledJobs` holding `azurebank-pool`. The exception is
  not behind the switch: it is the template's default from that run on, with the demo off and
  after every way back, and an override that takes it out holds only until the next run without
  it. While no pool job exists it serves only whoever may create a job in the resource group,
  which the deployment identity may not: its role is assigned on resources that exist.
- ~~**With the demo on, the pool is the only bound on strangers.** The cap for one client is
  1,000 until the address is measured, so what is left is 50 free copies, the day's 150 claims
  and 200 changes in a copy.~~ *(struck 2026-10-07, decision 2's note of that date: the address
  is measured and the template sets no cap, so one client is given the default, 10 copies in a
  rolling 24 hours, and the pool's numbers are what bounds callers who come from many
  addresses. On the deployed app the 1,000 is expected to stand until the run of the template
  that takes it out, which has not been made; and since the run that named the ingress's
  network the limit of ten a minute counts a caller there by the caller's own address, by the
  function the 300 a minute count by too.)* And if the BFF sees one address
  for every visitor, two of its three rate
  limits are shared by everybody, the ten a minute of the sign-in doors and the 300 a minute of
  everything else: they count by one function of the caller's address. The third, on recipient
  lookups, counts by the signed-in user.
- **Between two deployments nothing notices a changed schedule or timeout** (decision 8).
- **A deployment with the demo on needs the pool job.** A job that cannot be read stops every
  workflow deployment until it is back.
- **The pool job is a third place the app's identity can be used from**, and it holds a copy of
  the pepper: "If something was stolen" in the runbook deletes it with the app, and says that
  what the new secrets mean for the rows already in the database is not designed.
- **A template run's revision is checked by hand.** Nothing runs `--check` by itself: the
  runbook's steps do.
- **A run of the template and a deployment must not overlap, and no code keeps them apart.** A
  deployment sends the app the template it read at its start. A run of the template that ends in
  between is expected to be undone on the app, the demo's flag with it, while the pool job that
  run created stays on its schedule: the harmful state of the Context, reached with a deployment
  that ends green. The runbook has the rule and the read that comes before a run of the template
  (`infra/README.md`, "Deploy a commit", "One deployment at a time"). Read in `deploy.py`; not
  provoked on Azure.
- **The offline tests read more than this folder**: eight source files of the backend and two
  runbooks, as text.
- **The phone's account rides the session's most delicate run** (decision 16). If Azure does
  not take the receiver, that run fails, and a deployment that fails is not expected to undo
  the app and the job it wrote beside it: the runbook's step reads `--check` at once and then
  runs again with no account, and the phone waits for a run of its own. Expected, not provoked.
- **The sentences of the earlier records about what Azure holds stay as they are.** "No job runs
  them yet", "the demo is still off" and the read-backs with one job are true until the session
  has run; the change that records it flips them.

## Validation

Offline, on one machine (Windows, Python 3.14, PowerShell 7.6, Bicep 0.47.16), on 2026-10-05.

- **The tests of `infra/`**: `python -B -m unittest discover -s infra -p "test_*.py"` ran 444
  tests, with no failure and no error; 2 were skipped, the two that hold only off Windows. They
  run `secrets.ps1` for real against a stand-in for the Azure CLI, read the compiled templates,
  work them out with `bicep snapshot`, and run `deploy.py` against invented answers. Among what
  they hold: the switch off predicts the 22 resources it predicted before, 23 with the alert on
  the workspace and 14 without the app, and on predicts 24 and 25; the compiled job, its two
  secrets and what its container is handed; the policy's rule, compared whole; the role's nine
  actions on three scopes; the script's schedule, timeout bounds and secret names held equal to
  the template's; the names the scripts, the template and the backend each type for the app, the
  jobs and the demo's settings, held to one another; the refusals the demo added to `deploy.py`,
  compared as whole sentences; that no line and no error of `--check` holds eight characters in
  a row of a listed secret; that a test of `deploy.py` which reaches for the network fails; and
  that the runbook has a row for each refusal that sends its reader there, and that the quotes
  of a good run which the tests list are words the scripts print: eleven of `deploy.py` at that
  run, twelve since 2026-10-06 with the line printed before a start by hand is sent, and three
  of `secrets.ps1`. A step's other quotes are held by no test of `infra/`.
- **`bicep build` and `bicep lint`** of the three templates: nothing on standard error.
- **On 2026-10-06, with decision 16**, on the same machine: the same command ran 452 tests,
  with no failure and no error; 2 were skipped, the same two. Eight are new. Seven are of
  `secrets.ps1`: the three places it finds the account in, the case where nobody names one, two
  receivers on the deployed group, an account that is not shaped like an address, and the
  argument without the app. The eighth holds three more quotes of `secrets.ps1` that the runbook
  gives, six with the three above. And the test of the action group changed: it reads the
  group's properties as a run works them out, with no account named, with an empty one and with
  one given, and holds that nothing else of the run differs. That test and the seven were seen
  failing without their code; the test of the quotes was green as written and failed with each
  line reworded on either side. One assertion of an earlier test changed its claim: with the
  mailbox named and the phone not, the deployed group is now read once, where it was not read
  at all. `bicep build` and `bicep lint` of the three templates: nothing on standard
  error. The compiled template holds the 23 resources it held and 24 parameters, one more. The
  runbook's blocks for the phone ran against a function standing in for `az`, with invented
  answers. The backend's gate was not run again: no file under `backend/` changed, and of its
  tests that read documents one reads the decision records, for a string this change does not
  write (read in their sources, not run).
- **The backend's gate**, since documents its tests read were edited: the Release build, the
  whitespace check, and `dotnet test` with and without a SQL Server, each with no failure, on
  the documents as they were first written. After the corrections of the same day: the build,
  the whitespace check and the two test projects without a SQL Server, each with no failure.
  The twelve SQL statements of the pool's runbook, which the tests that need a SQL Server run,
  are byte for byte what they were at the run with one.
- **One registration on a database that only `migrate` has touched**, on a local stack
  (`compose.yaml` under a project name of its own, the `seed` service made to run `migrate`
  again; images built from the sources of `6988514f`; SQL Server 2022 CU27). 17 migrations, 0
  users, 0 roles. `POST /bff/auth/register` with a body that passes the rules: **500**, and
  afterwards 0 users and 0 roles, no role membership, no account. The API's console:
  `Role USER does not exist.` The control: after `seed` ran on that database, the same body was
  answered 201 and the users went from 4 to 5. One request each.

**Not measured: everything on Azure.** Among it:

- that Azure Policy takes a rule on a job's name, and how soon a changed definition is enforced;
- that a job on a schedule is accepted, starts with nobody typing, and can also be started by
  hand;
- what a run of the template with these files shows in its what-if, and that one with the
  switch off, or without the app, leaves the rest alone;
- how Azure words an execution whose container exits 10, and whether its code is reported at
  once;
- what the deployment identity is answered for a pool job that is not there, and for its change
  of one that carries the app's identity;
- the seconds of a first fill on Basic, and the first write of `azurebank_app` there;
- that the owner may list the secrets of the app and of a job;
- the demo's tag and the closed registration through the ingress, and the claim and a PIN from a
  browser;
- ~~what the BFF sees as a visitor's address;~~ *(struck 2026-10-07: measured, decision 2's
  note of that date)*
- what a stopped app answers, and which property says it is stopped;
- that Azure takes the action group's second receiver, that a notification reaches the owner's
  phone, after how long, and what it costs;
- every way back.

`infra/README.md`, "Not measured yet", lists each with the step where it shows.

## What would change this

- **Azure refuses the rule on a job's name:** a stop, and the owner's decision; a dated note
  here says which way it went.
- **The address the BFF sees is measured** (the runbook's step 30): the cap of 1,000 goes back to
  its default, or stays until the BFF reads the client's address through the ingress, which is a
  change of its code. *(2026-10-07: measured. The BFF reads the client's address through the
  ingress, from the networks of ADR-0013's note of 2026-10-06, and the cap of 1,000 goes back
  to its default in the template: decision 2's note of this date.)*
- **The pool is found empty twice in a week with nobody at the terminal:** a workflow action for
  the refill.
- **The two metrics have been read** (step 31): an alert on a failed pool run and on the
  database's size, built on what they showed.
- **Two exits 1 in a row at the job's first open:** a longer connect timeout for the job.
- **The test notification is refused again, or is taken and nothing shows on the phone:** the
  alerts go on by e-mail, the phone's road stays unproved until an alert fires, and a dated note
  here says what was read (step 25).
- **A need to tell the job's writes from the app's, or to revoke the job alone:** a database user
  of its own (ADR-0062).
- **A pepper rotation on Azure:** the template carries a key id and a previous pepper for the
  `api` container and for the job, in that order (`docs/runbooks/demo-pool.md`, section 6).
- **The app's identity moves to an app of the API's own** (ADR-0061): the job then shares it with
  that app alone.

## Related

- [ADR-0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md):
  the deployment this adds to, and its notes of this date.
- [ADR-0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md): the pool, the
  commands the job runs, their exit codes and their line.
- [ADR-0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md): the claim, the
  flag on each host, and the state a flag on the job alone leads to.
- [ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md): the tools image, and
  which database user a job signs in as.
- [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md): the connections a job
  opens beside the app.
- `infra/README.md`: the third session, the ways back, and what is not measured.
- [`docs/runbooks/demo-pool.md`](../runbooks/demo-pool.md): what a run's exit code and line mean.
