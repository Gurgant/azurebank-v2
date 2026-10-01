"""Resource-scoped deployment after azure/login; requires Azure CLI on PATH."""

import copy
import json
import os
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

API_VERSION = '2025-01-01'


def az(*args):
    result = subprocess.run(
        ['az', *args, '--only-show-errors', '--output', 'json'],
        check=True, capture_output=True, text=True, timeout=180,
    )
    return json.loads(result.stdout) if result.stdout.strip() else {}


def rest(method, resource_id, body=None):
    url = f'https://management.azure.com{resource_id}?api-version={API_VERSION}'
    args = ['rest', '--method', method, '--url', url]
    if body is None:
        return az(*args)
    # No configuration/secrets payload is read or sent. Keep even the template
    # off argv, and remove the temporary request whether the call succeeds or fails.
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, 'request.json')
        with open(path, 'w', encoding='utf-8') as request:
            json.dump(body, request)
        return az(*args, '--body', f'@{path}')


def images(resource):
    return {c['name']: c['image'] for c in resource['properties']['template']['containers']}


def image_patch(resource, desired):
    template = copy.deepcopy(resource['properties']['template'])
    containers = template['containers']
    if len(containers) != len(desired) or set(images(resource)) != set(desired):
        raise RuntimeError('Unexpected container topology; review before deploying.')
    for container in containers:
        container['image'] = desired[container['name']]
    # Azure generates a new suffix. Reusing the current suffix rejects an image change.
    template.pop('revisionSuffix', None)
    return {'location': resource['location'], 'properties': {'template': template}}


def wait_resource(resource_id, desired, timeout=900, expected_revision=None):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        resource = rest('GET', resource_id)
        props = resource['properties']
        state = props.get('provisioningState')
        if state in {'Failed', 'Canceled', 'Cancelled'}:
            raise RuntimeError(f'Resource provisioning ended with {state}.')
        matching = images(resource) == desired
        ready = (props.get('latestRevisionName') == expected_revision
                 and props.get('latestReadyRevisionName') == expected_revision)
        if state == 'Succeeded' and matching and (ready or expected_revision is None):
            return resource
        time.sleep(5)
    raise RuntimeError('Timed out waiting for the requested images/revision to become ready.')


def execution_list(resource_group):
    return az('containerapp', 'job', 'execution', 'list', '--name', 'azurebank-migrate',
              '--resource-group', resource_group)


def run_migration(resource_group, timeout):
    # A cancelled workflow can leave a server-side job running. Do not overlap it.
    for execution in execution_list(resource_group):
        if execution['properties'].get('status') not in {'Succeeded', 'Failed', 'Stopped'}:
            raise RuntimeError('A migration execution is still active or has unknown status.')
    execution = az('containerapp', 'job', 'start', '--name', 'azurebank-migrate',
                   '--resource-group', resource_group)
    name = execution.get('name')
    if not name:
        # Never repeat start: the first request might already have launched the job.
        raise RuntimeError('Start returned no execution name; inspect Azure before retrying.')
    print(f'Waiting for migration execution {name}.', flush=True)
    deadline = time.monotonic() + timeout + 120
    while time.monotonic() < deadline:
        # Select this exact execution, never the latest successful historical run.
        matches = [item for item in execution_list(resource_group) if item['name'] == name]
        if matches:
            status = matches[0]['properties'].get('status')
            if status == 'Succeeded':
                return
            if status not in {'Running', 'Processing', 'Pending'}:
                raise RuntimeError(f'Migration did not succeed (status: {status}).')
        time.sleep(5)
    raise RuntimeError('Migration polling timed out; inspect/stop the execution before retrying.')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def smoke(url, timeout=300):
    opener = urllib.request.build_opener(NoRedirect)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with opener.open(url + '/', timeout=20) as response:
                body = response.read(2_000_000).decode('utf-8')
                spa_ok = (
                    response.status == 200
                    and response.headers.get_content_type() == 'text/html'
                    and '<!doctype html>' in body.lower()
                    and '<title>AzureBank</title>' in body
                    and 'id="root"' in body
                    and re.search(r'<script\b[^>]*src="/assets/[^\"]+\.js"', body)
                )
            with opener.open(url + '/health/ready', timeout=20) as response:
                ready_ok = response.status == 200
            if spa_ok and ready_ok:
                print(f'Smoke passed: {url}/ serves the SPA; /health/ready returns 200.')
                return
        except (urllib.error.URLError, TimeoutError, UnicodeError):
            pass
        time.sleep(5)
    raise RuntimeError('Smoke test failed: expected SPA HTML and readiness HTTP 200.')


def deploy(subscription, resource_group, tag):
    if not re.fullmatch(r'[0-9a-f]{40}', tag):
        raise ValueError('IMAGE_TAG must be a full lowercase commit SHA.')
    if not subscription or not resource_group:
        raise ValueError('AZURE_SUBSCRIPTION_ID and AZURE_RESOURCE_GROUP must be set.')
    prefix = (f'/subscriptions/{urllib.parse.quote(subscription, safe="")}'
              f'/resourceGroups/{urllib.parse.quote(resource_group, safe="")}/providers/Microsoft.App')
    job_id = prefix + '/jobs/azurebank-migrate'
    app_id = prefix + '/containerApps/azurebank'
    job_images = {'migrate': f'ghcr.io/gurgant/azurebank-tools:{tag}'}
    app_images = {name: f'ghcr.io/gurgant/azurebank-{name}:{tag}' for name in ('bff', 'api')}

    # Read both before mutating anything: bootstrap must already have created them.
    app = rest('GET', app_id)
    job = rest('GET', job_id)
    app_patch = image_patch(app, app_images)
    # A distinct suffix also prevents a healthy previous revision passing this run's gate.
    suffix = f'd-{tag[:12]}-{uuid.uuid4().hex[:8]}'
    app_patch['properties']['template']['revisionSuffix'] = suffix
    job_patch = image_patch(job, job_images)
    timeout = job['properties']['configuration']['replicaTimeout']
    if not 1 <= timeout <= 1800:
        raise RuntimeError('Migration timeout is outside the supported workflow budget.')
    print('Updating migration image.', flush=True)
    rest('PATCH', job_id, job_patch)
    wait_resource(job_id, job_images)
    run_migration(resource_group, timeout)

    print('Migration succeeded; updating both application containers together.', flush=True)
    rest('PATCH', app_id, app_patch)
    app = wait_resource(app_id, app_images, expected_revision=f'azurebank--{suffix}')
    fqdn = app['properties']['configuration']['ingress']['fqdn']
    if not re.fullmatch(r'[a-z0-9.-]+', fqdn):
        raise RuntimeError('Unexpected application FQDN.')
    smoke(f'https://{fqdn}')


if __name__ == '__main__':
    try:
        deploy(os.environ.get('AZURE_SUBSCRIPTION_ID', ''),
               os.environ.get('AZURE_RESOURCE_GROUP', ''), os.environ.get('IMAGE_TAG', ''))
    except subprocess.CalledProcessError:
        # Do not echo a potentially sensitive Azure response or request body.
        raise SystemExit('Azure CLI request failed; inspect the Azure operation before retrying.')
    except (RuntimeError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        raise SystemExit(str(error))
