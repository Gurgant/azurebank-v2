"""Deploy one commit to the running AzureBank app, or move the app alone by hand.

    python infra/deploy.py                    migrate the database, then move the app, then check it
    python infra/deploy.py --app-only         move the app only: the owner's road back
    python infra/deploy.py --job-log [NAME]   print what a migration printed (the latest one, or NAME)
    python infra/deploy.py --app-log MINUTES  print what the app printed in the last MINUTES

The last three are for the owner's terminal and are refused inside GitHub Actions.

It needs the Azure CLI signed in and on PATH, and the environment variables AZURE_SUBSCRIPTION_ID
and AZURE_RESOURCE_GROUP. A deployment also needs IMAGE_TAG (the full SHA of a commit whose three
images are published).

A full run, in order:
  1. read the app and the jobs, print what runs now, and refuse to go on if their shape drifted;
  2. move the tools image on the migrate job, run the migration, wait for that exact execution;
  3. move the tools image on every other job;
  4. move both app images in one request, wait for the new revision to be ready, and read the
     app's shape again;
  5. wait until the revision that ran before has stopped answering, then smoke-test the address.
If the new revision never gets ready, the app's shape has drifted when it is read again, or the
smoke test gets a wrong answer, the script tries to put the app back on the template it had at
step 1, and the run still fails. If that put-back fails too, the run says so: the app may then be
serving a broken revision. The schema is never put back.

A migration leaves one line here, its verdict: the execution's name, status, times, exit code and
a one-word reason. What it printed is never fetched by a deployment: the log of a public
repository is public, and that text can name the server, an address, or a value from a database
error. It is kept in the log workspace, where --job-log reads it as the owner.

In a deployment every Azure call is `az rest` on the app or on a job. The deployment identity can
reach nothing else, so it could not follow the status URL of a long-running operation, and this
script never asks it to.
"""

import argparse
import copy
import datetime
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
# What each signs in to the database as: the one user-assigned identity it may carry.
APP_IDENTITY = 'azurebank-app'
MIGRATE_IDENTITY = 'azurebank-migrate'
WORKSPACE = 'azurebank-logs'
LOG_QUERY = 'https://api.loganalytics.io'
MAX_LOG_LINES = 5000
# Job name -> container name. Every job runs the tools image and moves with the commit: the
# migrate job before the migration, the others only after it succeeded.
JOBS = {MIGRATE_JOB: 'migrate'}
MAX_JOB_TIMEOUT = 840
ACTIVE = {'Running', 'Processing'}
FAILED = {'Failed', 'Stopped', 'Degraded'}
FINISHED = FAILED | {'Succeeded'}
STATUSES = ACTIVE | FINISHED | {'Unknown'}
GUID = re.compile(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
IPV4 = re.compile(r'\b\d{1,3}(?:\.\d{1,3}){3}\b')
# The shape of an execution's name. A name of any other shape is never printed.
EXECUTION_NAME = re.compile(r'[A-Za-z0-9-]{1,100}')
# What the tools image exits with (backend/tools/AzureBank.Seeder/Commands/ExitCodes.cs).
EXIT_CODES = {0: 'done', 1: 'failed after it reached for the server; running it again is safe',
              2: 'refused before any connection; the configuration must change'}

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
    """The page, the readiness answer or the sign-in answer was wrong: the app is put back."""


class SmokeUnproven(RuntimeError):
    """The sign-in was only rate limited or unanswered: nothing was proved, nothing is put back."""


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


def assert_shape(what, drift, when):
    if drift:
        raise ShapeError(f'{what} is not in the shape this script deploys onto ({when}): '
                         f'{"; ".join(drift)}. Put it right with the template (infra/README.md) '
                         'before deploying.')


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
    may be public, and this script has read no answer of Azure's yet: a field is printed only in
    the shape expected of it."""
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


def stop_hint(job_id, execution):
    """The deployment identity may start the job and read its executions; it may not stop one."""
    match = re.search(r'/resourceGroups/([^/]+)/providers/Microsoft\.App/jobs/([^/]+)$', job_id)
    group, job = match.groups() if match else ('<resource group>', '<job>')
    return ('The deployment identity cannot stop it, and it blocks every later deploy until it '
            f'ends or the owner stops it: az containerapp job stop --name {job} '
            f"--resource-group {group} --job-execution-name {named(execution) or '<its name>'}")


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


def verdict(name, properties, in_actions=False):
    """One line about an execution: name, status, times, exit code, reason. Every field is checked
    for its shape before it is printed, because inside GitHub Actions the line is public. The
    fields were seen filled once, by hand, for a throwaway job (README.md, "Measured on Azure");
    this script has not read them from Azure itself. There, a reason that is not one plain word is
    withheld, and Azure's message is not printed at all."""
    parts = [f"{told_name(name)}: {told_status(properties.get('status'))}"]
    began, ended = moment(properties.get('startTime')), moment(properties.get('endTime'))
    parts.append(f'started {stamp(began)}' if began else 'start not reported')
    if began and ended:
        parts.append(f'ended {stamp(ended)} ({round((ended - began).total_seconds())} s)')
    else:
        parts.append(f'ended {stamp(ended)}' if ended else 'end not reported')
    code, entry = exit_code(properties, JOBS[MIGRATE_JOB])
    meaning = EXIT_CODES.get(code, 'not a code the tool itself exits with')
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


def report_verdict(job_id, name, known, in_actions):
    """Read the execution once more, with the version that carries its exit code, and print its
    verdict. Never raises: if that read fails or lacks the execution, the verdict is what the
    polling already knew, and "Failed" is still a verdict."""
    properties = (known or {}).get('properties') or {}
    try:
        answer = rest('GET', job_id + '/executions', api_version=VERDICT_API_VERSION)
        for execution in listed(answer, 'value'):
            if execution.get('name') == name and isinstance(execution.get('properties'), dict):
                properties = execution['properties']
    except (AzError, AttributeError):
        pass
    line = verdict(name, properties, in_actions)
    say(line)
    summary = os.environ.get('GITHUB_STEP_SUMMARY') if in_actions else None
    if summary:
        # The same line on the run's summary page. A summary that cannot be written fails nothing.
        try:
            with open(summary, 'a', encoding='utf-8') as page:
                page.write(f'Migration: {line}\n')
        except OSError:
            pass


def run_migration(job_id, timeout, in_actions=False):
    before = executions(job_id)
    for execution in before:
        status = state_of(execution)
        # A state that is neither running nor finished ("Unknown") blocks only while a run that
        # started then could still be alive.
        if status in ACTIVE or (status not in FINISHED and started_within(execution, timeout + 120)):
            state = told_status(status, 'in a state this script does not know')
            raise RuntimeError(f"Execution {named(execution['name']) or 'whose name is withheld'} "
                               f"is {state}: a migration may still be running. "
                               f"{stop_hint(job_id, execution['name'])}")
    known = {execution['name'] for execution in before}

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
                           'before deploying again.')

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


def told(answer, text=False):
    """An answer as a failure message shows it: the status, and the text when it says why."""
    status, _, body = answer
    return f'{status} {body[:80]!r}' if text or status == 'no answer' else str(status)


def smoke(url, timeout=300):
    opener = urllib.request.build_opener(NoRedirect)
    deadline = time.monotonic() + timeout
    while True:
        page = ready = ('not asked', None, '')
        try:
            page = fetch(opener, url + '/')
            ready = fetch(opener, url + '/health/ready')
            # "Healthy" and not just 200: the BFF answers 200 "Degraded" when the API is down.
            if is_spa(*page) and ready[0] == 200 and ready[2].strip() == 'Healthy':
                break
        except NO_ANSWER as error:
            # A dropped connection is one more wrong answer of this loop, never the end of the run.
            silence = ('no answer', None, f'{type(error).__name__}: {error}')
            page, ready = (silence, ready) if page[0] == 'not asked' else (page, silence)
        if time.monotonic() >= deadline:
            raise SmokeFailed('Smoke test failed: the page or the readiness answer was wrong '
                              f'after {timeout} s (/ -> {told(page)}, /health/ready -> '
                              f'{told(ready, text=True)}).')
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
            say(f'Smoke passed: {url}/ is the SPA, /health/ready is Healthy, and a sign-in for an '
                'unknown address was refused by the API after it asked the database.')
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
    jobs = {} if app_only else {name: rest('GET', f'{prefix}/jobs/{name}') for name in JOBS}
    say(running_now(app, jobs))
    assert_shape('The app', app_drift(app), 'nothing was changed')
    if not app_only:
        assert_shape(f'The job {MIGRATE_JOB}', job_drift(jobs[MIGRATE_JOB]), 'nothing was changed')
        timeout = jobs[MIGRATE_JOB]['properties']['configuration']['replicaTimeout']
        if not 1 <= timeout <= MAX_JOB_TIMEOUT:
            raise RuntimeError('The migrate job timeout is outside what this workflow waits for.')
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
        # Only now: a failed migration must leave the other jobs on the image that matches the
        # schema they run on.
        for name in job_patches:
            if name != MIGRATE_JOB:
                move_job(name)
        say('Migration succeeded; moving both app containers together.')
    else:
        say('Moving both app containers together; no job is touched and no migration runs.')

    patch(app_id, app_patch, 'the app')
    try:
        moved = wait_revision(app_id, app_images, previous)
        assert_shape('The app', app_drift(moved), 'after its images moved')
        wait_inactive(app_id, previous)
        smoke(f'https://{fqdn}')
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


# --- Reading the log workspace: the owner's terminal only ---
# The deployment identity has no right on the workspace, and a public log must never hold this
# text. Both commands sign in as whoever ran `az login`.

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


def job_log(subscription, resource_group, execution='', in_actions=False):
    refuse_in_actions('--job-log', in_actions)
    prefix = prefix_of(subscription, resource_group)
    job_id = f'{prefix}/Microsoft.App/jobs/{MIGRATE_JOB}'
    customer_id = workspace_id(prefix)
    known = listed(rest('GET', job_id + '/executions', api_version=VERDICT_API_VERSION), 'value')
    if execution:
        chosen = [entry for entry in known if entry.get('name') == execution]
    else:
        known.sort(key=lambda entry: str((entry.get('properties') or {}).get('startTime')))
        chosen = known[-1:]
    if not chosen:
        raise RuntimeError(f'The job {MIGRATE_JOB} has no execution'
                           + (f' named {execution}.' if execution else ' yet.'))
    properties = chosen[0].get('properties') or {}
    say(verdict(chosen[0].get('name'), properties))
    began = moment(properties.get('startTime'))
    if not began:
        raise RuntimeError('The execution has no start time: there is no period to read.')
    ended = moment(properties.get('endTime')) or datetime.datetime.now(datetime.timezone.utc)
    # A margin on both sides: a line is stamped by the platform, not by the job.
    since, until = began - datetime.timedelta(minutes=2), ended + datetime.timedelta(minutes=5)
    rows = query(customer_id,
                 f"ContainerAppConsoleLogs | where JobName == '{MIGRATE_JOB}' "
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
        if options.job_log is not None:
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


if __name__ == '__main__':
    main()
