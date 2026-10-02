"""Deploy one commit to the running AzureBank app, or move the app alone by hand.

    python infra/deploy.py              migrate the database, then move the app, then check it
    python infra/deploy.py --app-only   move the app only: the owner's road back, from a terminal

It needs the Azure CLI signed in and on PATH, and three environment variables:
AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP and IMAGE_TAG (the full SHA of a commit whose three
images are published).

A full run, in order:
  1. read the app and the jobs, print what runs now, and refuse to go on if their shape drifted;
  2. move the tools image on the migrate job, run the migration, wait for that exact execution;
  3. move the tools image on every other job;
  4. move both app images in one request and wait for the new revision to be ready;
  5. wait until the revision that ran before has stopped answering, then smoke-test the address.
If the new revision never gets ready, or the smoke test gets a wrong answer, the app is put back
on the template it had at step 1 and the run still fails. The schema is never put back.

Every Azure call is `az rest` on the app or on a job. The deployment identity can reach nothing
else, so it could not follow the status URL of a long-running operation, and this script never
asks it to.
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
# The resolved path, so the same script also runs where az is a .cmd file.
AZ = shutil.which('az') or 'az'
AZ_TIMEOUT = 180
APP = 'azurebank'
MIGRATE_JOB = 'azurebank-migrate'
# Job name -> container name. Every job runs the tools image and moves with the commit: the
# migrate job before the migration, the others only after it succeeded.
JOBS = {MIGRATE_JOB: 'migrate'}
MAX_JOB_TIMEOUT = 840
ACTIVE = {'Running', 'Processing'}
FAILED = {'Failed', 'Stopped', 'Degraded'}
FINISHED = FAILED | {'Succeeded'}
GUID = re.compile(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')

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


class RevisionFailed(RuntimeError):
    """The new revision ended Failed or never became ready: the app is put back."""


class SmokeFailed(RuntimeError):
    """The page, the readiness answer or the sign-in answer was wrong: the app is put back."""


class SmokeUnproven(RuntimeError):
    """The sign-in was only rate limited or unanswered: nothing was proved, nothing is put back."""


def redact(text):
    return GUID.sub('<id>', text or '').strip()[:2000]


def say(message):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%H:%M:%SZ')
    print(f'{stamp} {message}', flush=True)


def short(resource_id):
    """The part of a resource ID that names the resource: no subscription, no resource group."""
    return resource_id.split('/providers/Microsoft.App', 1)[-1]


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


def rest(method, resource_id, body=None):
    url = f'https://management.azure.com{resource_id}?api-version={API_VERSION}'
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
    return {'location': resource['location'], 'properties': {'template': template}}


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
    return [f'{field} is {value!r}' for field, value, wanted in checks if not wanted]


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
    return [f'{field} is {value!r}' for field, value, wanted in checks if not wanted]


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


def stop_hint(job_id, execution):
    """The deployment identity may start the job and read its executions; it may not stop one."""
    match = re.search(r'/resourceGroups/([^/]+)/providers/Microsoft\.App/jobs/([^/]+)$', job_id)
    group, job = match.groups() if match else ('<resource group>', '<job>')
    return ('The deployment identity cannot stop it, and it blocks every later deploy until it '
            f'ends or the owner stops it: az containerapp job stop --name {job} '
            f'--resource-group {group} --job-execution-name {execution}')


def run_migration(job_id, timeout):
    before = executions(job_id)
    for execution in before:
        status = execution['properties'].get('status')
        # A state that is neither running nor finished ("Unknown") blocks only while a run that
        # started then could still be alive.
        if status in ACTIVE or (status not in FINISHED and started_within(execution, timeout + 120)):
            raise RuntimeError(f"Execution {execution['name']} is {status}: a migration may still "
                               f"be running. {stop_hint(job_id, execution['name'])}")
    known = {execution['name'] for execution in before}

    name = rest('POST', job_id + '/start').get('name')
    deadline = time.monotonic() + 60
    while not name and time.monotonic() < deadline:
        # Never start twice: the first request may already have launched the run.
        fresh = [e['name'] for e in executions(job_id) if e['name'] not in known]
        if len(fresh) > 1:
            raise RuntimeError(f'Several new executions appeared ({", ".join(fresh)}); inspect them.')
        name = fresh[0] if fresh else None
        if not name:
            time.sleep(5)
    if not name:
        raise RuntimeError('The start was accepted but no execution appeared; inspect the job '
                           'before deploying again.')

    say(f'Migration execution {name} started.')
    deadline = time.monotonic() + timeout + 120
    seen = object()
    while time.monotonic() < deadline:
        match = [e for e in executions(job_id) if e['name'] == name]
        status = match[0]['properties'].get('status') if match else None
        if status != seen:
            say(f'Execution {name}: {status}.')
            seen = status
        if status == 'Succeeded':
            return name
        if status in FAILED:
            raise RuntimeError(f'The migration did not succeed (execution {name}: {status}). The '
                               'app was not touched. The job now runs the new tools image and some '
                               'migrations may be applied; its log is in the portal, on the '
                               "execution's log stream.")
        time.sleep(5)
    raise RuntimeError(f'Timed out waiting for execution {name}. The app was not touched. '
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
        rest('PATCH', f'{prefix}/jobs/{name}', job_patches[name])
        return wait_job(f'{prefix}/jobs/{name}', {JOBS[name]: tools})

    if not app_only:
        moved = move_job(MIGRATE_JOB)
        assert_shape(f'The job {MIGRATE_JOB}', job_drift(moved), 'after its image moved')
        run_migration(f'{prefix}/jobs/{MIGRATE_JOB}', timeout)
        # Only now: a failed migration must leave the other jobs on the image that matches the
        # schema they run on.
        for name in job_patches:
            if name != MIGRATE_JOB:
                move_job(name)
        say('Migration succeeded; moving both app containers together.')
    else:
        say('Moving both app containers together; no job is touched and no migration runs.')

    rest('PATCH', app_id, app_patch)
    try:
        moved = wait_revision(app_id, app_images, previous)
        assert_shape('The app', app_drift(moved), 'after its images moved')
        wait_inactive(app_id, previous)
        smoke(f'https://{fqdn}')
    except (RevisionFailed, SmokeFailed) as failure:
        say(str(failure))
        diagnose(app_id)
        label = put_back(app_id, app)
        raise RuntimeError(f'{failure} The app was put back to {label}; the schema stays where '
                           'the migration left it.') from failure


def main(arguments=None):
    parser = argparse.ArgumentParser(description='Deploy one commit to the running AzureBank app.')
    parser.add_argument('--app-only', action='store_true',
                        help='move the app to IMAGE_TAG without touching any job: the road back, '
                             'by hand; refused inside GitHub Actions')
    options = parser.parse_args(arguments)
    try:
        deploy(os.environ.get('AZURE_SUBSCRIPTION_ID', ''), os.environ.get('AZURE_RESOURCE_GROUP', ''),
               os.environ.get('IMAGE_TAG', ''), app_only=options.app_only,
               in_actions=os.environ.get('GITHUB_ACTIONS') == 'true',
               expect_secrets_refused=os.environ.get('EXPECT_SECRETS_REFUSED') == '1')
    except AzError as error:
        raise SystemExit(f'Azure refused or failed a request: {error}')
    except KeyError as error:
        raise SystemExit(f'An answer from Azure lacked {error}.')
    except (RuntimeError, ValueError) as error:
        raise SystemExit(str(error))


if __name__ == '__main__':
    main()
