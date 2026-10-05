"""Deploy one commit to the running AzureBank app, or move the app alone by hand.

    python infra/deploy.py                    migrate the database, then move the app, then check it
    python infra/deploy.py --app-only         move the app only: the owner's road back
    python infra/deploy.py --check            read the running app and check it: nothing is moved
    python infra/deploy.py --pool-run         start the pool job once, by hand, and wait for that run
    python infra/deploy.py --pool-log [NAME]  print what one run of the pool job printed
    python infra/deploy.py --job-log [NAME]   print what one migration printed (the latest, or NAME)
    python infra/deploy.py --app-log MINUTES  print what the app printed in the last MINUTES

The last six are for the owner's terminal and are refused inside GitHub Actions.

It needs the Azure CLI signed in and on PATH, and the environment variables AZURE_SUBSCRIPTION_ID
and AZURE_RESOURCE_GROUP. A deployment also needs IMAGE_TAG (the full SHA of a commit whose three
images are published).

A full run, in order:
  1. read the app, and from its containers whether it is the public demo (Demo__Enabled: on in
     one and off in the other stops the run); read the migrate job, and the pool job only if the
     demo is on; print what runs now, and refuse to go on if a shape drifted or a run of the
     pool job may still be in progress;
  2. move the tools image on the migrate job, run the migration, wait for that exact execution;
  3. with the demo on, move the tools image on the pool job and read its shape again;
  4. move both app images in one request, wait for the new revision to be ready, and read the
     app's shape again;
  5. wait until the revision that ran before has stopped answering, then smoke-test the address.
If the new revision never gets ready, the app's shape has drifted when it is read again, or the
smoke test gets a wrong answer, the script tries to put the app back on the template it had at
step 1, and the run still fails. If that put-back fails too, the run says so: the app may then be
serving a broken revision. The schema is never put back.

An interrupt stops the script where it is, in any mode: Ctrl+C on a terminal, and a workflow
run that is cancelled is expected to arrive the same way. Nothing is put back, nothing that was
started is stopped, and the last sentence says so and names the commands that read what was
moved or started.

The smoke test asks the address for the page, for the readiness answer, and for one sign-in
with an address nobody can register, which the API must refuse after it asked the database. The
page must say what the app's containers say: it carries the demo's tag with the demo on, and
does not with the demo off. With the demo on there is a fourth request, one registration with an
empty body, which the BFF must refuse as closed; with the demo off no registration is ever sent.
The tag and the closed registration are both the BFF's alone, and neither spends a copy of the
pool: no deployment claims a copy, and none proves that a PIN is taken.

--check moves nothing. It is the read-back of a run of the template, which is expected to make
a revision outside this script. It reads the app until its last update has succeeded with its
latest revision as its latest ready one, and the app's revisions until no other one is active,
so that what answers is that revision; it reads from the app whether the demo is on, and checks
the app's shape; with the demo on it reads the pool job and checks its shape; then it runs the
same smoke test, and a wrong answer puts nothing back.

With the demo on --check also lists the secrets of the app and of the pool job, as whoever is
signed in, and compares the job's PIN pepper and connection string with the app's. It says that
they are the app's, or which one differs, and never shows a value, a part of one or its length.
It is the one mode that lists a secret. A deployment never does: a workflow run goes on proving
that its own identity is refused the listing of the app's secrets.

For a deployment and for --check the pool job is the app's to announce. With the demo off they
neither read nor move it, and whether it exists is not asked: no answer of Azure's is read as
"there is no pool job". With the demo on it must be readable and in shape, its schedule and its
timeout included, or the run stops before any change. A deployment moves that job's image and
never starts it: the template gives it a schedule. The two commands that are about that job
alone, --pool-run and --pool-log, ask the app nothing: the first reads the job and the second
the job's executions, whatever the app says.

--pool-run starts that job once beside its schedule, as whoever is signed in: the first fill, or
a refill by hand. It reads the job and checks its shape, reads its executions once and refuses
while one may still be in progress, sends the start once and never again, and waits for that
exact execution for the job's timeout and two minutes. How the run ended is told by the exit
code of its container, never by the execution's status alone: 0 ends well, and so do 10, 11 and
15, which say the run finished and found the pool short; any other code, a code Azure did not
report, and a read of the code that Azure refused or failed, which is said as that and in
Azure's words, end the command as failed. It reads nothing of the app: whether the app is the
demo is --check's to say, before a run is started by hand. What the run printed, its one
summary line among it, is not fetched here: --pool-log reads it afterwards.

A migration leaves one line here, its verdict: the execution's name, status, times, exit code and
a one-word reason. What it printed is never fetched by a deployment: the log of a public
repository is public, and that text can name the server, an address, or a value from a database
error. It is kept in the log workspace, where --job-log reads it as the owner, the lines of one
execution at a time. --pool-log reads a run of the pool job the same way: its verdict, worded
with that job's own exit codes, then its lines.

In a deployment every Azure call is `az rest` on the app or on a job. The deployment identity can
reach nothing else, so it could not follow the status URL of a long-running operation, and this
script never asks it to.
"""

import argparse
import copy
import datetime
import hmac
import http.client
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

API_VERSION = '2025-01-01'
# The one read that needs a later version: from this one on an execution carries its container's
# exit code and a reason (seen filled once, by hand: README.md, "Measured on Azure"). Every other
# call stays on API_VERSION.
VERDICT_API_VERSION = '2026-07-01'
WORKSPACE_API_VERSION = '2023-09-01'
# The resolved path, so the same script also runs where az is a .cmd file.
AZ = shutil.which('az') or 'az'
AZ_TIMEOUT = 180
APP = 'azurebank'
MIGRATE_JOB = 'azurebank-migrate'
POOL_JOB = 'azurebank-pool'
# What each signs in to the database as: the one user-assigned identity it may carry. The pool
# job signs in as the app does.
APP_IDENTITY = 'azurebank-app'
MIGRATE_IDENTITY = 'azurebank-migrate'
WORKSPACE = 'azurebank-logs'
LOG_QUERY = 'https://api.loganalytics.io'
MAX_LOG_LINES = 5000
# Job name -> container name. Every job runs the tools image and moves with the commit: the
# migrate job before the migration, the pool job only after it succeeded. infra/main.bicep writes
# the pool job only with the demo on, so it is read and moved only when the app says it is the
# demo (demo_of).
JOBS = {MIGRATE_JOB: 'migrate', POOL_JOB: 'pool'}
MAX_JOB_TIMEOUT = 840
# The one schedule infra/main.bicep writes (its variable poolSchedule) and the bounds it gives
# the pool job's timeout (its parameter poolTimeout). A test holds each equal to the template's
# (infra/test_scripts.py). Nothing else reads the schedule or the timeout of a job that is
# deployed: the Deny policy has no rule on either.
POOL_SCHEDULE = '0 */4 * * *'
POOL_TIMEOUTS = (60, 840)
# The setting both containers of the app read to know whether they are the public demo
# (infra/main.bicep writes it on both from one switch, `demo`).
DEMO_FLAG = 'Demo__Enabled'
# What the public demo shows of itself without spending a copy, both under
# backend/src/AzureBank.Bff: the tag the BFF puts in the page's head
# (Extensions/SpaHostingExtensions.cs, DemoTag), and its own door for a registration, which the
# demo closes (Middleware/DemoModeMiddleware.cs): 403 with this error code, answered by the BFF
# before the request reaches the API, whatever the body holds.
DEMO_TAG = '<meta name="azurebank-demo" content="true">'
REGISTER_PATH = '/bff/auth/register'
REGISTRATION_CLOSED = 'REGISTRATION_CLOSED'
# The shape of an error code of the app's (backend/src/AzureBank.Shared/Constants/ErrorCodes.cs):
# capitals, digits and underscores, forty characters at most. Of an answer to a registration a
# line shows the status and an error code of this shape, and nothing else.
ERROR_CODE = re.compile(r'[A-Z][A-Z0-9_]{0,39}')
ACTIVE = {'Running', 'Processing'}
FAILED = {'Failed', 'Stopped', 'Degraded'}
FINISHED = FAILED | {'Succeeded'}
STATUSES = ACTIVE | FINISHED | {'Unknown'}
GUID = re.compile(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
IPV4 = re.compile(r'\b\d{1,3}(?:\.\d{1,3}){3}\b')
# The shape of an execution's name. A name of any other shape is never printed.
EXECUTION_NAME = re.compile(r'[A-Za-z0-9-]{1,100}')
# What `migrate` exits with: the one command a deployment runs, and the container verdict() reads
# unless it is told another (backend/tools/AzureBank.Seeder/Commands/ExitCodes.cs). For `migrate`
# a 2 always comes before any connection. The image's other commands differ: `seed` and `reset`
# can also refuse after one count, and `seed-pool` and `recycle` can also end with a code from 10
# to 15 (Pool/PoolExitCodes.cs; `seed-pool` only 12 or 13), so the pool job has a map of its own,
# below.
EXIT_CODES = {0: 'done', 1: 'failed after it reached for the server; running it again is safe',
              2: 'refused before any connection; the configuration must change'}
# What a line says of a code that the map it is read with does not hold.
NOT_THE_TOOLS_CODE = 'not a code the tool itself exits with'
# What `recycle` exits with, the one command the pool job runs
# (backend/tools/AzureBank.Seeder/Pool/PoolExitCodes.cs; docs/runbooks/demo-pool.md, "The exit
# code"). A code from 10 to 15 is a signal: the run finished, and the code names the count somebody
# should read. A deployment never starts the pool job and reads no exit code of it: --pool-run
# does, for the one run it started, and --pool-log for the run it is asked about.
POOL_EXIT_CODES = {
    0: 'done',
    1: 'did not finish, and left no summary line; read its last line before it is started again',
    2: 'refused before anything was opened; the job\'s configuration must change',
    10: 'done, with a signal: the pool was low (PoolLow)',
    11: 'done, with a signal: the pool was empty (PoolEmpty)',
    12: 'needs a look: a copy could not be built (TopUpIncomplete)',
    13: 'needs a look: a user outside every copy exists (ForeignUsers)',
    14: 'needs a look: a copy could not be deleted (DeleteFailed)',
    15: 'done, with a signal: the day\'s claims held the top-up back (ClaimCeiling)',
}
# The codes a pool run ends well with: it finished, and at most the pool was short.
POOL_RUN_ENDS_WELL = {0, 10, 11, 15}
# The count of the run's one summary line that each signal says to read: the one its code is
# worked out from (Pool/PoolExitCodes.cs, From; the line is Pool/PoolRunSummary.cs, ToLine, and
# docs/runbooks/demo-pool.md, "The line", says what each count counts). 12 is also a copy that
# failed, which the line does not count: `free` is the half of it the line shows.
POOL_COUNTS = {10: 'was', 11: 'was', 12: 'free', 13: 'foreignUsers', 14: 'failed', 15: 'ceiling'}

# The sign-in the smoke test sends. It goes to the BFF's own door: the BFF answers 404 itself on
# the proxied /api/auth/login and never forwards it. The address is one nobody can register, and
# the password passes the BFF's own rule, so the request reaches the API, which looks the address
# up in the database, finds nothing, writes nothing and refuses.
SMOKE_PATH = '/bff/auth/login'
SMOKE_LOGIN = {'email': 'deploy-smoke@azurebank.invalid', 'password': 'Not-a-real-account-0'}
SIGN_IN_TRIES = 4
# Sign-ins share one bucket of 10 a minute with every visitor (Bff/appsettings.json,
# AuthPermitLimit): after a 429 only a full window can free a permit.
WAIT_AFTER_429 = 65
WAIT_BETWEEN_TRIES = 20
# What "no answer" is, for the smoke test: a connection refused, reset, closed before the answer
# or timed out, a name that does not resolve, a TLS failure (all OSError, urllib's own URLError
# included); an answer that is not HTTP or stops short (HTTPException); bytes that are not text.
NO_ANSWER = (OSError, http.client.HTTPException, UnicodeError)


class AzError(RuntimeError):
    """The Azure CLI refused or failed; the message is its own, without identifiers."""


class ShapeError(RuntimeError):
    """The app or a job is not in the shape this script deploys onto."""


class IdentityRightAsked(RuntimeError):
    """Azure wants a right on an attached identity before it changes the resource: the run stops."""


class RevisionFailed(RuntimeError):
    """The new revision ended Failed or never became ready: the app is put back."""


class SmokeFailed(RuntimeError):
    """The page, the readiness answer, the sign-in answer or, on the demo, the answer to a
    registration was wrong: after a deployment the app is put back."""


class SmokeUnproven(RuntimeError):
    """The sign-in, or on the demo the registration, was only rate limited or unanswered: nothing
    was proved, nothing is put back."""


def redact(text):
    """What Azure wrote, as a log that anybody can read may show it: an ID becomes <id>, and what
    is shaped like an IPv4 address becomes <address>. The name of a host is not looked for: no
    answer printed here is expected to hold the database server's."""
    return IPV4.sub('<address>', GUID.sub('<id>', text or '')).strip()[:2000]


def say(message):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%H:%M:%SZ')
    print(f'{stamp} {message}', flush=True)


def short(resource_id):
    """The part of a resource ID that names the resource: no subscription, no resource group."""
    tail = resource_id.split('/providers/', 1)[-1]
    return tail[len('Microsoft.App'):] if tail.startswith('Microsoft.App/') else f'/{tail}'


def run_az(arguments, what):
    """Run the Azure CLI. A timeout is reported by `what`, never by the command line: the URL on
    it holds the subscription, and Python's own message for a timeout prints all of it."""
    try:
        return subprocess.run([AZ, *arguments], capture_output=True, text=True, timeout=AZ_TIMEOUT)
    except subprocess.TimeoutExpired:
        raise AzError(f'The Azure CLI gave no answer in {AZ_TIMEOUT} s ({what}).') from None


def az(*args, what=None):
    result = run_az([*args, '--only-show-errors', '--output', 'json'], what or f'az {args[0]}')
    if result.returncode != 0:
        # The requests carry no secret (a template, never a configuration), and the answer to a
        # refusal names the action and the scope: the one thing a failed run must show.
        raise AzError(redact(result.stderr) or f'az exited {result.returncode} with no message.')
    return json.loads(result.stdout) if result.stdout.strip() else {}


def rest(method, resource_id, body=None, api_version=API_VERSION):
    url = f'https://management.azure.com{resource_id}?api-version={api_version}'
    args = ['rest', '--method', method, '--url', url]
    what = f'{method} {short(resource_id)}'
    if body is None:
        return az(*args, what=what)
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, 'request.json')
        with open(path, 'w', encoding='utf-8') as request:
            json.dump(body, request)
        return az(*args, '--body', f'@{path}', what=what)


def images(resource):
    return {c['name']: c['image'] for c in resource['properties']['template']['containers']}


def image_patch(resource, desired):
    template = copy.deepcopy(resource['properties']['template'])
    containers = template['containers']
    if len(containers) != len(desired) or set(images(resource)) != set(desired):
        raise RuntimeError('Unexpected container topology; review before deploying.')
    for container in containers:
        container['image'] = desired[container['name']]
    template.pop('revisionSuffix', None)
    # The location and the template, and nothing else: no configuration, and never an identity.
    return {'location': resource['location'], 'properties': {'template': template}}


def patch(resource_id, body, what):
    """PATCH a template. Azure may refuse it for a right on the identity attached to the resource,
    though no body here names an identity. That refusal is not tried again and no role is added
    for it: the run stops and the refusal goes to the owner as Azure wrote it."""
    try:
        return rest('PATCH', resource_id, body)
    except AzError as error:
        refusal = str(error).lower()
        if 'linkedauthorizationfailed' in refusal and 'userassignedidentities/assign/action' in refusal:
            raise IdentityRightAsked(
                f'Azure asked for a right on a database identity before it would change {what}: '
                'stop here. This request changed nothing, it was not tried again, and no role is '
                f'to be added for it; the refusal goes to the owner as Azure wrote it: {error}') from None
        raise


# --- The shape this script deploys onto ---
# It catches drift: a click in the portal, a half-applied change. It does not stop whoever can
# edit this script; the Deny policy on the resource group does (infra/guardrails.bicep).
# A value Azure leaves out reads as its default where the default is the value wanted.

def app_drift(app):
    properties = app.get('properties') or {}
    configuration = properties.get('configuration') or {}
    ingress = configuration.get('ingress') or {}
    template = properties.get('template') or {}
    scale = template.get('scale') or {}
    names = sorted(str(c.get('name')) for c in template.get('containers') or [])
    mode = configuration.get('activeRevisionsMode')
    checks = [
        ('template.scale.minReplicas', scale.get('minReplicas'), scale.get('minReplicas') in (None, 0)),
        ('template.scale.maxReplicas', scale.get('maxReplicas'), scale.get('maxReplicas') == 1),
        ('configuration.activeRevisionsMode', mode, str(mode).lower() == 'single'),
        ('configuration.ingress.external', ingress.get('external'), ingress.get('external') is True),
        ('configuration.ingress.targetPort', ingress.get('targetPort'), ingress.get('targetPort') == 8080),
        ('configuration.ingress.allowInsecure', ingress.get('allowInsecure'),
         ingress.get('allowInsecure') in (None, False)),
        ('configuration.ingress.additionalPortMappings', ingress.get('additionalPortMappings'),
         not ingress.get('additionalPortMappings')),
        ('template.containers', names, names == ['api', 'bff']),
        ('template.initContainers', init_names(template), not template.get('initContainers')),
    ]
    return ([f'{field} is {value!r}' for field, value, wanted in checks if not wanted]
            + identity_drift(app, APP_IDENTITY))


def identity_drift(resource, name):
    """The one user-assigned identity the template attaches, and no other. The field and the
    ending it should have are named; what was found is not: an identity's ID holds the
    subscription, and the entry under it holds the client and principal IDs."""
    identity = resource.get('identity') or {}
    attached = identity.get('userAssignedIdentities')
    wanted = (str(identity.get('type')).lower() == 'userassigned'
              and isinstance(attached, dict) and len(attached) == 1
              and str(next(iter(attached))).lower().endswith(f'/userassignedidentities/{name}'))
    return [] if wanted else [f'identity is not exactly one user-assigned identity ending in /{name}']


def init_names(template):
    """Names only: a container's definition may hold a plain value nobody should print."""
    return [str(c.get('name')) if isinstance(c, dict) else '?'
            for c in template.get('initContainers') or []]


def job_drift(job):
    properties = job.get('properties') or {}
    configuration = properties.get('configuration') or {}
    template = properties.get('template') or {}
    manual = configuration.get('manualTriggerConfig') or {}
    trigger = configuration.get('triggerType')
    checks = [
        ('configuration.triggerType', trigger, str(trigger).lower() == 'manual'),
        ('configuration.manualTriggerConfig.parallelism', manual.get('parallelism'),
         manual.get('parallelism') in (None, 1)),
        ('configuration.replicaRetryLimit', configuration.get('replicaRetryLimit'),
         configuration.get('replicaRetryLimit') in (None, 0)),
        ('template.initContainers', init_names(template), not template.get('initContainers')),
    ]
    return ([f'{field} is {value!r}' for field, value, wanted in checks if not wanted]
            + identity_drift(job, MIGRATE_IDENTITY))


def pool_drift(job):
    """The pool job as the template writes it: on its one schedule, one run at a time and none
    tried again, a timeout inside the template's bounds, one container that runs `recycle`, and
    the app's database identity. The schedule and the timeout are read here because nothing else
    reads them on a job that is deployed, and whoever may write the job may change both: a
    shorter interval or a longer timeout could let two runs overlap. The schedule's expression,
    the arguments and a command are named by what they should be, never by what was found: each
    is free text, and an argument may hold a value nobody should print."""
    properties = job.get('properties') or {}
    configuration = properties.get('configuration') or {}
    template = properties.get('template') or {}
    schedule = configuration.get('scheduleTriggerConfig')
    schedule = schedule if isinstance(schedule, dict) else {}
    trigger = configuration.get('triggerType')
    timeout = configuration.get('replicaTimeout')
    shortest, longest = POOL_TIMEOUTS
    containers = listed(template, 'containers')
    names = [str(c.get('name')) if isinstance(c, dict) else '?' for c in containers]
    checks = [
        ('configuration.triggerType', trigger, str(trigger).lower() == 'schedule'),
        ('configuration.scheduleTriggerConfig.parallelism', schedule.get('parallelism'),
         schedule.get('parallelism') in (None, 1)),
        ('configuration.replicaRetryLimit', configuration.get('replicaRetryLimit'),
         configuration.get('replicaRetryLimit') in (None, 0)),
        # Its type first: a timeout that came back as text would not compare with a number.
        ('configuration.replicaTimeout', timeout, type(timeout) is int and shortest <= timeout <= longest),
        ('template.initContainers', init_names(template), not template.get('initContainers')),
        ('template.containers', names, names == [JOBS[POOL_JOB]]),
    ]
    drift = [f'{field} is {value!r}' for field, value, wanted in checks if not wanted]
    if schedule.get('cronExpression') != POOL_SCHEDULE:
        drift.append(f'configuration.scheduleTriggerConfig.cronExpression is not {POOL_SCHEDULE!r}')
    if len(containers) == 1 and isinstance(containers[0], dict):
        if containers[0].get('args') != ['recycle']:
            drift.append("template.containers[0].args is not ['recycle']")
        # A command is expected to replace the image's entry point
        # (backend/tools/AzureBank.Seeder/Dockerfile) and to leave `recycle` an argument of
        # something else.
        if containers[0].get('command'):
            drift.append('template.containers[0].command is set')
    return drift + identity_drift(job, APP_IDENTITY)


def assert_shape(what, drift, when, before='deploying'):
    """Refuse a shape that drifted. `before` ends the sentence with what whoever is refused had
    asked for: a deployment, unless the caller says otherwise."""
    if drift:
        raise ShapeError(f'{what} is not in the shape this script deploys onto ({when}): '
                         f'{"; ".join(drift)}. Put it right with the template (infra/README.md) '
                         f'before {before}.')


def demo_says(container):
    """What one container of the app says about the demo: 'true', 'false', or None for neither.
    A container that does not carry the setting says 'false': off is the default in the code
    (backend/src/AzureBank.Shared/Options/DemoOptions.cs). The template writes the setting once,
    as the plain value true or false; anything else was set by hand and is neither. The name is
    matched whatever its case, as infra/secrets.ps1 matches it: the two must not read one app two
    ways."""
    settings = [entry for entry in listed(container, 'env')
                if isinstance(entry, dict) and str(entry.get('name')).lower() == DEMO_FLAG.lower()]
    if not settings:
        return 'false'
    value = settings[0].get('value')
    return value if len(settings) == 1 and isinstance(value, str) and value in ('true', 'false') else None


def demo_of(app, when='nothing was changed'):
    """Whether the app is the public demo, as the app itself says it: True when every container
    carries Demo__Enabled as true, False when none does. On in one container and off in another
    is not a state this folder deploys, and neither is a value the template never writes: no side
    is chosen, and the run stops. The refusal names the setting and the containers, by name on
    each side when they disagree; what was found in the setting is never repeated.

    It looks at every container the app has, and a deployment asks it before it checks the app's
    shape: a container too many that does not carry the setting is one more that says off, and
    with the demo on in the others it is met here first. The names are what shows it."""
    containers = listed((app.get('properties') or {}).get('template'), 'containers')
    says = [demo_says(container) for container in containers]
    assert_shape('The app', [
        f"{DEMO_FLAG} in the container {str(container.get('name'))!r} is something the template "
        'never writes (it writes the plain value true or false, once)'
        for container, said in zip(containers, says) if said is None], when)
    # Names only, as the shape check prints them; an entry that is not an object has none.
    names = [str(container.get('name')) if isinstance(container, dict) else '?' for container in containers]
    on, off = (sorted(name for name, said in zip(names, says) if said == side) for side in ('true', 'false'))
    assert_shape('The app', [f'{DEMO_FLAG} is true in {on} and not in {off}'] if on and off else [], when)
    return bool(on)


# --- Azure ---

def prove_secrets_are_refused(app_id):
    """Try to list the app's secrets and go on only if Azure refuses.

    The workflow asks for this on every run, so that the limit of the deployment identity is an
    event that happens, not a sentence in a role definition. `--output none`: if the listing were
    answered, the values would never reach this process's output.
    """
    url = f'https://management.azure.com{app_id}/listSecrets?api-version={API_VERSION}'
    try:
        result = run_az(['rest', '--method', 'POST', '--url', url, '--only-show-errors',
                         '--output', 'none'], f'POST {short(app_id)}/listSecrets')
    except AzError as error:
        raise RuntimeError('Could not prove that this identity is refused the secrets of the app. '
                           f'Nothing was changed. {error}') from None
    if result.returncode == 0:
        raise RuntimeError('This identity can list the secrets of the app: it was expected to be '
                           'refused. Nothing was changed. Look at its role assignments.')
    if 'AuthorizationFailed' not in (result.stderr or ''):
        raise RuntimeError('Could not prove that this identity is refused the secrets of the app: '
                           f'the listing failed another way. Nothing was changed. {redact(result.stderr)}')
    say('Tried to list the secrets of the app as this identity: the listing was refused.')


def wait_job(job_id, desired, timeout=300):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        job = rest('GET', job_id)
        state = job['properties'].get('provisioningState')
        if state in {'Failed', 'Canceled'}:
            raise RuntimeError(f'The job update ended with {state}.')
        if state == 'Succeeded' and images(job) == desired:
            return job
        time.sleep(5)
    raise RuntimeError('Timed out waiting for the job to take its new image.')


def wait_revision(app_id, desired, previous, timeout=900):
    """The app runs `desired` in a revision that did not exist before this request, and it is ready."""
    deadline = time.monotonic() + timeout
    latest = None
    while time.monotonic() < deadline:
        app = rest('GET', app_id)
        props = app['properties']
        state = props.get('provisioningState')
        if state in {'Failed', 'Canceled'}:
            raise RevisionFailed(f'The app update ended with {state}.')
        latest = props.get('latestRevisionName')
        if (state == 'Succeeded' and images(app) == desired and latest and latest != previous
                and props.get('latestReadyRevisionName') == latest):
            say(f'Revision {latest} is ready.')
            return app
        time.sleep(5)
    raise RevisionFailed(f'Timed out: the new revision never became ready in {timeout} s '
                         f'(latest: {latest}, before this request: {previous}).')


def wait_inactive(app_id, revision, timeout=180):
    """Until the revision that ran before is inactive, an answer could still come from it."""
    if not revision:
        return
    deadline = time.monotonic() + timeout
    while True:
        properties = rest('GET', f'{app_id}/revisions/{revision}').get('properties') or {}
        if properties.get('active') is False:
            say(f'Revision {revision} is inactive: what answers now is the new revision.')
            return
        if time.monotonic() >= deadline:
            raise RuntimeError(f'Revision {revision} was still active after {timeout} s, so an '
                               'answer could come from the old code: the smoke test was not run. '
                               'Nothing was proved wrong and nothing was put back; look at the '
                               "app's revisions, then deploy again.")
        time.sleep(5)


def diagnose(app_id):
    """Say what Azure knows about the latest revision and its replicas. Never raises."""
    try:
        latest = rest('GET', app_id)['properties'].get('latestRevisionName')
        if not latest:
            say('Could not read why: the app names no latest revision.')
            return
        revision = rest('GET', f'{app_id}/revisions/{latest}').get('properties') or {}
        say(f'Revision {latest}: ' + ', '.join(
            f'{field} {redact(str(revision.get(field)))[:500]}'
            for field in ('provisioningState', 'runningState', 'healthState', 'provisioningError')))
        replicas = rest('GET', f'{app_id}/revisions/{latest}/replicas').get('value') or []
        if not replicas:
            say(f'Revision {latest} has no replica.')
        for replica in replicas:
            for container in (replica.get('properties') or {}).get('containers') or []:
                say(f"Replica {replica.get('name')}, container {container.get('name')}: "
                    f"{container.get('runningState')} "
                    f"({redact(str(container.get('runningStateDetails')))[:300]}), "
                    f"ready {container.get('ready')}, started {container.get('started')}, "
                    f"restarts {container.get('restartCount')}.")
    except (AzError, KeyError, AttributeError, TypeError) as error:
        say(f'Could not read why: {error}')


def executions(job_id):
    return rest('GET', job_id + '/executions').get('value', [])


def started_within(execution, seconds, now=None):
    start = execution['properties'].get('startTime')
    if not start:
        return True
    now = now or datetime.datetime.now(datetime.timezone.utc)
    try:
        began = datetime.datetime.fromisoformat(start.replace('Z', '+00:00'))
    except ValueError:
        return True
    return (now - began).total_seconds() < seconds


def named(name):
    """An execution's name when it has the shape of one, else None. Every line of a deployment
    may be public: a field is printed only in the shape expected of it. Until 2026-10-03 this
    said that the script had read no answer of Azure's yet; it read them that day (README.md,
    "Measured on Azure"), and the rule stays."""
    return name if isinstance(name, str) and EXECUTION_NAME.fullmatch(name) else None


def told_name(name):
    return f"execution {named(name) or 'whose name is withheld'}"


def told_status(status, otherwise='status not reported'):
    """A status as a line may show it: one of the states this script knows, or words instead."""
    return status if isinstance(status, str) and status in STATUSES else otherwise


def state_of(execution):
    """An execution's status when it is text, else None: anything else is a state nobody named."""
    status = execution['properties'].get('status')
    return status if isinstance(status, str) else None


def stop_command(job_id, execution):
    """The command that stops one execution of a job, for whoever may: the owner. The execution
    is named only in the shape of a name."""
    match = re.search(r'/resourceGroups/([^/]+)/providers/Microsoft\.App/jobs/([^/]+)$', job_id)
    group, job = match.groups() if match else ('<resource group>', '<job>')
    return (f'az containerapp job stop --name {job} '
            f"--resource-group {group} --job-execution-name {named(execution) or '<its name>'}")


def stop_hint(job_id, execution):
    """The deployment identity may start the job and read its executions; it may not stop one."""
    return ('The deployment identity cannot stop it, and it blocks every later deploy until it '
            f'ends or the owner stops it: {stop_command(job_id, execution)}')


def moment(text):
    """A time as Azure writes it, or None. What is printed is this value printed again."""
    if not isinstance(text, str):
        return None
    try:
        parsed = datetime.datetime.fromisoformat(text.replace('Z', '+00:00'))
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=datetime.timezone.utc)


def stamp(time_):
    return time_.astimezone(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


def listed(node, key):
    """node[key] when it is a list, else nothing: an answer this script never read may hold anything."""
    value = node.get(key) if isinstance(node, dict) else None
    return value if isinstance(value, list) else []


def exit_code(properties, container):
    """(code, the container's entry), or (None, None) when Azure did not report one exit code."""
    entries = [entry for replica in listed(properties.get('detailedStatus'), 'replicas')
               for entry in listed(replica, 'containers') if isinstance(entry, dict)]
    named = [entry for entry in entries if entry.get('name') == container]
    if len(named) != 1 and len(entries) == 1:
        named = entries
    if len(named) == 1 and type(named[0].get('code')) is int:
        return named[0]['code'], named[0]
    return None, None


def verdict(name, properties, in_actions=False, container=JOBS[MIGRATE_JOB], codes=EXIT_CODES):
    """One line about an execution: name, status, times, exit code, reason. Every field is checked
    for its shape before it is printed, because inside GitHub Actions the line is public. The
    fields were seen filled by hand for a throwaway job on 2026-10-02, and by this script at the
    first deployment on 2026-10-03 (README.md, "Measured on Azure"). Inside GitHub Actions a
    reason that is not one plain word is withheld, and Azure's message is not printed at all.

    `container` and `codes` say whose exit code is read and how it is worded: the migrate job's
    unless the caller names another. A run of the pool job is read with that job's container and
    its own map, and only from the owner's terminal."""
    parts = [f"{told_name(name)}: {told_status(properties.get('status'))}"]
    began, ended = moment(properties.get('startTime')), moment(properties.get('endTime'))
    parts.append(f'started {stamp(began)}' if began else 'start not reported')
    if began and ended:
        parts.append(f'ended {stamp(ended)} ({round((ended - began).total_seconds())} s)')
    else:
        parts.append(f'ended {stamp(ended)}' if ended else 'end not reported')
    code, entry = exit_code(properties, container)
    meaning = codes.get(code, NOT_THE_TOOLS_CODE)
    parts.append('exit code not reported' if code is None else f'exit code {code} ({meaning})')
    reason = properties.get('reason')
    if reason in (None, ''):
        parts.append('no reason given')
    elif isinstance(reason, str) and re.fullmatch(r'[A-Za-z]{1,40}', reason):
        parts.append(f'reason {reason}')
    elif in_actions:
        parts.append('reason withheld, read it with --job-log')
    else:
        parts.append(f'reason {redact(str(reason))[:300]!r}')
    line = 'Verdict: ' + ', '.join(parts) + '.'
    if not in_actions:
        texts = (properties.get('message'), (entry or {}).get('additionalInformation'))
        said = [str(text) for text in texts if text]
        if said:
            line += ' Azure says: ' + ' / '.join(redact(text)[:500] for text in said)
    return line


def detailed(job_id, name, known, a_failed_read_raises=False):
    """An execution's properties, read once more with the version that carries its container's
    exit code. Never raises: if that read fails or lacks the execution, the answer is what the
    polling already knew. The one exception is asked for by name: a caller for whom the exit
    code decides (a start of the pool job by hand) is told of a read that Azure refused or
    failed, as Azure worded it."""
    properties = (known or {}).get('properties') or {}
    try:
        answer = rest('GET', job_id + '/executions', api_version=VERDICT_API_VERSION)
        for execution in listed(answer, 'value'):
            if execution.get('name') == name and isinstance(execution.get('properties'), dict):
                properties = execution['properties']
    except AttributeError:
        pass
    except AzError:
        if a_failed_read_raises:
            raise
    return properties


def report_verdict(job_id, name, known, in_actions):
    """Read the execution once more, with the version that carries its exit code, and print its
    verdict. Never raises: if that read fails or lacks the execution, the verdict is what the
    polling already knew, and "Failed" is still a verdict."""
    line = verdict(name, detailed(job_id, name, known), in_actions)
    say(line)
    summary = os.environ.get('GITHUB_STEP_SUMMARY') if in_actions else None
    if summary:
        # The same line on the run's summary page. A summary that cannot be written fails nothing.
        try:
            with open(summary, 'a', encoding='utf-8') as page:
                page.write(f'Migration: {line}\n')
        except OSError:
            pass


def in_progress(executions, timeout):
    """The first execution, of a list already read, that may still be running, or None. It reads
    nothing by itself: whoever asks has read the job's executions once, and says what a run in
    progress means for it."""
    for execution in executions:
        status = state_of(execution)
        # A state that is neither running nor finished ("Unknown") blocks only while a run that
        # started then could still be alive.
        if status in ACTIVE or (status not in FINISHED and started_within(execution, timeout + 120)):
            return execution
    return None


def start_once(job_id, known, before='deploying again'):
    """Start a job and answer with the name of the execution that start made. The request is
    sent once. When its answer names no execution, the run it may already have launched is
    looked for among the job's executions, by the names that were not `known` before the start,
    for one minute. `before` ends the sentence of a start that made nothing to be seen: what the
    caller would do next."""
    name = rest('POST', job_id + '/start').get('name')
    deadline = time.monotonic() + 60
    while not name and time.monotonic() < deadline:
        # Never start twice: the first request may already have launched the run.
        fresh = [e['name'] for e in executions(job_id) if e['name'] not in known]
        if len(fresh) > 1:
            names = ', '.join(named(new) or 'a name withheld' for new in fresh)
            raise RuntimeError(f'Several new executions appeared ({names}); inspect them.')
        name = fresh[0] if fresh else None
        if not name:
            time.sleep(5)
    if not name:
        raise RuntimeError('The start was accepted but no execution appeared; inspect the job '
                           f'before {before}.')
    return name


def run_migration(job_id, timeout, in_actions=False):
    before = executions(job_id)
    blocking = in_progress(before, timeout)
    if blocking:
        state = told_status(state_of(blocking), 'in a state this script does not know')
        raise RuntimeError(f"Execution {named(blocking['name']) or 'whose name is withheld'} "
                           f"is {state}: a migration may still be running. "
                           f"{stop_hint(job_id, blocking['name'])}")
    known = {execution['name'] for execution in before}

    name = start_once(job_id, known)

    # The name and each status come from Azure and go into a log that may be public: both are
    # printed only in the shape expected, as the verdict prints them.
    label = told_name(name)
    say(f'Migration {label} started.')
    deadline = time.monotonic() + timeout + 120
    seen = object()
    while time.monotonic() < deadline:
        match = [e for e in executions(job_id) if e['name'] == name]
        status = state_of(match[0]) if match else None
        if status != seen:
            say(f'Migration {label}: {told_status(status)}.')
            seen = status
        if status == 'Succeeded':
            report_verdict(job_id, name, match[0], in_actions)
            return name
        if status in FAILED:
            report_verdict(job_id, name, match[0], in_actions)
            raise RuntimeError(f'The migration did not succeed ({label}: {status}). The '
                               'app was not touched. The job now runs the new tools image and some '
                               'migrations may be applied. What it printed is kept in the log '
                               'workspace and is never fetched here: read it from a terminal with '
                               "`python infra/deploy.py --job-log`, and the app's own lines with "
                               '`--app-log <minutes>`.')
        time.sleep(5)
    raise RuntimeError(f'Timed out waiting for {label}. The app was not touched. '
                       f'{stop_hint(job_id, name)}')


# --- The smoke test ---

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def fetch(opener, url, body=None):
    """(status, content type, text) for any answer, an error status included."""
    data = None if body is None else json.dumps(body).encode()
    headers = {} if body is None else {'Content-Type': 'application/json'}
    request = urllib.request.Request(url, data=data, headers=headers)
    try:
        with opener.open(request, timeout=20) as response:
            return (response.status, response.headers.get_content_type(),
                    response.read(2_000_000).decode('utf-8'))
    except urllib.error.HTTPError as error:
        return error.code, error.headers.get_content_type(), error.read(200_000).decode('utf-8')


def is_spa(status, content_type, body):
    return bool(status == 200 and content_type == 'text/html'
                and '<!doctype html>' in body.lower() and '<title>AzureBank</title>' in body
                and 'id="root"' in body and re.search(r'<script\b[^>]*src="/assets/[^"]+\.js"', body))


def is_the_refusal(status, content_type, body):
    """401 with the API's own error code: the BFF reached the API with its key, and the API asked
    the database about the address before it refused.

    Observed on 2026-10-02 on a local stack of this code (compose.yaml, Production images):
        401 application/json
        {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
         "detail":"Invalid email or password.","instance":"/api/auth/login",
         "errorCode":"INVALID_CREDENTIALS","traceId":"..."}
    The same request answered 500 on a database with no table, 503 with no database at all, and
    429 with Retry-After: 60 once the shared limit was spent; /health/ready said Healthy each time.
    That is why this check exists: without it a deployment that skipped the migration would pass.
    """
    if status != 401:
        return False
    try:
        answer = json.loads(body)
    except ValueError:
        return False
    return isinstance(answer, dict) and answer.get('errorCode') == 'INVALID_CREDENTIALS'


def error_code(body):
    """An answer's error code when it has the shape of one, else None. It is the one thing of an
    answer to a registration that a line may show: the rest of that body is never printed."""
    try:
        answer = json.loads(body)
    except ValueError:
        return None
    code = answer.get('errorCode') if isinstance(answer, dict) else None
    return code if isinstance(code, str) and ERROR_CODE.fullmatch(code) else None


def is_closed(status, content_type, body):
    """403 with the error code of a registration the public demo keeps closed. The BFF answers it
    by itself, before the request reaches the API and whatever the body holds; with the demo off
    the same request with an empty body is answered 400. Both are read from the BFF's code and
    from its tests (backend/tests/AzureBank.Bff.Tests/DemoClaimTests.cs), not from an answer this
    script ever got."""
    return status == 403 and error_code(body) == REGISTRATION_CLOSED


def prove_registration_is_closed(opener, url):
    """With the demo on, one registration that must be refused as closed. The body is an empty
    object: it holds nothing that could register anybody, wherever it lands. It is tried and
    waited for as the sign-in is. By the BFF's code it is expected to spend a permit of the
    limit sign-ins share even when it is refused as closed: the limiter stands before the
    demo's refusal (backend/src/AzureBank.Bff/Program.cs). Three verdicts: the refusal passes;
    any other definite answer on the last try fails; a 429 or a silence on the last try proves
    nothing either way. An answer that is not the refusal is told by its status and its error
    code, never by its body: a door that is open answers a registration with the data of
    whoever it registered."""
    answer = None
    for attempt in range(SIGN_IN_TRIES):
        if attempt:
            time.sleep(WAIT_AFTER_429 if answer and answer[0] == 429 else WAIT_BETWEEN_TRIES)
        try:
            answer = fetch(opener, url + REGISTER_PATH, {})
        except NO_ANSWER:
            answer = None
            continue
        if is_closed(*answer):
            return
    if answer is None or answer[0] == 429:
        raise SmokeUnproven(f'Smoke test unproven: in {SIGN_IN_TRIES} tries the registration probe '
                            f'got {"no answer" if answer is None else "429 (rate limited)"} last. '
                            'The page, the readiness answer and the sign-in answer were right. '
                            'Nothing was proved wrong and nothing is put back: whether the demo '
                            'keeps registration closed is not known. Deploy again; or, from a '
                            'terminal, run `python infra/deploy.py --check`, which asks the address '
                            'the same four questions and moves nothing; or ask by hand: '
                            f'POST {REGISTER_PATH} with the body {{}} must answer 403 '
                            f'{REGISTRATION_CLOSED}.')
    got = error_code(answer[2]) or 'with no error code of the shape expected'
    raise SmokeFailed(f'Smoke test failed: the registration probe expected 403 {REGISTRATION_CLOSED} '
                      f'and got {answer[0]} {got}. On the public demo a registration must be '
                      'refused as closed, whatever its body: this answer was not that refusal.')


def told(answer, text=False):
    """An answer as a failure message shows it: the status, and the text when it says why."""
    status, _, body = answer
    return f'{status} {body[:80]!r}' if text or status == 'no answer' else str(status)


def tag_fault(page, demo):
    """What a failed smoke test says of the demo's tag: nothing, unless the page is the SPA and
    carries the tag on an app that says the demo is off, or lacks it on one that says it is on."""
    if not is_spa(*page) or (DEMO_TAG in page[2]) == bool(demo):
        return ''
    return (f" The page {'does not carry' if demo else 'carries'} the tag of the public demo "
            f"({DEMO_TAG}), and the app's containers say the demo is {'on' if demo else 'off'}.")


def smoke(url, timeout=300, demo=False):
    """Ask the address what only a running app can answer. `demo` is what the app's containers
    say of themselves (demo_of), and the page must agree: it carries the demo's tag exactly when
    the demo is on. Then one sign-in that must be refused, and, only with the demo on, one
    registration that must be refused as closed. With the demo off no registration is ever sent:
    the door is open then. Both of the demo's checks are answered by the BFF alone, and neither
    spends a copy of the pool: that a visitor is handed a copy, and that a PIN is taken, is
    proved by nothing here."""
    opener = urllib.request.build_opener(NoRedirect)
    deadline = time.monotonic() + timeout
    while True:
        page = ready = ('not asked', None, '')
        try:
            page = fetch(opener, url + '/')
            ready = fetch(opener, url + '/health/ready')
            # "Healthy" and not just 200: the BFF answers 200 "Degraded" when the API is down.
            if (is_spa(*page) and not tag_fault(page, demo)
                    and ready[0] == 200 and ready[2].strip() == 'Healthy'):
                break
        except NO_ANSWER as error:
            # A dropped connection is one more wrong answer of this loop, never the end of the run.
            silence = ('no answer', None, f'{type(error).__name__}: {error}')
            page, ready = (silence, ready) if page[0] == 'not asked' else (page, silence)
        if time.monotonic() >= deadline:
            raise SmokeFailed('Smoke test failed: the page or the readiness answer was wrong '
                              f'after {timeout} s (/ -> {told(page)}, /health/ready -> '
                              f'{told(ready, text=True)}).{tag_fault(page, demo)}')
        time.sleep(5)

    # One sign-in that must be refused. Three verdicts: the refusal passes; any other definite
    # answer on the last try fails; a 429 or a silence on the last try proves nothing either way.
    answer = None
    for attempt in range(SIGN_IN_TRIES):
        if attempt:
            time.sleep(WAIT_AFTER_429 if answer and answer[0] == 429 else WAIT_BETWEEN_TRIES)
        try:
            answer = fetch(opener, url + SMOKE_PATH, SMOKE_LOGIN)
        except NO_ANSWER:
            answer = None
            continue
        if is_the_refusal(*answer):
            if demo:
                # It returns only once the registration was refused as closed: a run that did
                # not pass never says it passed.
                prove_registration_is_closed(opener, url)
            say(f'Smoke passed: {url}/ is the SPA, /health/ready is Healthy, and a sign-in for an '
                'unknown address was refused by the API after it asked the database.'
                + (" It is the public demo: the page carries the demo's tag, and a registration "
                   'with an empty body was refused as closed.' if demo else ''))
            return
    if answer is None or answer[0] == 429:
        raise SmokeUnproven(f'Smoke test unproven: in {SIGN_IN_TRIES} tries the sign-in probe got '
                            f'{"no answer" if answer is None else "429 (rate limited)"} last. The '
                            'page and the readiness answer were right. The new revision is serving '
                            'and was not put back: nothing was proved wrong. Deploy again, or check '
                            'the sign-in by hand.')
    raise SmokeFailed('Smoke test failed: the sign-in probe expected 401 INVALID_CREDENTIALS and '
                      f'got {answer[0]} {answer[2][:200]!r}.')


# --- The deployment ---

def address_of(app):
    """The app's public host name, or None when it has no ingress (the shape check then says so)."""
    ingress = ((app.get('properties') or {}).get('configuration') or {}).get('ingress') or {}
    fqdn = ingress.get('fqdn')
    if fqdn is not None and not (isinstance(fqdn, str) and re.fullmatch(r'[a-z0-9.-]+', fqdn)):
        # Never printed: it would become a line of the log, and under Actions a workflow command.
        raise RuntimeError('Unexpected application address.')
    return fqdn


def tag_of(image_map):
    """The one tag every image carries, or the references themselves when they differ."""
    tags = {image.rsplit(':', 1)[-1] for image in image_map.values()}
    return tags.pop() if len(tags) == 1 else ', '.join(sorted(image_map.values()))


def running_now(app, jobs):
    """What a failed run must be able to go back to, printed before anything changes."""
    properties = app.get('properties') or {}
    references = [c.get('image') for c in (properties.get('template') or {}).get('containers') or []]
    for job in jobs.values():
        references += [c.get('image') for c in
                       ((job.get('properties') or {}).get('template') or {}).get('containers') or []]
    return (f"Running now: {', '.join(str(r) for r in references)}; latest revision "
            f"{properties.get('latestRevisionName')}, latest ready revision "
            f"{properties.get('latestReadyRevisionName')}.")


def put_back(app_id, app):
    """PATCH the template read at the start under a new suffix and wait for that revision."""
    before = images(app)
    label = tag_of(before)
    template = copy.deepcopy(app['properties']['template'])
    hint = re.sub(r'[^a-z0-9]', '', label.lower())[:12] if len(label) == 40 else ''
    template['revisionSuffix'] = '-'.join(part for part in ('b', hint, uuid.uuid4().hex[:8]) if part)
    say(f'Putting the app back to {label}.')
    try:
        latest = rest('GET', app_id)['properties'].get('latestRevisionName')
        rest('PATCH', app_id, {'location': app['location'], 'properties': {'template': template}})
        restored = wait_revision(app_id, before, latest)
    except (AzError, RevisionFailed, KeyError) as error:
        raise RuntimeError(f'The put-back to {label} did not succeed ({error}). The app may be '
                           'serving a broken revision: look at it now (infra/README.md, "When '
                           'something fails").') from error
    assert_shape('The app', app_drift(restored), 'after the put-back')
    return label


def read_pool_job(job_id):
    """The pool job, which is expected to be there once the app says it is the demo. What Azure
    answers for a job that is not there, to an identity whose role is on each resource by itself
    ("not found", or "not authorised"), has not been seen: no answer is read as "there is no pool
    job", and the run stops before anything is changed."""
    try:
        return rest('GET', job_id)
    except AzError as error:
        raise RuntimeError(f'The app says the demo is on, and the job {POOL_JOB} could not be '
                           f"read: {str(error).rstrip('.')}. Nothing was changed. See "
                           'infra/README.md, "When something fails".') from None


def refuse_beside_a_pool_run(job_id, job, starting=False):
    """Stop while a run of the pool job is, or may still be, in progress, and answer with the
    executions that were read. A deployment asks because the migration is about to change the
    schema that run works on, and the job itself is about to be moved. A start by hand asks
    (`starting`) because two runs at once each top the pool up from their own count
    (backend/tools/AzureBank.Seeder/README.md); it is the same rule in the words of a start. It
    is one read, before anything is changed or started: a run the schedule starts after it is not
    seen, and nothing here holds the schedule back.

    It is also one answer. Whether Azure gives the list of a job's executions in pages, and in
    which order, is recorded nowhere in this repository: if it does, a link to a next page is not
    followed, and a run listed only on a later page is not seen either."""
    timeout = job['properties']['configuration']['replicaTimeout']
    left = 'Nothing was started' if starting else 'Nothing was changed'
    try:
        runs = executions(job_id)
    except AzError as error:
        # The job itself was read a moment ago. Azure's words alone would not say that nothing
        # was changed, nor that the question left open is whether a pool run is in progress.
        raise RuntimeError(f'The executions of the job {POOL_JOB} could not be read, so whether a '
                           f"pool run is in progress is not known: {str(error).rstrip('.')}. {left}. "
                           'See infra/README.md, "When something fails".') from None
    running = in_progress(runs, timeout)
    if running:
        state = told_status(state_of(running), 'in a state this script does not know')
        refused, again = (('a second run is not started', 'start it again') if starting
                          else ('a deployment does not start', 'deploy again'))
        # The last sentence is for a run that never ends for this script: one in a state nobody
        # named and with no start time blocks for as long as it is listed so (started_within), and
        # the deployment identity may not stop an execution.
        raise RuntimeError(f"Execution {named(running['name']) or 'whose name is withheld'} of the "
                           f'job {POOL_JOB} is {state}: a pool run may still be in progress, and '
                           f'{refused} beside one. {left}. A run is '
                           f"expected to end within the job's timeout ({timeout} s): {again} "
                           'after that. If it is refused again then, the owner reads the job\'s '
                           'executions and stops that one (infra/README.md, "When something fails").')
    return runs


def deploy(subscription, resource_group, tag, app_only=False, in_actions=False,
           expect_secrets_refused=False):
    if not re.fullmatch(r'[0-9a-f]{40}', tag):
        raise ValueError('IMAGE_TAG must be a full lowercase commit SHA.')
    if not subscription or not resource_group:
        raise ValueError('AZURE_SUBSCRIPTION_ID and AZURE_RESOURCE_GROUP must be set.')
    if app_only and in_actions:
        # It skips the migration and takes any published tag: a road for the owner's own terminal,
        # not for whoever can dispatch a workflow.
        raise ValueError('--app-only is refused inside GitHub Actions: it is the road back the '
                         'owner takes by hand, from a terminal.')
    prefix = (f'/subscriptions/{urllib.parse.quote(subscription, safe="")}'
              f'/resourceGroups/{urllib.parse.quote(resource_group, safe="")}/providers/Microsoft.App')
    app_id = f'{prefix}/containerApps/{APP}'
    app_images = {name: f'ghcr.io/gurgant/azurebank-{name}:{tag}' for name in ('bff', 'api')}
    tools = f'ghcr.io/gurgant/azurebank-tools:{tag}'

    # Read everything before changing anything.
    app = rest('GET', app_id)
    fqdn = address_of(app)
    if in_actions and fqdn:
        # The log of a public repository is public. This hides the name in it and nothing more.
        print(f'::add-mask::{fqdn}', flush=True)
    # The app itself says whether it is the public demo, and two containers that disagree stop
    # the run here. The pool job is asked for only when it says so: with the demo off, whether
    # such a job exists is not a question this script puts to Azure.
    demo = demo_of(app)
    jobs = {}
    if not app_only:
        jobs[MIGRATE_JOB] = rest('GET', f'{prefix}/jobs/{MIGRATE_JOB}')
        if demo:
            jobs[POOL_JOB] = read_pool_job(f'{prefix}/jobs/{POOL_JOB}')
        else:
            say(f'The app says the demo is off: the job {POOL_JOB} is not read and not moved.')
    say(running_now(app, jobs))
    assert_shape('The app', app_drift(app), 'nothing was changed')
    if not app_only:
        assert_shape(f'The job {MIGRATE_JOB}', job_drift(jobs[MIGRATE_JOB]), 'nothing was changed')
        timeout = jobs[MIGRATE_JOB]['properties']['configuration']['replicaTimeout']
        if not 1 <= timeout <= MAX_JOB_TIMEOUT:
            raise RuntimeError('The migrate job timeout is outside what this workflow waits for.')
        if POOL_JOB in jobs:
            assert_shape(f'The job {POOL_JOB}', pool_drift(jobs[POOL_JOB]), 'nothing was changed')
            refuse_beside_a_pool_run(f'{prefix}/jobs/{POOL_JOB}', jobs[POOL_JOB])
    job_patches = {name: image_patch(jobs[name], {container: tools})
                   for name, container in JOBS.items() if name in jobs}
    app_patch = image_patch(app, app_images)
    # A new suffix makes a new revision even when the images are the same commit again.
    app_patch['properties']['template']['revisionSuffix'] = f'd-{tag[:12]}-{uuid.uuid4().hex[:8]}'
    previous = app['properties'].get('latestRevisionName')
    if expect_secrets_refused:
        prove_secrets_are_refused(app_id)

    def move_job(name):
        say(f'Moving {name} to {tag[:12]}.')
        patch(f'{prefix}/jobs/{name}', job_patches[name], f'the job {name}')
        return wait_job(f'{prefix}/jobs/{name}', {JOBS[name]: tools})

    if not app_only:
        moved = move_job(MIGRATE_JOB)
        assert_shape(f'The job {MIGRATE_JOB}', job_drift(moved), 'after its image moved')
        run_migration(f'{prefix}/jobs/{MIGRATE_JOB}', timeout, in_actions=in_actions)
        # Only now: a failed migration must leave the pool job on the image that matches the
        # schema it runs on.
        if POOL_JOB in jobs:
            moved = move_job(POOL_JOB)
            assert_shape(f'The job {POOL_JOB}', pool_drift(moved), 'after its image moved')
        say('Migration succeeded; moving both app containers together.')
    else:
        say('Moving both app containers together; no job is touched and no migration runs.')

    patch(app_id, app_patch, 'the app')
    try:
        moved = wait_revision(app_id, app_images, previous)
        assert_shape('The app', app_drift(moved), 'after its images moved')
        wait_inactive(app_id, previous)
        smoke(f'https://{fqdn}', demo=demo)
    # A shape read back wrong is put back too: put_back sends the template read at the start,
    # which passed the same check, never the one read after the move.
    except (RevisionFailed, ShapeError, SmokeFailed) as failure:
        say(str(failure))
        diagnose(app_id)
        label = put_back(app_id, app)
        raise RuntimeError(f'{failure} The app was put back to {label}; the schema stays where '
                           'the migration left it. What the containers printed is in the log '
                           'workspace: `python infra/deploy.py --app-log 30`, from a terminal.'
                           ) from failure


# --- The check that moves nothing: the owner's terminal only ---
# A run of the template is expected to make a revision outside this script: no migration, no
# smoke test and no put-back follow it. This is its read-back. Every request it sends is a read,
# but for the two listings of secrets, which ask for no change either.

def settled_app(app_id, timeout=300):
    """The app, read until its last update has succeeded and its latest revision is its latest
    ready one: what wait_revision waits for after a deployment's own request, here for a
    revision somebody else made. An update that ended Failed or Canceled is not waited out."""
    deadline = time.monotonic() + timeout
    while True:
        app = rest('GET', app_id)
        properties = app.get('properties') or {}
        state = properties.get('provisioningState')
        latest, ready = properties.get('latestRevisionName'), properties.get('latestReadyRevisionName')
        if state == 'Succeeded' and latest and ready == latest:
            return app
        ended = state in {'Failed', 'Canceled'}
        if ended or time.monotonic() >= deadline:
            raise RuntimeError(
                f"{'The app is' if ended else f'After {timeout} s the app was'} not in a state to be "
                f'checked (its last update: {state!r}; its latest revision: {latest!r}; its latest '
                f'ready revision: {ready!r}). Until the latest revision is the latest ready one, an '
                'answer could still come from another revision: nothing was proved, nothing was '
                "moved. Look at the app's revisions, then check again.")
        time.sleep(5)


def is_inactive(revision):
    """True only for a revision that says so. An entry of another shape, and one that does not
    say, is not taken for inactive."""
    properties = revision.get('properties') if isinstance(revision, dict) else None
    return isinstance(properties, dict) and properties.get('active') is False


def wait_alone(app_id, latest, timeout=180):
    """Until every other revision is inactive, an answer could still come from one of them: what
    wait_inactive waits for after a deployment, for the one revision it knows ran before. Here
    no earlier revision is known, so the app's revisions are listed. The latest must be in the
    list, or the list is not one that could have shown an active revision; every other entry
    must say it is inactive.

    It is one answer. Whether Azure gives the list of an app's revisions in pages is recorded
    nowhere in this repository: a list that comes with a link to a next page does not show every
    revision, the link is not followed, and the check stops there."""
    deadline = time.monotonic() + timeout
    while True:
        answer = rest('GET', f'{app_id}/revisions')
        if isinstance(answer, dict) and answer.get('nextLink'):
            raise RuntimeError(
                "The list of the app's revisions came with a link to a next page, and this script "
                'reads one answer: a revision that is still active could be on a page it did not '
                'read. An answer could then still come from another revision: nothing was proved, '
                'nothing was moved.')
        entries = listed(answer, 'value')
        names = [entry.get('name') if isinstance(entry, dict) else None for entry in entries]
        awake = [entry for entry, name in zip(entries, names) if name != latest and not is_inactive(entry)]
        if latest in names and not awake:
            say(f'Revision {latest} is the latest ready one and no other revision is active: what '
                'answers now is that revision.')
            return
        if time.monotonic() >= deadline:
            found = []
            if awake:
                found.append(f"{len(awake)} other revision{'' if len(awake) == 1 else 's'} had not "
                             'gone inactive')
            if latest not in names:
                found.append('the latest ready revision was not in the list')
            raise RuntimeError(
                f"After {timeout} s the list of the app's revisions did not show {latest} answering "
                f"alone ({', and '.join(found)}), so an answer could still come from another "
                "revision: nothing was proved, nothing was moved. Look at the app's revisions, "
                'then check again.')
        time.sleep(5)


# The pool job's two secrets, by what a line calls each. infra/main.bicep writes them from the
# two expressions it writes the app's secrets of the same names from, so a run of the template
# is expected to leave them equal. The job holds a copy of its own: a secret of the app that is
# written again by itself is not expected to change it. A test holds the two names equal to the
# template's (infra/test_scripts.py).
POOL_SECRETS = {'pin-pepper': 'PIN pepper', 'app-connection': 'connection string'}


def listed_secrets(resource_id, whose):
    """The secrets of the app or of a job, as whoever is signed in may list them: name -> every
    value listed under that name. The values stay in this process. Nothing here prints one, and
    no error holds one, a part of one or its length. A deployment never calls this: it proves
    that its own identity is refused the listing (prove_secrets_are_refused)."""
    try:
        answer = rest('POST', f'{resource_id}/listSecrets')
    except (AzError, ValueError) as error:
        # A ValueError is a decoder's: the CLI answered, and what it printed is not a listing. Its
        # own words say where in the answer it stopped, and of a listing not even that is shown:
        # the place is worked out from what stood before it.
        why = str(error).rstrip('.') if isinstance(error, AzError) else 'the answer could not be read'
        raise RuntimeError(f"The secrets of {whose} could not be listed, so the pool job's secrets "
                           f"could not be compared with the app's: {why}. "
                           'Nothing was moved. See infra/README.md, "When something fails".') from None
    found = {}
    for entry in listed(answer, 'value'):
        if isinstance(entry, dict) and isinstance(entry.get('name'), str):
            found.setdefault(entry['name'], []).append(entry.get('value'))
    return found


def compare_pool_secrets(app_id, pool_id):
    """Say whether the pool job's PIN pepper and connection string are the app's, and show
    neither. The tool that builds the copies needs the pepper the API has
    (backend/src/AzureBank.Api/README.md, Security__PinPepper). With two that differ a PIN of a
    copy is expected to be refused while every status stays good: a PIN that is not taken is
    answered 200 (backend/src/AzureBank.Bff/Controllers/BffAuthController.cs, verify-pin), so no
    status, alert or exit code is expected to show it. That is why the two are compared here,
    where nothing is spent, and not left to a browser alone.

    Two that cannot be compared, and two that differ, both end the check."""
    sides = [(whose, listed_secrets(resource_id, whose))
             for whose, resource_id in (('the app', app_id), (f'the job {POOL_JOB}', pool_id))]
    # One value of text that is not empty: two secrets that hold nothing are not "the app's".
    unreadable = [f'{whose} lists no single value of text for {name}'
                  for name in POOL_SECRETS for whose, secrets in sides
                  if not (len(secrets.get(name, [])) == 1 and isinstance(secrets[name][0], str)
                          and secrets[name][0])]
    closing = 'Nothing was moved, and no value was shown. See infra/README.md, "When something fails".'
    if unreadable:
        raise RuntimeError("The pool job's secrets could not be compared with the app's: "
                           f"{'; '.join(unreadable)}. {closing}")
    (_, ours), (_, theirs) = sides
    # Bytes, and an encoding that cannot fail: an error of the encoder would quote a character.
    differing = [name for name in POOL_SECRETS if not hmac.compare_digest(
        ours[name][0].encode('utf-8', 'surrogatepass'), theirs[name][0].encode('utf-8', 'surrogatepass'))]
    if differing:
        which = ' and '.join(f'{POOL_SECRETS[name]} (its secret {name})' for name in differing)
        raise RuntimeError(f"The pool job's {which} {'differs' if len(differing) == 1 else 'differ'} "
                           f"from the app's. {closing}")
    say("The pool job's PIN pepper and connection string are the app's: each was listed on both "
        'and compared here, and no value was shown.')


def check(subscription, resource_group, in_actions=False):
    """Read the running app and ask its address what a deployment asks at its end. Nothing is
    moved: no request here changes a resource or starts a job, and a wrong answer puts nothing
    back.

    In order: the app, read until its last update has succeeded with its latest revision as its
    latest ready one; from that read its address, whether it is the public demo, and its shape,
    and an app in shape that reports no address ends the check there; its revisions, until no
    other one is active; with the demo on the pool job and its shape, then the job's two secrets
    against the app's; then the smoke test, with the demo as the app says it. With the demo off
    the pool job is not read and no secret is listed."""
    if in_actions:
        raise ValueError("--check is refused inside GitHub Actions: it is the owner's read of the "
                         'running app, from a terminal. A workflow run checks the app at the end of '
                         'its own deployment.')
    prefix = f'{prefix_of(subscription, resource_group)}/Microsoft.App'
    app_id = f'{prefix}/containerApps/{APP}'
    app = settled_app(app_id)
    fqdn = address_of(app)
    demo = demo_of(app, when='nothing was moved')
    assert_shape('The app', app_drift(app), 'nothing was moved')
    if not fqdn:
        # The shape check does not read the host name: an ingress in shape may still hold none.
        raise RuntimeError('The app is in shape and reports no address (its ingress holds no host '
                           'name), so there is nothing to ask: nothing was proved, nothing was moved.')
    latest = app['properties']['latestRevisionName']
    wait_alone(app_id, latest)
    if demo:
        pool_id = f'{prefix}/jobs/{POOL_JOB}'
        assert_shape(f'The job {POOL_JOB}', pool_drift(read_pool_job(pool_id)), 'nothing was moved')
        compare_pool_secrets(app_id, pool_id)
    else:
        say(f'The app says the demo is off: the job {POOL_JOB} is not read and no secret is listed.')
    try:
        smoke(f'https://{fqdn}', demo=demo)
    except (SmokeFailed, SmokeUnproven) as failure:
        # The smoke test words its verdicts for a deployment, and nothing was deployed here.
        raise type(failure)(f'{failure} This was --check, not a deployment: nothing was moved and '
                            'nothing is put back; where that says to deploy again, check again.') from None
    say(f'Checked: {latest} is the latest ready revision and no other is active; '
        + (f'the demo is on and the job {POOL_JOB} is in shape' if demo else 'the demo is off')
        + '; the smoke test passed; nothing was moved.')


# --- A run of the pool job by hand: the owner's terminal only ---
# The template gives the pool job a schedule, and a deployment never starts it. This starts it
# once beside the schedule, as whoever is signed in: the first fill, or a refill by hand.

def where_it_printed(name):
    """Where what a run of the pool job printed is read: the last sentence of a start by hand.
    The command is named only with a name it takes: --pool-log asks for the lines of one
    execution by its name, and a name of another shape is refused there and never printed here."""
    if not named(name):
        return 'What it printed is kept in the log workspace (infra/README.md, "Reading the logs").'
    return (f"`python infra/deploy.py --pool-log {name}` reads the run's verdict again and prints the "
            "run's lines, once the log workspace has them: a line takes minutes to arrive.")


def end_pool_run(name, code, status=None):
    """Say how a run of the pool job that is over ended, by the exit code of its container and
    never by the execution's status alone: how Azure words an execution whose container exited
    with a signal's code has not been seen. 0, 10, 11 and 15 end well, the last three with a
    signal (POOL_RUN_ENDS_WELL). Any other code, and a code Azure did not report, is a failure:
    nothing is guessed, and nothing is started again. A signal names the count of the run's
    summary line that it says to read.

    `status` is the one the run's verdict showed. A run that ends well is told that the exit code
    decided wherever the line above could make it doubted: for a signal, and for a status that
    says the run failed."""
    label = told_name(name)
    if code is None:
        raise RuntimeError(f'The pool run ended and Azure reported no exit code for it ({label}: exit '
                           'code not reported). How it ended is not guessed from its status, and it '
                           f'was not started again. {where_it_printed(name)}')
    how = f'{label}: exit code {code}, {POOL_EXIT_CODES.get(code, NOT_THE_TOOLS_CODE)}'
    count = POOL_COUNTS.get(code)
    points_at = (f" The code is a signal to read the count '{count}' on the run's summary line "
                 '(docs/runbooks/demo-pool.md, "The line").' if count else '')
    if code not in POOL_RUN_ENDS_WELL:
        raise RuntimeError(f'The pool run did not end well ({how}). It was not started again.'
                           f'{points_at} {where_it_printed(name)}')
    decides = (' The exit code decides here, whatever status Azure gave the execution.'
               if count or status in FAILED else '')
    say(f'The pool run ended well ({how}).{decides}{points_at} {where_it_printed(name)}')


def pool_run(subscription, resource_group, in_actions=False):
    """Start the pool job once and wait for that exact execution, then end by its exit code.

    In order: the job, read and checked for its shape, its schedule and its timeout included;
    its executions, read once, and a refusal while one is or may still be in progress; the one
    start, said in a line before it is sent; that execution, read until it has ended, for the
    job's timeout and two minutes; its verdict, read with the version that carries the exit
    code; and the end, by that code alone.
    A read that fails once the start was made, or a list of executions that then comes in a shape
    this script does not read, ends the wait in a sentence that says the run was started and may
    go on. Once the run is seen over, a read of its exit code that Azure refuses or fails ends
    the command as failed in a sentence of its own, with Azure's words: it is not said to be a
    run for which Azure reported no exit code.

    It reads nothing of the app and does not ask whether the demo is on. The job's container
    carries the demo's flag as the literal true (infra/main.bicep), so a run is expected to build
    the pool whatever the app's two containers say: what they say is --check's to read, before a
    run is started by hand. A run the schedule starts after the one read of the executions is not
    seen, as in a deployment."""
    if in_actions:
        raise ValueError('--pool-run is refused inside GitHub Actions: the pool job runs on its '
                         'schedule, and a run beside the schedule is started by the owner, from a '
                         "terminal. A deployment moves that job's image and never starts it.")
    job_id = f'{prefix_of(subscription, resource_group)}/Microsoft.App/jobs/{POOL_JOB}'
    try:
        job = rest('GET', job_id)
    except AzError as error:
        # What Azure answers for a job that is not there has not been seen: its words are shown,
        # and no answer is read as "the demo is off".
        raise RuntimeError(f"The job {POOL_JOB} could not be read: {str(error).rstrip('.')}. Nothing "
                           'was started. infra/main.bicep writes that job only with the demo on: see '
                           'infra/README.md, "When something fails".') from None
    assert_shape(f'The job {POOL_JOB}', pool_drift(job), 'nothing was started', before='it is started')
    timeout = job['properties']['configuration']['replicaTimeout']
    known = {execution['name'] for execution in refuse_beside_a_pool_run(job_id, job, starting=True)}
    # Said before the one write of this command: until the start's answer has come, no other line
    # says that a run may exist, and an interrupt at that moment would leave none.
    say(f'Starting the job {POOL_JOB} once.')
    try:
        name = start_once(job_id, known, before='it is started again')
    except (AzError, KeyError, TypeError, AttributeError) as error:
        # A start that Azure refuses is expected to make no run, and one that got no answer may
        # have made one: the sentence is the same for both, and sends the reader to the executions.
        # So may one whose answer, or the list read after it, came in a shape this script does not
        # read: that is said in words of this script's, and nothing of the answer is shown.
        failed = (f"Azure refused or failed a request before the run it made was known: {str(error).rstrip('.')}"
                  if isinstance(error, AzError)
                  else 'an answer was not in the shape this script reads before the run it made was known')
        raise RuntimeError(f'The job {POOL_JOB} was asked to start once, and {failed}. '
                           'The start is not sent again. Whether a run began is read from the '
                           "job's executions, before any second start (infra/README.md, \"When "
                           'something fails").') from None

    # As for a migration, the name and each status are printed only in the shape expected.
    label = told_name(name)
    say(f'Pool run {label} started.')
    how_it_ends = f' How it ends is read with `python infra/deploy.py --pool-log {name}`.' if named(name) else ''
    waited = timeout + 120
    deadline = time.monotonic() + waited
    seen = object()
    while time.monotonic() < deadline:
        try:
            match = [e for e in executions(job_id) if e['name'] == name]
            status = state_of(match[0]) if match else None
        except (AzError, KeyError, TypeError, AttributeError) as error:
            # The start was made. Azure's words alone would not say so, nor that the run may go
            # on: a second start is refused for as long as it is listed as running. Neither would
            # "an answer lacked 'name'", or a traceback, for a list that came in a shape this
            # script does not read: it ends the wait the same way, and nothing of it is shown.
            why = (str(error).rstrip('.') if isinstance(error, AzError)
                   else 'the answer was not the list of executions this script reads')
            raise RuntimeError(f"The pool run was started ({label}), and a read of the job's "
                               f'executions failed while its end was waited for: {why}. '
                               'It may still be in progress, and it was not started again.'
                               f'{how_it_ends}') from None
        if status != seen:
            say(f'Pool run {label}: {told_status(status)}.')
            seen = status
        if status in FINISHED:
            try:
                properties = detailed(job_id, name, match[0], a_failed_read_raises=True)
            except AzError as error:
                # The exit code decides how this command ends. A read of it that Azure refuses or
                # fails is said as that, in Azure's words: "Azure reported no exit code" would be
                # false of it. The verdict's line is then what the polling knew.
                say(verdict(name, match[0].get('properties') or {}, container=JOBS[POOL_JOB],
                            codes=POOL_EXIT_CODES))
                raise RuntimeError(f'The pool run ended ({label}), and the read of its exit code failed: '
                                   f"{str(error).rstrip('.')}. How it ended is not guessed from its status, "
                                   f'and it was not started again. {where_it_printed(name)}') from None
            say(verdict(name, properties, container=JOBS[POOL_JOB], codes=POOL_EXIT_CODES))
            # The status as that line showed it: one of the states this script knows, or words.
            end_pool_run(name, exit_code(properties, JOBS[POOL_JOB])[0], told_status(properties.get('status')))
            return name
        time.sleep(5)
    raise RuntimeError(f'Timed out waiting for {label} of the job {POOL_JOB}: it had not ended '
                       f"{waited} s after it was started here (the job's timeout and two minutes). "
                       'It was not started again. While it is listed as running, a deployment and a '
                       f'second start are refused.{how_it_ends} The owner can stop it: '
                       f'{stop_command(job_id, name)}')


# --- Reading the log workspace: the owner's terminal only ---
# The deployment identity has no right on the workspace, and a public log must never hold this
# text. The three commands sign in as whoever ran `az login`.

# The option that reads each job's lines, and the map its exit code is worded with.
JOB_LOGS = {MIGRATE_JOB: ('--job-log', EXIT_CODES), POOL_JOB: ('--pool-log', POOL_EXIT_CODES)}


def prefix_of(subscription, resource_group):
    if not subscription or not resource_group:
        raise ValueError('AZURE_SUBSCRIPTION_ID and AZURE_RESOURCE_GROUP must be set.')
    return (f'/subscriptions/{urllib.parse.quote(subscription, safe="")}'
            f'/resourceGroups/{urllib.parse.quote(resource_group, safe="")}/providers')


def refuse_in_actions(option, in_actions):
    if in_actions:
        raise ValueError(f'{option} is refused inside GitHub Actions: what the containers printed '
                         'is read by the owner, from a terminal, and never reaches a public log.')


def workspace_id(prefix):
    """Print what the daily cap is doing, then return the ID a query is sent to. The cap comes
    first: a log that stopped taking lines explains an empty answer better than the answer does."""
    try:
        workspace = rest('GET', f'{prefix}/Microsoft.OperationalInsights/workspaces/{WORKSPACE}',
                         api_version=WORKSPACE_API_VERSION)
    except AzError as error:
        if 'ResourceNotFound' in str(error):
            raise RuntimeError(f'There is no log workspace {WORKSPACE} in this resource group: the '
                               'logs are switched off and nothing is kept.') from None
        raise
    properties = workspace.get('properties') or {}
    capping = properties.get('workspaceCapping') or {}
    status = capping.get('dataIngestionStatus')
    reset = moment(capping.get('quotaNextResetTime'))
    say(f"Log workspace {WORKSPACE}: daily cap {capping.get('dailyQuotaGb')} GB, ingestion "
        f"{status}, next reset {stamp(reset) if reset else 'not reported'}.")
    if status == 'OverQuota':
        say('The cap was reached: the workspace takes no line until the reset, so what was printed '
            'since it was reached is not there.')
    try:
        # Parsed and printed again before it becomes part of a URL.
        return str(uuid.UUID(str(properties.get('customerId'))))
    except ValueError:
        raise RuntimeError('The workspace did not report the ID a query is sent to.') from None


def query(customer_id, text, timespan):
    """Rows of a log query, as dictionaries. The query travels in a file, like every body."""
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, 'query.json')
        with open(path, 'w', encoding='utf-8') as request:
            json.dump({'query': text, 'timespan': timespan}, request)
        answer = az('rest', '--method', 'POST', '--url',
                    f'{LOG_QUERY}/v1/workspaces/{customer_id}/query', '--resource', LOG_QUERY,
                    '--body', f'@{path}', what='POST the log query')
    table = (answer.get('tables') or [{}])[0]
    names = [column.get('name') for column in table.get('columns') or []]
    return [dict(zip(names, row)) for row in table.get('rows') or []]


def print_lines(rows, columns):
    for row in rows:
        print(' '.join(str(row.get(column)) for column in columns), flush=True)
    say(f'{len(rows)} line(s).' + (f' Only the first {MAX_LOG_LINES} of the period are shown.'
                                  if len(rows) >= MAX_LOG_LINES else ''))
    if not rows:
        say('No line is not proof that nothing was printed: a line takes minutes to arrive, a new '
            'workspace up to 90 minutes, a run that lasts seconds may leave none, and a capped '
            'workspace takes none until its reset.')


def job_log(subscription, resource_group, execution='', in_actions=False, job=MIGRATE_JOB):
    """Print one execution's verdict, then what that execution printed: the latest one, or the one
    named. Its lines are told from another execution's by ContainerGroupName, which holds the
    execution's name, a hyphen and a suffix. The job's name and a period are not enough: on
    2026-10-03 they also matched the lines of the next execution, which started inside the margin
    (README.md, "Measured on Azure"). The name goes into the query between quotes, so only the
    shape of an execution's name may: one given on the command line in another shape is refused
    before anything is read, and one Azure lists in another shape is never asked for. The query
    with this filter is tested offline and has not been sent to the workspace yet.

    `job` is the migrate job, or the pool job for --pool-log: the executions read, the name in
    the query, the container whose exit code is on the verdict's line and the map that words it
    all follow it. It goes into the query between quotes too, so no other job is ever asked for."""
    if job not in JOB_LOGS:
        raise ValueError('A log is read for the migrate job or for the pool job, and for no other. '
                         'Nothing was read.')
    option, codes = JOB_LOGS[job]
    refuse_in_actions(option, in_actions)
    if execution and not named(execution):
        raise ValueError(f"{option} takes an execution's name: letters, digits and hyphens, 100 at "
                         'most. Nothing was read.')
    prefix = prefix_of(subscription, resource_group)
    job_id = f'{prefix}/Microsoft.App/jobs/{job}'
    customer_id = workspace_id(prefix)
    known = listed(rest('GET', job_id + '/executions', api_version=VERDICT_API_VERSION), 'value')
    if execution:
        chosen = [entry for entry in known if entry.get('name') == execution]
    else:
        known.sort(key=lambda entry: str((entry.get('properties') or {}).get('startTime')))
        chosen = known[-1:]
    if not chosen:
        raise RuntimeError(f'The job {job} has no execution'
                           + (f' named {execution}.' if execution else ' yet.'))
    properties = chosen[0].get('properties') or {}
    say(verdict(chosen[0].get('name'), properties, container=JOBS[job], codes=codes))
    name = named(chosen[0].get('name'))
    if not name:
        raise RuntimeError("The execution's name is not the shape of one, so its lines cannot be "
                           "told from another execution's: no query was sent.")
    began = moment(properties.get('startTime'))
    if not began:
        raise RuntimeError('The execution has no start time: there is no period to read.')
    ended = moment(properties.get('endTime')) or datetime.datetime.now(datetime.timezone.utc)
    # A margin on both sides: a line is stamped by the platform, not by the job. The period
    # bounds the read; the group's name is what keeps another execution's lines out.
    since, until = began - datetime.timedelta(minutes=2), ended + datetime.timedelta(minutes=5)
    rows = query(customer_id,
                 f"ContainerAppConsoleLogs | where JobName == '{job}' "
                 f"| where ContainerGroupName startswith '{name}-' "
                 f'| where TimeGenerated between (datetime({stamp(since)}) .. datetime({stamp(until)})) '
                 f'| order by TimeGenerated asc | take {MAX_LOG_LINES} | project TimeGenerated, Log',
                 f'{stamp(since)}/{stamp(until)}')
    print_lines(rows, ('TimeGenerated', 'Log'))


def app_log(subscription, resource_group, minutes, in_actions=False):
    refuse_in_actions('--app-log', in_actions)
    if not 1 <= minutes <= 1440:
        raise ValueError('--app-log takes a number of minutes between 1 and 1440.')
    customer_id = workspace_id(prefix_of(subscription, resource_group))
    rows = query(customer_id,
                 f"ContainerAppConsoleLogs | where ContainerAppName == '{APP}' "
                 f'| where TimeGenerated > ago({minutes}m) | order by TimeGenerated asc '
                 f'| take {MAX_LOG_LINES} | project TimeGenerated, ContainerName, Log',
                 f'PT{minutes}M')
    print_lines(rows, ('TimeGenerated', 'ContainerName', 'Log'))


def main(arguments=None):
    parser = argparse.ArgumentParser(description='Deploy one commit to the running AzureBank app.')
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--app-only', action='store_true',
                       help='move the app to IMAGE_TAG without touching any job: the road back, '
                            'by hand; refused inside GitHub Actions')
    modes.add_argument('--check', action='store_true',
                       help='read the running app, and with the demo on the pool job, then '
                            'smoke-test the address: nothing is moved; refused inside GitHub Actions')
    modes.add_argument('--pool-run', action='store_true',
                       help='start the pool job once, beside its schedule, and wait for that run: the '
                            'first fill, or a refill by hand; refused inside GitHub Actions')
    modes.add_argument('--pool-log', nargs='?', const='', metavar='EXECUTION',
                       help='print what a run of the pool job printed, from the log workspace: the '
                            'latest execution, or the one named; refused inside GitHub Actions')
    modes.add_argument('--job-log', nargs='?', const='', metavar='EXECUTION',
                       help='print what a migration printed, from the log workspace: the latest '
                            'execution, or the one named; refused inside GitHub Actions')
    modes.add_argument('--app-log', type=int, metavar='MINUTES',
                       help='print what the app printed in the last MINUTES, from the log '
                            'workspace; refused inside GitHub Actions')
    options = parser.parse_args(arguments)
    subscription = os.environ.get('AZURE_SUBSCRIPTION_ID', '')
    resource_group = os.environ.get('AZURE_RESOURCE_GROUP', '')
    in_actions = os.environ.get('GITHUB_ACTIONS') == 'true'
    try:
        if options.check:
            check(subscription, resource_group, in_actions=in_actions)
        elif options.pool_run:
            pool_run(subscription, resource_group, in_actions=in_actions)
        elif options.pool_log is not None:
            job_log(subscription, resource_group, options.pool_log, in_actions=in_actions, job=POOL_JOB)
        elif options.job_log is not None:
            job_log(subscription, resource_group, options.job_log, in_actions=in_actions)
        elif options.app_log is not None:
            app_log(subscription, resource_group, options.app_log, in_actions=in_actions)
        else:
            deploy(subscription, resource_group, os.environ.get('IMAGE_TAG', ''),
                   app_only=options.app_only, in_actions=in_actions,
                   expect_secrets_refused=os.environ.get('EXPECT_SECRETS_REFUSED') == '1')
    except AzError as error:
        raise SystemExit(f'Azure refused or failed a request: {error}')
    except KeyError as error:
        raise SystemExit(f'An answer from Azure lacked {error}.')
    except (RuntimeError, ValueError) as error:
        raise SystemExit(str(error))
    except KeyboardInterrupt:
        # Ctrl+C; a workflow run that is cancelled is expected to arrive the same way. The script
        # stops where it is: it does not put the app back, and it stops nothing it started. Which
        # request was on its way is not known here, so the sentence does not say that nothing
        # was changed.
        raise SystemExit('Interrupted. Nothing is put back and nothing is stopped by this: a request '
                         'that was on its way may have reached Azure, and a job that was started goes '
                         'on. What was moved or started is read, from a terminal, with `python '
                         'infra/deploy.py --check` (the app), `--job-log` (a migration) and '
                         '`--pool-log` (a run of the pool job).')


if __name__ == '__main__':
    main()
