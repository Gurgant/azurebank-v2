"""Offline tests of the deployment script's decisions.

Every Azure and HTTP answer below is invented here. The tests prove what the script does with an
answer; what Azure and the app really answer is read on the first deployment (README.md, "Not
measured yet"). Time is a counter: a wait of fifteen minutes costs nothing.
"""

import contextlib
import copy
import datetime
from email.message import Message
import http.client
import io
import json
import os
import re
import socket
import struct
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import MagicMock, patch
import urllib.error
import urllib.parse

import deploy

SUBSCRIPTION = 'subscription-placeholder'
GROUP = 'group'
PREFIX = f'/subscriptions/{SUBSCRIPTION}/resourceGroups/{GROUP}/providers/Microsoft.App'
APP_ID = f'{PREFIX}/containerApps/azurebank'
MIGRATE_ID = f'{PREFIX}/jobs/azurebank-migrate'
POOL_ID = f'{PREFIX}/jobs/azurebank-pool'
OLD = 'b' * 40
NEW = 'a' * 40
ADDRESS = 'azurebank.example.invalid'
BEFORE = 'azurebank--before'
IDENTITY_IDS = ('aaaaaaaa-1111-4222-8333-444444444444', 'bbbbbbbb-5555-4666-8777-888888888888')
# What a listing of secrets answers in these tests: the nine secrets infra/main.bicep gives the
# app, and the two it gives the pool job from the same two expressions. Every value is planted:
# no line and no error may hold one, or eight characters in a row of one.
PEPPER = 'Kq7vX2mPz9LwTn4RbY6cJd8HsF3gA1eU'
CONNECTION = 'Zx5nQw8rVt2yBu6iMo3pLa9sDk4fGj7h;Hc1eXz0vNb5mQp8wRt3y'
SECRETS_OF_THE_APP = {
    'app-connection': CONNECTION,
    'jwt-secret': 'Wd4kTy7uIo1pAs6dFg9hJk2lZx5cVb8n',
    'idempotency-hash-key': 'Mn3bVc6xZa9sDf2gHj5kLq8wEr1tYu4i',
    'stepup-binding-key': 'Pl0oKi9jUh8yGt7fRd6eSw5aQz4xCv3b',
    'service-key': 'Yt6rEw3qAs9dFg2hJk5lZx8cVb1nMq4w',
    'audit-chain-key': 'Gh2jKl5zXc8vBn1mQw4eRt7yUi0oPa3s',
    'audit-anchor-key': 'Bv7cXz4aSd1fGh8jKl5qWe2rTy9uIo6p',
    'pin-pepper': PEPPER,
    'demo-client-key': 'Nm9bVc2xZl5kJh8gFd1sAp4oIu7yTr0e',
}
SECRETS_OF_THE_POOL_JOB = {'app-connection': CONNECTION, 'pin-pepper': PEPPER}


def container(name, image):
    return {'name': name, 'image': image, 'env': [{'name': 'KEY', 'secretRef': 'key'}],
            'resources': {'cpu': 0.25, 'memory': '0.5Gi'}}


def identity(name):
    """The identity block as Azure returns it: the key holds the subscription, the entry two IDs."""
    return {'type': 'UserAssigned', 'userAssignedIdentities': {
        f'/subscriptions/{SUBSCRIPTION}/resourcegroups/{GROUP}/providers/Microsoft.ManagedIdentity'
        f'/userAssignedIdentities/{name}': {'principalId': IDENTITY_IDS[0], 'clientId': IDENTITY_IDS[1]}}}


def app_resource(tag=OLD):
    return {
        'location': 'italynorth',
        'identity': identity('azurebank-app'),
        'properties': {
            'provisioningState': 'Succeeded',
            'latestRevisionName': BEFORE,
            'latestReadyRevisionName': BEFORE,
            'configuration': {
                'activeRevisionsMode': 'Single',
                'ingress': {'external': True, 'targetPort': 8080, 'allowInsecure': False,
                            'transport': 'Auto', 'fqdn': ADDRESS},
            },
            'template': {
                'revisionSuffix': 'before',
                'scale': {'minReplicas': 0, 'maxReplicas': 1},
                'containers': [container(name, f'ghcr.io/gurgant/azurebank-{name}:{tag}')
                               for name in ('bff', 'api')],
            },
        },
    }


def job_resource(name='migrate', tag=OLD):
    return {
        'location': 'italynorth',
        'identity': identity('azurebank-migrate'),
        'properties': {
            'provisioningState': 'Succeeded',
            'configuration': {
                'triggerType': 'Manual', 'replicaRetryLimit': 0, 'replicaTimeout': 600,
                'manualTriggerConfig': {'parallelism': 1, 'replicaCompletionCount': 1},
            },
            'template': {'containers': [container(name, f'ghcr.io/gurgant/azurebank-tools:{tag}')]},
        },
    }


def pool_resource(tag=OLD):
    """The pool job as infra/main.bicep writes it: on a schedule, one run at a time, one container
    that runs `recycle`, and the app's database identity."""
    return {
        'location': 'italynorth',
        'identity': identity('azurebank-app'),
        'properties': {
            'provisioningState': 'Succeeded',
            'configuration': {
                'triggerType': 'Schedule', 'replicaRetryLimit': 0, 'replicaTimeout': 600,
                'scheduleTriggerConfig': {'cronExpression': '0 */4 * * *', 'parallelism': 1,
                                          'replicaCompletionCount': 1},
            },
            'template': {'containers': [
                {**container('pool', f'ghcr.io/gurgant/azurebank-tools:{tag}'), 'args': ['recycle']}]},
        },
    }


def flagged(bff=None, api=None, name='Demo__Enabled'):
    """An app whose two containers carry the demo's setting as given: a plain value, a list of
    whole entries, or None for a container that does not carry the setting."""
    app = app_resource()
    for entry, value in zip(app['properties']['template']['containers'], (bff, api)):
        if isinstance(value, list):
            entry['env'] = entry['env'] + copy.deepcopy(value)
        elif value is not None:
            entry['env'] = entry['env'] + [{'name': name, 'value': value}]
    return app


def resource(names):
    """A bare resource for the tests of one function."""
    return {
        'location': 'italynorth',
        'properties': {
            'provisioningState': 'Succeeded',
            'configuration': {'replicaTimeout': 600},
            'template': {
                'revisionSuffix': 'previous',
                'scale': {'minReplicas': 0, 'maxReplicas': 1},
                'containers': [container(name, 'previous') for name in names],
            },
        },
    }


def raise_(error):
    raise error


def execution(name, status, started=None, **more):
    properties = {'status': status, **more}
    if started:
        properties['startTime'] = started
    return {'name': name, 'properties': properties}


def finished(name='run', status='Succeeded', code=0, reason='Completed', container='migrate', **more):
    """An execution as the later API version describes it once it has ended."""
    return execution(name, status, '2026-10-02T18:00:03.1234567Z', endTime='2026-10-02T18:00:09Z',
                     reason=reason, detailedStatus={'replicas': [{'name': f'{name}-abcde', 'containers': [
                         {'name': container, 'status': status, 'code': code}]}]}, **more)


def pool_finished(name='pool-run', status='Succeeded', code=0, **more):
    """A run of the pool job as the later API version describes it once it has ended: its one
    container is `pool`."""
    return finished(name, status, code, container='pool', **more)


def ends_as(run):
    """How a run of the pool job that a test starts reads, one read after the other: first as the
    polling sees it end, a name and a status and no exit code, then as the later API version
    describes it."""
    return [execution(run['name'], run['properties']['status']), run]


class Clock:
    """Stands in for time.sleep and time.monotonic: sleeping is what moves the clock."""

    def __init__(self):
        self.now = 0.0
        self.sleeps = []

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.now += seconds


class FakeAzure:
    """What `deploy.rest` talks to: one app, its revisions, replicas and secrets, and the jobs."""

    def __init__(self, out):
        self.out = out
        self.app = app_resource()
        self.jobs = {'azurebank-migrate': job_resource()}
        self.revisions = {BEFORE: {'active': True, 'provisioningState': 'Provisioned',
                                   'runningState': 'Running', 'healthState': 'Healthy'}}
        # True: the list of the app's revisions comes with a link to a next page.
        self.more_revisions = False
        # What a listing of secrets answers, for the app and for the pool job: every value is
        # planted, and no test may find one, or a part of one, in a line or in an error.
        self.secrets = {APP_ID: dict(SECRETS_OF_THE_APP), POOL_ID: dict(SECRETS_OF_THE_POOL_JOB)}
        self.replicas = [{'name': 'azurebank--replica-1', 'properties': {'containers': [
            {'name': 'bff', 'ready': False, 'started': False, 'restartCount': 7,
             'runningState': 'Waiting', 'runningStateDetails': 'CrashLoopBackOff'}]}}]
        # What happens to a revision, by the first letter of its suffix: d is a deploy, b a put-back.
        self.fate = {'d': 'ready', 'b': 'ready'}
        self.keep_old_active = False
        self.reads_before_old_is_inactive = 0
        self.drift_app_on_patch = None
        self.drift_job_on_patch = None
        self.refuse = None
        self.refusal = 'Forbidden: AuthorizationFailed'
        # None: the migration is a stand-in. A list: the job runs here, and ends as `outcome`.
        self.executions = None
        self.outcome = finished('this-run')
        # The executions of the pool job, once there is one. A deployment only ever reads them: it
        # moves that job and never starts it, and a start is an unexpected call while `pool_outcome`
        # is None. A list: what a start by hand makes, as that run reads one read after the other
        # (the last entry stays). `start_names_the_run` False: the start's answer carries no name.
        self.pool_runs = []
        self.pool_outcome = None
        self.pool_to_come = []
        self.start_names_the_run = True
        self.calls = []
        self.versions = []
        self.events = []
        self.printed_before_first_write = None

    def turn_the_demo_on(self):
        """What a run of the template with `demo` true writes: the flag on both containers of the
        app, and the pool job."""
        self.app = flagged('true', 'true')
        self.jobs['azurebank-pool'] = pool_resource()

    def a_new_revision_is_ready(self, name='azurebank--after'):
        """What a run of the template leaves when it made a revision: a new one that is the latest
        and the latest ready one. The one before stays active for as long as these tests say
        (keep_old_active, reads_before_old_is_inactive)."""
        self.revisions[name] = {'active': True, 'provisioningState': 'Provisioned',
                                'runningState': 'Running', 'healthState': 'Healthy'}
        self.app['properties'].update(latestRevisionName=name, latestReadyRevisionName=name)
        if not self.keep_old_active and not self.reads_before_old_is_inactive:
            self.deactivate_all_but(name)
        return name

    def writes(self):
        return [(method, resource_id) for method, resource_id, _ in self.calls if method != 'GET']

    def app_patches(self):
        return [body for method, resource_id, body in self.calls
                if method == 'PATCH' and resource_id == APP_ID]

    def rest(self, method, resource_id, body=None, api_version=deploy.API_VERSION):
        self.calls.append((method, resource_id, copy.deepcopy(body)))
        self.versions.append((method, resource_id.rsplit('/', 1)[-1], api_version))
        if method != 'GET' and self.printed_before_first_write is None:
            self.printed_before_first_write = self.out.getvalue()
        if self.refuse and self.refuse(method, resource_id, body):
            raise deploy.AzError(self.refusal)
        if resource_id == APP_ID:
            return self.app_call(method, body)
        if (method, resource_id) == ('GET', APP_ID + '/revisions'):
            return self.revisions_call()
        if resource_id.startswith(APP_ID + '/revisions/'):
            return self.revision_call(resource_id[len(APP_ID + '/revisions/'):])
        if method == 'POST' and resource_id in (APP_ID + '/listSecrets', POOL_ID + '/listSecrets'):
            return self.secrets_call(resource_id[:-len('/listSecrets')], body)
        if resource_id.startswith(PREFIX + '/jobs/'):
            return self.job_call(method, resource_id[len(PREFIX + '/jobs/'):], body)
        raise AssertionError(f'unexpected call: {method} {resource_id}')

    def app_call(self, method, body):
        if method == 'GET':
            return copy.deepcopy(self.app)
        assert method == 'PATCH', method
        assert set(body) == {'location', 'properties'} and set(body['properties']) == {'template'}, body
        properties = self.app['properties']
        template = copy.deepcopy(body['properties']['template'])
        suffix = template['revisionSuffix']
        name = f'azurebank--{suffix}'
        fate = self.fate[suffix[0]]
        if self.drift_app_on_patch:
            self.drift_app_on_patch(template)
        properties['template'] = template
        properties['latestRevisionName'] = name
        properties['provisioningState'] = 'Failed' if fate == 'failed' else 'Succeeded'
        self.revisions[name] = {
            'active': fate == 'ready',
            'provisioningState': 'Provisioned' if fate == 'ready' else 'Provisioning',
            'runningState': 'Running' if fate == 'ready' else 'Activating',
            'healthState': 'Healthy' if fate == 'ready' else 'Unhealthy',
            'provisioningError': None if fate == 'ready' else 'Container bff failed its startup probe.',
        }
        if fate == 'ready':
            properties['latestReadyRevisionName'] = name
            if not self.keep_old_active and not self.reads_before_old_is_inactive:
                self.deactivate_all_but(name)
        self.events.append(f'app {suffix[0]}')
        return {}

    def deactivate_all_but(self, name):
        for other, state in self.revisions.items():
            if other != name:
                state['active'] = False

    def revision_call(self, tail):
        if tail.endswith('/replicas'):
            return {'value': copy.deepcopy(self.replicas)}
        if tail not in self.revisions:
            raise deploy.AzError('Not Found: ResourceNotFound')
        self.count_a_read_of_the_revisions()
        self.events.append(f'read {tail}')
        return {'name': tail, 'properties': copy.deepcopy(self.revisions[tail])}

    def count_a_read_of_the_revisions(self):
        if self.reads_before_old_is_inactive:
            self.reads_before_old_is_inactive -= 1
            if not self.reads_before_old_is_inactive:
                self.deactivate_all_but(self.app['properties']['latestReadyRevisionName'])

    def revisions_call(self):
        """The list of the app's revisions, each as a read of it alone answers."""
        self.count_a_read_of_the_revisions()
        self.events.append('read revisions')
        answer = {'value': [{'name': name, 'properties': copy.deepcopy(state)}
                            for name, state in self.revisions.items()]}
        if self.more_revisions:
            answer['nextLink'] = f'https://management.azure.com{APP_ID}/revisions?page=2'
        return answer

    def secrets_call(self, resource_id, body):
        """A listing of secrets: a POST that changes nothing, and carries no body."""
        assert body is None, body
        self.events.append(f"read secrets of {resource_id.rsplit('/', 1)[-1]}")
        return {'value': [{'name': name, 'value': value}
                          for name, value in self.secrets[resource_id].items()]}

    def job_call(self, method, tail, body):
        name, _, rest = tail.partition('/')
        if rest and name == 'azurebank-pool':
            return self.pool_call(method, rest)
        if rest and self.executions is not None:
            return self.execution_call(method, rest)
        assert not rest, f'the migration is run by a stand-in in these tests: {tail}'
        if method == 'GET':
            return copy.deepcopy(self.jobs[name])
        assert method == 'PATCH', method
        assert set(body) == {'location', 'properties'} and set(body['properties']) == {'template'}, body
        self.jobs[name]['properties']['template'] = copy.deepcopy(body['properties']['template'])
        if self.drift_job_on_patch:
            self.drift_job_on_patch(self.jobs[name])
        self.events.append(f'job {name}')
        return {}

    def pool_call(self, method, tail):
        """Below the pool job: the list of its executions, and a start by hand when a test says how
        that run goes. Each read of the list moves the run that was started one state on."""
        if (method, tail) == ('POST', 'start') and self.pool_outcome is not None:
            self.pool_to_come = copy.deepcopy(self.pool_outcome)
            self.events.append('pool run')
            if not self.pool_to_come:
                # The start is accepted and no execution ever appears.
                return {}
            self.pool_runs.append(self.pool_to_come.pop(0))
            return {'name': self.pool_runs[-1]['name']} if self.start_names_the_run else {}
        assert (method, tail) == ('GET', 'executions'), f'unexpected call on the pool job: {method} {tail}'
        answer = {'value': copy.deepcopy(self.pool_runs)}
        if self.pool_to_come:
            self.pool_runs[-1] = self.pool_to_come.pop(0)
        return answer

    def execution_call(self, method, tail):
        """A job that runs for real: started once, and finished by the next time it is read."""
        if (method, tail) == ('POST', 'start'):
            self.executions.append(copy.deepcopy(self.outcome))
            self.events.append('migration')
            return {'name': 'this-run'}
        assert (method, tail) == ('GET', 'executions'), f'unexpected call: {method} {tail}'
        return {'value': copy.deepcopy(self.executions)}


class Offline(unittest.TestCase):
    """No clock, no Azure, no network; what the script prints is kept."""

    def setUp(self):
        self.clock = Clock()
        self.start(patch('deploy.time.sleep', self.clock.sleep))
        self.start(patch('deploy.time.monotonic', self.clock.monotonic))
        # A test that reaches the Azure CLI is a broken test: on a machine where az is signed in
        # it would send a real request.
        self.start(patch('deploy.subprocess.run',
                         side_effect=AssertionError('a test tried to start a process')))
        # Under Actions the verdict is also written to the run's summary: never from a test.
        self.start(patch.dict(deploy.os.environ))
        deploy.os.environ.pop('GITHUB_STEP_SUMMARY', None)
        self.out = io.StringIO()
        redirect = contextlib.redirect_stdout(self.out)
        redirect.__enter__()
        self.addCleanup(redirect.__exit__, None, None, None)

    def start(self, patcher):
        mock = patcher.start()
        self.addCleanup(patcher.stop)
        return mock

    def printed(self):
        return self.out.getvalue()

    def clear(self):
        """Forget what was printed so far."""
        self.out.seek(0)
        self.out.truncate()


class DeployCase(Offline):
    """`deploy.deploy` against FakeAzure, with a stand-in for the migration and for the smoke test."""

    def setUp(self):
        super().setUp()
        self.azure = FakeAzure(self.out)
        self.start(patch('deploy.rest', self.azure.rest))
        self.migration = self.start(patch('deploy.run_migration', side_effect=(
            lambda *args, **options: self.azure.events.append('migration'))))
        self.smoke = self.start(patch(
            'deploy.smoke', side_effect=lambda *args, **options: self.azure.events.append('smoke')))

    def deploy(self, **options):
        return deploy.deploy(SUBSCRIPTION, GROUP, NEW, **options)

    def steps(self):
        """The writes, the migration and the smoke test, in the order they happened."""
        return [event for event in self.azure.events if not event.startswith('read ')]


class AzTests(unittest.TestCase):
    @patch('deploy.subprocess.run')
    def test_a_refusal_shows_its_code_and_action_without_identifiers(self, run):
        run.return_value = subprocess.CompletedProcess(
            [], 1, stdout='',
            stderr="Forbidden({\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"The client "
                   "'11111111-2222-3333-4444-555555555555' does not have authorization to perform "
                   "action 'Microsoft.App/containerApps/write' over scope "
                   "'/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourceGroups/x'\"}})")
        with self.assertRaises(deploy.AzError) as raised:
            deploy.az('rest', '--method', 'PATCH')
        message = str(raised.exception)
        self.assertIn('AuthorizationFailed', message)
        self.assertIn('Microsoft.App/containerApps/write', message)
        self.assertNotRegex(message, r'[0-9a-f]{8}-[0-9a-f]{4}-')

    @patch('deploy.subprocess.run')
    def test_a_failure_shows_no_address(self, run):
        run.return_value = subprocess.CompletedProcess(
            [], 1, stdout='', stderr="Bad Request: the caller at 203.0.113.7 was refused; retry from 10.0.0.12:443.")
        with self.assertRaises(deploy.AzError) as raised:
            deploy.az('rest', '--method', 'GET')
        self.assertEqual(str(raised.exception),
                         'Bad Request: the caller at <address> was refused; retry from <address>:443.')

    def test_what_is_hidden_is_an_id_and_an_address_and_nothing_that_only_looks_alike(self):
        self.assertEqual(deploy.redact(' api-version 2025-01-01, 0.25 vCPU, sdk:10.0, 1.2.3 '),
                         'api-version 2025-01-01, 0.25 vCPU, sdk:10.0, 1.2.3')
        self.assertEqual(deploy.redact('at 198.51.100.254, id AAAAAAAA-1111-4222-8333-444444444444'),
                         'at <address>, id <id>')
        self.assertEqual(deploy.redact(None), '')

    @patch('deploy.subprocess.run')
    def test_an_empty_answer_is_an_empty_object(self, run):
        run.return_value = subprocess.CompletedProcess([], 0, stdout='  ', stderr='')
        self.assertEqual(deploy.az('rest'), {})

    @patch('deploy.subprocess.run')
    def test_a_request_body_travels_in_a_file_never_on_the_command_line(self, run):
        run.return_value = subprocess.CompletedProcess([], 0, stdout='{}', stderr='')
        deploy.rest('PATCH', APP_ID, {'properties': {'template': {'marker': 'only-in-the-file'}}})
        command = run.call_args.args[0]
        self.assertNotIn('only-in-the-file', ' '.join(command))
        self.assertTrue(command[command.index('--body') + 1].startswith('@'))
        self.assertEqual(command[1:4], ['rest', '--method', 'PATCH'])

    @patch('deploy.subprocess.run')
    def test_a_timeout_names_the_request_and_never_the_command_line(self, run):
        # Python's own text for a timeout is the whole command, and the URL on it holds the subscription.
        run.side_effect = lambda command, **options: raise_(
            subprocess.TimeoutExpired(command, options['timeout']))
        with self.assertRaises(deploy.AzError) as raised:
            deploy.rest('GET', f'{APP_ID}/revisions/{BEFORE}')
        message = str(raised.exception)
        self.assertIn('no answer in 180 s', message)
        self.assertIn(f'GET /containerApps/azurebank/revisions/{BEFORE}', message)
        for hidden in (SUBSCRIPTION, 'subscriptions', 'management.azure.com'):
            self.assertNotIn(hidden, message)
        self.assertIn(SUBSCRIPTION, ' '.join(run.call_args.args[0]), 'the command line did hold it')

    @patch('deploy.subprocess.run')
    def test_a_timeout_on_a_resource_that_is_not_the_app_names_no_subscription_either(self, run):
        run.side_effect = lambda command, **options: raise_(
            subprocess.TimeoutExpired(command, options['timeout']))
        workspace = (f'/subscriptions/{SUBSCRIPTION}/resourceGroups/{GROUP}/providers'
                     '/Microsoft.OperationalInsights/workspaces/azurebank-logs')
        with self.assertRaises(deploy.AzError) as raised:
            deploy.rest('GET', workspace, api_version='2023-09-01')
        message = str(raised.exception)
        self.assertIn('(GET /Microsoft.OperationalInsights/workspaces/azurebank-logs)', message)
        for hidden in (SUBSCRIPTION, 'subscriptions', 'resourceGroups'):
            self.assertNotIn(hidden, message)

    @patch('deploy.subprocess.run')
    def test_a_request_carries_the_api_version_it_is_given_and_the_usual_one_otherwise(self, run):
        run.return_value = subprocess.CompletedProcess([], 0, stdout='{}', stderr='')
        deploy.rest('GET', APP_ID)
        deploy.rest('GET', f'{MIGRATE_ID}/executions', api_version='2026-07-01')
        urls = [call.args[0][call.args[0].index('--url') + 1] for call in run.call_args_list]
        self.assertEqual(urls, [f'https://management.azure.com{APP_ID}?api-version=2025-01-01',
                                f'https://management.azure.com{MIGRATE_ID}/executions?api-version=2026-07-01'])
        self.assertEqual((deploy.API_VERSION, deploy.VERDICT_API_VERSION), ('2025-01-01', '2026-07-01'))

    @patch('deploy.subprocess.run')
    def test_a_log_query_travels_in_a_file_and_asks_for_the_log_service_by_name(self, run):
        answer = '{"tables":[{"columns":[{"name":"Log"}],"rows":[["a line"],["another"]]}]}'
        run.return_value = subprocess.CompletedProcess([], 0, stdout=answer, stderr='')
        customer = 'cccccccc-9999-4aaa-8bbb-cccccccccccc'
        rows = deploy.query(customer, 'ContainerAppConsoleLogs | take 2', 'PT5M')
        self.assertEqual(rows, [{'Log': 'a line'}, {'Log': 'another'}])
        command = run.call_args.args[0]
        self.assertEqual(command[1:8], ['rest', '--method', 'POST', '--url',
                                        f'https://api.loganalytics.io/v1/workspaces/{customer}/query',
                                        '--resource', 'https://api.loganalytics.io'])
        self.assertTrue(command[command.index('--body') + 1].startswith('@'))
        self.assertNotIn('ContainerAppConsoleLogs', ' '.join(command))
        self.assertEqual(deploy.query(customer, 'x', 'PT5M'), deploy.query(customer, 'y', 'PT5M'))


class PatchTests(unittest.TestCase):
    def test_both_images_change_together_without_mutating_source_or_secrets(self):
        original = resource(['bff', 'api'])
        before = copy.deepcopy(original)
        body = deploy.image_patch(original, {'bff': 'new-bff', 'api': 'new-api'})
        self.assertEqual(original, before)
        self.assertNotIn('configuration', body['properties'])
        self.assertNotIn('revisionSuffix', body['properties']['template'])
        self.assertEqual(deploy.images(body), {'bff': 'new-bff', 'api': 'new-api'})
        self.assertEqual(body['properties']['template']['scale'], {'minReplicas': 0, 'maxReplicas': 1})
        self.assertEqual(body['properties']['template']['containers'][0]['env'],
                         [{'name': 'KEY', 'secretRef': 'key'}])

    def test_unexpected_topology_is_rejected(self):
        with self.assertRaises(RuntimeError):
            deploy.image_patch(resource(['bff']), {'bff': 'new', 'api': 'new'})


class MigrationTests(Offline):
    @patch('deploy.rest', return_value={'name': 'this-run'})
    @patch('deploy.executions')
    def test_waits_for_its_own_execution_not_an_older_success(self, executions, start):
        old = execution('old-run', 'Succeeded')
        executions.side_effect = [[old], [old], [old, execution('this-run', 'Running')],
                                  [old, execution('this-run', 'Succeeded')]]
        self.assertEqual(deploy.run_migration('/job', 600), 'this-run')
        self.assertEqual([call.args for call in start.call_args_list if call.args[0] != 'GET'],
                         [('POST', '/job/start')])

    @patch('deploy.rest', return_value={})
    @patch('deploy.executions')
    def test_a_start_that_returns_no_name_is_found_by_what_is_new(self, executions, start):
        old = execution('old-run', 'Succeeded')
        executions.side_effect = [[old], [old], [old, execution('new-run', 'Running')],
                                  [old, execution('new-run', 'Succeeded')]]
        self.assertEqual(deploy.run_migration('/job', 600), 'new-run')
        self.assertEqual([call.args[0] for call in start.call_args_list].count('POST'), 1)

    @patch('deploy.rest', return_value={})
    @patch('deploy.executions', return_value=[])
    def test_a_start_with_no_execution_fails_and_is_never_repeated(self, executions, start):
        with self.assertRaisesRegex(RuntimeError, 'no execution appeared'):
            deploy.run_migration('/job', 600)
        self.assertEqual(start.call_count, 1)

    def test_a_run_that_ends_any_other_way_fails(self):
        for status in ['Failed', 'Stopped', 'Degraded']:
            with self.subTest(status=status), \
                    patch('deploy.executions', side_effect=[[], [execution('run', status)]]), \
                    patch('deploy.rest', return_value={'name': 'run'}):
                with self.assertRaisesRegex(RuntimeError, status):
                    deploy.run_migration('/job', 600)

    @patch('deploy.rest', return_value={'name': 'run'})
    @patch('deploy.executions')
    def test_unknown_while_running_is_waited_out(self, executions, start):
        executions.side_effect = [[], [execution('run', 'Unknown')], [execution('run', None)],
                                  [execution('run', 'Succeeded')]]
        self.assertEqual(deploy.run_migration('/job', 600), 'run')

    @patch('deploy.rest')
    @patch('deploy.executions', return_value=[execution('previous', 'Running')])
    def test_a_running_execution_blocks_the_start(self, executions, start):
        with self.assertRaisesRegex(RuntimeError, 'previous is Running'):
            deploy.run_migration('/job', 600)
        start.assert_not_called()

    @patch('deploy.rest')
    @patch('deploy.executions', return_value=[execution('previous', 'Running')])
    def test_a_stuck_execution_names_who_can_stop_it_and_how(self, executions, start):
        with self.assertRaises(RuntimeError) as raised:
            deploy.run_migration(MIGRATE_ID, 600)
        message = str(raised.exception)
        self.assertIn('cannot stop it', message)
        self.assertIn('az containerapp job stop --name azurebank-migrate --resource-group group '
                      '--job-execution-name previous', message)

    @patch('deploy.rest')
    def test_an_unknown_execution_blocks_only_while_it_could_be_alive(self, start):
        now = datetime.datetime.now(datetime.timezone.utc)
        recent = (now - datetime.timedelta(seconds=30)).isoformat()
        with patch('deploy.executions', return_value=[execution('recent', 'Unknown', recent)]):
            with self.assertRaisesRegex(RuntimeError, 'recent is Unknown'):
                deploy.run_migration('/job', 600)
        start.assert_not_called()

        stale = (now - datetime.timedelta(days=2)).isoformat().replace('+00:00', 'Z')
        start.return_value = {'name': 'run'}
        with patch('deploy.executions', side_effect=[[execution('stale', 'Unknown', stale)],
                                                    [execution('run', 'Succeeded')]]):
            self.assertEqual(deploy.run_migration('/job', 600), 'run')

    @patch('deploy.rest', return_value={'name': 'run'})
    @patch('deploy.executions', return_value=[])
    def test_polling_timeout_fails_and_names_the_stop_command(self, executions, start):
        with self.assertRaisesRegex(RuntimeError, 'Timed out waiting for execution run') as raised:
            deploy.run_migration(MIGRATE_ID, 600)
        self.assertGreaterEqual(self.clock.now, 720)
        self.assertIn('--job-execution-name run', str(raised.exception))

    def test_the_words_a_migration_shares_with_a_pool_run_are_the_ones_it_had(self):
        # CONTROL: green before this change. A start of the pool job by hand sends its request as
        # a migration sends its own, once, and names the command that stops an execution. What
        # the two share moved into helpers; no word a migration says moved with it, and its start
        # is still looked for during one minute, five seconds apart. Each sentence is held whole.
        stop = ('The deployment identity cannot stop it, and it blocks every later deploy until it ends or '
                'the owner stops it: az containerapp job stop --name azurebank-migrate --resource-group group '
                '--job-execution-name ')
        with patch('deploy.rest') as start, \
                patch('deploy.executions', return_value=[execution('previous', 'Running')]):
            with self.assertRaises(RuntimeError) as raised:
                deploy.run_migration(MIGRATE_ID, 600)
            start.assert_not_called()
        self.assertEqual(str(raised.exception),
                         f'Execution previous is Running: a migration may still be running. {stop}previous')
        with patch('deploy.rest', return_value={}) as start, patch('deploy.executions', return_value=[]):
            with self.assertRaises(RuntimeError) as raised:
                deploy.run_migration(MIGRATE_ID, 600)
            self.assertEqual(start.call_count, 1)
        self.assertEqual(str(raised.exception),
                         'The start was accepted but no execution appeared; inspect the job before deploying again.')
        self.assertEqual(self.clock.sleeps, [5] * 12)
        with patch('deploy.rest', return_value={}), patch('deploy.executions', side_effect=[
                [], [execution('one', 'Running'), execution('two', 'Running')]]):
            with self.assertRaises(RuntimeError) as raised:
                deploy.run_migration(MIGRATE_ID, 600)
        self.assertEqual(str(raised.exception), 'Several new executions appeared (one, two); inspect them.')
        with patch('deploy.rest', return_value={'name': 'run'}), patch('deploy.executions', return_value=[]):
            with self.assertRaises(RuntimeError) as raised:
                deploy.run_migration(MIGRATE_ID, 600)
        self.assertEqual(str(raised.exception),
                         f'Timed out waiting for execution run. The app was not touched. {stop}run')


class InProgressTests(Offline):
    """`deploy.in_progress`: which execution, of a list somebody already read, may still be running.
    It is the rule a migration always had for its own job; a deployment asks it of the pool job."""

    @staticmethod
    def ago(seconds):
        now = datetime.datetime.now(datetime.timezone.utc)
        return (now - datetime.timedelta(seconds=seconds)).isoformat().replace('+00:00', 'Z')

    def test_it_answers_with_the_execution_that_runs_or_may_still_be_alive(self):
        ended = execution('ended', 'Succeeded', self.ago(30))
        for blocking in (execution('running', 'Running'), execution('processing', 'Processing'),
                         # A state that is neither running nor finished, on a run young enough: the
                         # timeout and two minutes.
                         execution('young', 'Unknown', self.ago(700)), execution('no-state', None, self.ago(5)),
                         execution('strange', {'state': 'x'}, self.ago(5)),
                         # No start time, or one nobody can read, is not proof of age.
                         execution('no-start', 'Unknown'), execution('unreadable', 'Unknown', 'yesterday')):
            with self.subTest(blocking=blocking['name']):
                self.assertIs(deploy.in_progress([ended, blocking], 600), blocking)
        first, second = execution('first', 'Running'), execution('second', 'Running')
        self.assertIs(deploy.in_progress([first, second], 600), first)
        # The age is measured against the timeout it is given: 800 s is too old for 600, not for 840.
        old = execution('old', 'Unknown', self.ago(800))
        self.assertIs(deploy.in_progress([old], 840), old)

    def test_an_execution_that_ended_or_is_too_old_to_be_alive_blocks_nothing(self):
        # CONTROL: green before this change: a function that answers None for any list passes it.
        ended = [execution(status.lower(), status, self.ago(30)) for status in ('Succeeded', 'Failed', 'Stopped',
                                                                                'Degraded')]
        self.assertIsNone(deploy.in_progress([], 600))
        self.assertIsNone(deploy.in_progress(ended, 600))
        self.assertIsNone(deploy.in_progress([*ended, execution('old', 'Unknown', self.ago(800))], 600))

    @patch('deploy.rest', side_effect=AssertionError('in_progress read something'))
    def test_it_reads_nothing_and_changes_nothing_of_the_list_it_is_given(self, rest):
        # CONTROL: green before this change. Whoever calls it has read the executions, once: a
        # read of its own would be one more than a migration makes before it starts its job.
        given = [execution('ended', 'Succeeded', self.ago(30)), execution('running', 'Running')]
        before = copy.deepcopy(given)
        deploy.in_progress(given, 600)
        self.assertEqual(given, before)
        rest.assert_not_called()


# What Azure could put in a name or a status and nobody has seen: a second line, and on it a
# command that GitHub Actions would obey.
STRANGE = 'x\n::add-mask::something'


class MigrationLinesTests(Offline):
    """Every line `deploy.run_migration` prints or raises may be public: an execution's name and
    its status are shown only in the shape expected, as the verdict shows them."""

    def assert_nothing_strange(self, text):
        for unwanted in ('\n', '::', 'something'):
            self.assertNotIn(unwanted, text)

    def assert_every_line_is_ours(self):
        for line in self.printed().splitlines():
            self.assertRegex(line, r'^\d\d:\d\d:\d\dZ ', 'a line this script did not begin')
            self.assert_nothing_strange(line)

    def lines(self):
        return [line.split(' ', 1)[1] for line in self.printed().splitlines()]

    @patch('deploy.rest', return_value={'name': 'run'})
    @patch('deploy.executions')
    def test_a_name_and_each_status_are_printed_as_azure_gave_them_when_they_have_the_shape(
            self, executions, start):
        executions.side_effect = [[], [execution('run', 'Running')], [execution('run', 'Succeeded')]]
        self.assertEqual(deploy.run_migration('/job', 600), 'run')
        self.assertEqual(self.lines()[:3], ['Migration execution run started.',
                                            'Migration execution run: Running.',
                                            'Migration execution run: Succeeded.'])

    @patch('deploy.rest', return_value={'name': STRANGE})
    @patch('deploy.executions')
    def test_a_name_with_another_shape_is_never_printed_and_the_run_goes_on(self, executions, start):
        executions.side_effect = [[], [execution(STRANGE, 'Running')], [execution(STRANGE, 'Succeeded')]]
        self.assertEqual(deploy.run_migration('/job', 600), STRANGE)
        self.assert_every_line_is_ours()
        self.assertEqual(self.lines()[:3], ['Migration execution whose name is withheld started.',
                                            'Migration execution whose name is withheld: Running.',
                                            'Migration execution whose name is withheld: Succeeded.'])

    @patch('deploy.rest', return_value={'name': 'run'})
    @patch('deploy.executions')
    def test_a_status_with_another_shape_is_never_printed_and_is_waited_out(self, executions, start):
        # Text on two lines, something that is not text, and a word this script does not know.
        executions.side_effect = [[], [execution('run', STRANGE)], [execution('run', ['Succeeded'])],
                                  [execution('run', 'Pending')], [execution('run', 'Succeeded')]]
        self.assertEqual(deploy.run_migration('/job', 600), 'run')
        self.assert_every_line_is_ours()
        self.assertEqual(self.lines()[:5], ['Migration execution run started.',
                                            'Migration execution run: status not reported.',
                                            'Migration execution run: status not reported.',
                                            'Migration execution run: status not reported.',
                                            'Migration execution run: Succeeded.'])
        self.assertNotIn('Pending', self.printed())

    @patch('deploy.rest')
    def test_an_execution_that_blocks_the_start_is_named_only_in_the_shape_expected(self, start):
        recent = datetime.datetime.now(datetime.timezone.utc).isoformat()
        for blocking in (execution(STRANGE, 'Running'), execution('previous', STRANGE, recent),
                         execution(STRANGE, {'state': STRANGE}, recent)):
            with self.subTest(blocking=blocking), patch('deploy.executions', return_value=[blocking]):
                with self.assertRaises(RuntimeError) as raised:
                    deploy.run_migration(MIGRATE_ID, 600)
                self.assert_nothing_strange(str(raised.exception))
                self.assertIn('a migration may still be running', str(raised.exception))
        self.assertIn('Execution whose name is withheld is in a state this script does not know: ',
                      str(raised.exception))
        self.assertIn('--job-execution-name <its name>', str(raised.exception))
        start.assert_not_called()

    @patch('deploy.rest', return_value={})
    @patch('deploy.executions')
    def test_several_new_executions_are_listed_without_a_name_of_another_shape(self, executions, start):
        executions.side_effect = [[], [execution('one', 'Running'), execution(STRANGE, 'Running')]]
        with self.assertRaises(RuntimeError) as raised:
            deploy.run_migration('/job', 600)
        self.assertIn('Several new executions appeared (one, a name withheld)', str(raised.exception))
        self.assert_nothing_strange(str(raised.exception))

    def test_a_failure_and_a_timeout_withhold_a_name_of_another_shape(self):
        with patch('deploy.rest', return_value={'name': STRANGE}), \
                patch('deploy.executions', side_effect=[[], [execution(STRANGE, 'Failed')]]):
            with self.assertRaises(RuntimeError) as raised:
                deploy.run_migration(MIGRATE_ID, 600)
        self.assertIn('did not succeed (execution whose name is withheld: Failed)', str(raised.exception))
        self.assert_nothing_strange(str(raised.exception))
        with patch('deploy.rest', return_value={'name': STRANGE}), \
                patch('deploy.executions', return_value=[]):
            with self.assertRaises(RuntimeError) as raised:
                deploy.run_migration(MIGRATE_ID, 600)
        self.assertIn('Timed out waiting for execution whose name is withheld.', str(raised.exception))
        self.assertIn('--job-execution-name <its name>', str(raised.exception))
        self.assert_nothing_strange(str(raised.exception))


VERDICT = ('Verdict: execution this-run: Succeeded, started 2026-10-02T18:00:03Z, '
           'ended 2026-10-02T18:00:09Z (6 s), exit code 0 (done), reason Completed.')


def public_verdict(properties, name='run'):
    """The line as a workflow run prints it."""
    return deploy.verdict(name, properties, in_actions=True)


class VerdictTests(unittest.TestCase):
    """`deploy.verdict`: the one line a migration leaves in a log that anybody can read."""

    def test_the_line_names_the_execution_its_status_its_times_its_exit_code_and_its_reason(self):
        self.assertEqual(public_verdict(finished('this-run')['properties'], 'this-run'), VERDICT)

    def test_each_exit_code_of_the_tool_says_what_it_means(self):
        # backend/tools/AzureBank.Seeder/Commands/ExitCodes.cs
        meanings = {0: 'done', 1: 'failed after it reached for the server; running it again is safe',
                    2: 'refused before any connection; the configuration must change',
                    137: 'not a code the tool itself exits with'}
        for code, meaning in meanings.items():
            with self.subTest(code=code):
                line = public_verdict(finished(status='Failed', code=code)['properties'])
                self.assertIn(': Failed, started', line)
                self.assertIn(f', exit code {code} ({meaning}), ', line)

    def test_the_pool_jobs_map_says_done_for_exactly_the_codes_a_pool_run_ends_well_with(self):
        # CONTROL: green as written. `recycle` exits with 0, 1, 2 and 10 to 15
        # (backend/tools/AzureBank.Seeder/Pool/PoolExitCodes.cs; docs/runbooks/demo-pool.md): the
        # words of the map and the set of codes that end a run well are one split written twice,
        # and each is held to the other here. Nothing reads either at a deployment, which never
        # starts the pool job. Seen red with 10 taken out of the set.
        self.assertEqual(sorted(deploy.POOL_EXIT_CODES), [0, 1, 2, 10, 11, 12, 13, 14, 15])
        self.assertEqual({code for code, words in deploy.POOL_EXIT_CODES.items() if words.startswith('done')},
                         deploy.POOL_RUN_ENDS_WELL)
        self.assertEqual({code for code, words in deploy.POOL_EXIT_CODES.items()
                          if words.startswith('needs a look: ')}, {12, 13, 14})

    def test_each_exit_code_of_a_pool_run_says_what_it_means(self):
        # backend/tools/AzureBank.Seeder/Pool/PoolExitCodes.cs; docs/runbooks/demo-pool.md, "The exit
        # code". The line is the owner's: no run of the pool job is started or read inside Actions.
        meanings = {
            0: 'done',
            1: 'did not finish, and left no summary line; read its last line before it is started again',
            2: "refused before anything was opened; the job's configuration must change",
            10: 'done, with a signal: the pool was low (PoolLow)',
            11: 'done, with a signal: the pool was empty (PoolEmpty)',
            12: 'needs a look: a copy could not be built (TopUpIncomplete)',
            13: 'needs a look: a user outside every copy exists (ForeignUsers)',
            14: 'needs a look: a copy could not be deleted (DeleteFailed)',
            15: "done, with a signal: the day's claims held the top-up back (ClaimCeiling)",
            137: 'not a code the tool itself exits with',
        }
        for code, meaning in meanings.items():
            with self.subTest(code=code):
                line = deploy.verdict('run', pool_finished(status='Failed', code=code)['properties'],
                                      container='pool', codes=deploy.POOL_EXIT_CODES)
                self.assertIn(': Failed, started', line)
                self.assertIn(f', exit code {code} ({meaning}), ', line)

    def test_the_exit_code_read_is_the_one_of_the_container_it_is_told(self):
        # An execution with two containers, which nothing in this folder writes: the code read is
        # the one of the container the caller names. With no container named it is the migrate
        # job's, as it was: the second assertion was green before this change.
        replicas = [{'containers': [{'name': 'pool', 'code': 10}, {'name': 'migrate', 'code': 2}]}]
        properties = {**finished()['properties'], 'detailedStatus': {'replicas': replicas}}
        self.assertIn(', exit code 10 (done, with a signal: the pool was low (PoolLow)), ',
                      deploy.verdict('run', properties, container='pool', codes=deploy.POOL_EXIT_CODES))
        self.assertIn(', exit code 2 (refused before any connection; the configuration must change), ',
                      deploy.verdict('run', properties))

    def test_the_pool_jobs_codes_and_the_counts_they_point_at_are_the_seeders_own(self):
        # The script types the numbers and the names of the signals, and for each the count of the
        # run's summary line that it says to read. Here both are read where they are written: the
        # codes in the tool that exits with them, the counts in the line that tool prints. Nothing
        # is built or run. The half about the map's names was green before this change.
        codes = {int(number): name for name, number in re.findall(
            r'public const int (\w+) = (\d+);', seeder_source('Pool', 'PoolExitCodes.cs'))}
        self.assertEqual(sorted(codes), [0, 10, 11, 12, 13, 14, 15], 'ARRANGE: the seven codes were found')
        signals = {number: name for number, name in codes.items() if number}
        self.assertEqual(sorted(deploy.POOL_EXIT_CODES), sorted({0, 1, 2, *signals}))
        for number, name in signals.items():
            self.assertTrue(deploy.POOL_EXIT_CODES[number].endswith(f' ({name})'), deploy.POOL_EXIT_CODES[number])
        (line,) = re.findall(r'public string ToLine\(\) =>(.*?);', seeder_source('Pool', 'PoolRunSummary.cs'), re.S)
        counts = re.findall(r'(\w+)=\{', line)
        self.assertEqual((counts[:2], counts[-1], len(counts)), (['free', 'was'], 'result', 16),
                         'ARRANGE: the counts of the line were found')
        # Each signal points at one count, and at one the line carries.
        self.assertEqual(sorted(deploy.POOL_COUNTS), sorted(signals))
        self.assertEqual([count for count in deploy.POOL_COUNTS.values() if count not in counts], [])

    def test_an_exit_code_azure_did_not_report_is_said_to_be_missing_and_never_guessed(self):
        entry = {'name': 'migrate', 'code': 0}
        for detailed in (
                None, {}, {'replicas': []}, {'replicas': [{'containers': []}]},
                {'replicas': [{'containers': [{'name': 'migrate'}]}]},
                {'replicas': [{'containers': [{'name': 'migrate', 'code': '0'}]}]},
                {'replicas': [{'containers': [{'name': 'migrate', 'code': True}]}]},
                {'replicas': [{'containers': [{'name': 'one', 'code': 0}, {'name': 'two', 'code': 0}]}]},
                {'replicas': [{'containers': [entry]}, {'containers': [entry]}]},
                'text', {'replicas': 'text'}, {'replicas': [None, {'containers': 'text'}]},
                {'replicas': [{'containers': [None, 5]}]}):
            with self.subTest(detailed=detailed):
                properties = {**finished()['properties'], 'detailedStatus': detailed}
                self.assertIn(', exit code not reported, ', public_verdict(properties))

    def test_the_one_container_of_the_job_is_read_whatever_azure_calls_it(self):
        replicas = [{'containers': [{'containerName': 'migrate', 'code': 2}]}]
        properties = {**finished()['properties'], 'detailedStatus': {'replicas': replicas}}
        self.assertIn(', exit code 2 (refused before', public_verdict(properties))

    def test_the_length_of_the_run_is_on_the_line_when_both_times_are(self):
        # About four seconds is a sign-in the database refused; the whole wait is a token that never came.
        properties = finished()['properties']
        properties.update(startTime='2026-10-02T18:00:00Z', endTime='2026-10-02T18:01:01.6Z')
        self.assertIn(', ended 2026-10-02T18:01:01Z (62 s), ', public_verdict(properties))
        del properties['endTime']
        self.assertIn(', started 2026-10-02T18:00:00Z, end not reported, ', public_verdict(properties))

    def test_inside_actions_a_reason_is_printed_only_if_it_is_one_plain_word(self):
        for reason in ('Completed', 'BackoffLimitExceeded', 'a' * 40):
            with self.subTest(reason=reason):
                line = public_verdict(finished(reason=reason)['properties'])
                self.assertTrue(line.endswith(f', reason {reason}.'), line)
        for reason in ('Container migrate exited: azurebank-x.database.windows.net refused 203.0.113.7',
                       'Two words', 'Completed\n', '::error::Completed', 'Completed.', 'a' * 41, 7,
                       ['Completed'], {'code': 'Completed'}):
            with self.subTest(reason=reason):
                line = public_verdict(finished(reason=reason)['properties'])
                self.assertTrue(line.endswith(', reason withheld, read it with --job-log.'), line)
                self.assertNotIn('\n', line)
                for word in ('Completed', 'database.windows.net', '203.0.113.7', 'Two', 'aaaa'):
                    self.assertNotIn(word, line)
        for reason in (None, ''):
            with self.subTest(reason=reason):
                line = public_verdict(finished(reason=reason)['properties'])
                self.assertTrue(line.endswith(', no reason given.'), line)

    def test_the_values_a_real_execution_carried_on_azure_read_as_these_lines(self):
        # What two executions of a throwaway job carried on API version 2026-07-01, read by hand on
        # 2026-10-02 (README.md, "Measured on Azure"): one that ended well, one made to exit 7. The
        # values are the ones read; the times are invented, and where each field sits in the answer
        # is the reference's, not something that read kept.
        def carried(status, code, reason, message):
            properties = finished(status=status, code=code, reason=reason, message=message)['properties']
            properties['detailedStatus']['replicas'][0]['containers'][0]['additionalInformation'] = 'ProcessExited'
            return properties

        ended_well = carried('Succeeded', 0, 'CompletionsReached', 'Reached expected number of succeeded pods')
        self.assertEqual(public_verdict(ended_well), (
            'Verdict: execution run: Succeeded, started 2026-10-02T18:00:03Z, '
            'ended 2026-10-02T18:00:09Z (6 s), exit code 0 (done), reason CompletionsReached.'))
        self.assertTrue(deploy.verdict('run', ended_well).endswith(
            ', reason CompletionsReached. Azure says: Reached expected number of succeeded pods / ProcessExited'))
        # On the one that failed no length could be worked out: a start or an end time was absent.
        failed = carried('Failed', 7, 'BackoffLimitExceeded', 'Job has reached the specified backoff limit')
        ending = ', exit code 7 (not a code the tool itself exits with), reason BackoffLimitExceeded.'
        for absent, times in (('startTime', 'start not reported, ended 2026-10-02T18:00:09Z'),
                              ('endTime', 'started 2026-10-02T18:00:03Z, end not reported')):
            with self.subTest(absent=absent):
                properties = {key: value for key, value in failed.items() if key != absent}
                self.assertEqual(public_verdict(properties), f'Verdict: execution run: Failed, {times}{ending}')
                self.assertNotIn('backoff limit', public_verdict(properties))

    def test_azures_own_message_is_for_the_owners_terminal_and_never_for_actions(self):
        properties = finished(reason='Container exited with a non-zero code',
                              message='MESSAGE-MARKER')['properties']
        container = properties['detailedStatus']['replicas'][0]['containers'][0]
        container['additionalInformation'] = 'ADDITIONAL-MARKER'
        self.assertIn(", reason 'Container exited with a non-zero code'. "
                      'Azure says: MESSAGE-MARKER / ADDITIONAL-MARKER', deploy.verdict('run', properties))
        for hidden in ('MESSAGE-MARKER', 'ADDITIONAL-MARKER', 'non-zero', 'Azure says'):
            self.assertNotIn(hidden, public_verdict(properties))

    def test_a_field_with_a_shape_nobody_expects_is_said_to_be_missing_and_is_never_printed(self):
        strange = 'x\n::add-mask::something'
        properties = {'status': strange, 'startTime': strange, 'endTime': 5, 'reason': strange,
                      'detailedStatus': strange}
        self.assertEqual(public_verdict(properties, strange), (
            'Verdict: execution whose name is withheld: status not reported, start not reported, '
            'end not reported, exit code not reported, reason withheld, read it with --job-log.'))


class MigrationVerdictTests(Offline):
    """`deploy.run_migration` leaves the verdict whichever way the run ends."""

    def run_with(self, final, later=None, in_actions=True):
        """A migration that the polling sees end as `final`. `later` is what the read with the
        later API version answers: an execution, nothing, or an error to raise."""
        self.calls = []

        def rest(method, resource_id, body=None, api_version=deploy.API_VERSION):
            self.calls.append((method, resource_id, api_version))
            if method == 'POST':
                return {'name': 'this-run'}
            if isinstance(later, Exception):
                raise later
            return {'value': [finished('another-run', code=2), later]} if later else {}

        self.start(patch('deploy.rest', rest))
        self.start(patch('deploy.executions', side_effect=[[], [final]]))
        try:
            return deploy.run_migration('/job', 600, in_actions=in_actions)
        except RuntimeError as error:
            return error

    def verdicts(self):
        return [line.split(' ', 1)[1] for line in self.printed().splitlines() if 'Verdict:' in line]

    def test_a_run_that_succeeds_leaves_its_verdict_read_once_with_the_later_version(self):
        result = self.run_with(execution('this-run', 'Succeeded'), finished('this-run'))
        self.assertEqual(result, 'this-run')
        self.assertEqual(self.verdicts(), [VERDICT])
        self.assertEqual(self.calls, [('POST', '/job/start', '2025-01-01'),
                                      ('GET', '/job/executions', '2026-07-01')])

    def test_a_run_that_fails_leaves_its_verdict_and_says_where_its_text_is(self):
        later = finished('this-run', status='Failed', code=1, reason='Error', message='MESSAGE-MARKER')
        result = self.run_with(execution('this-run', 'Failed'), later)
        self.assertIsInstance(result, RuntimeError)
        self.assertEqual(self.verdicts(), [
            'Verdict: execution this-run: Failed, started 2026-10-02T18:00:03Z, '
            'ended 2026-10-02T18:00:09Z (6 s), exit code 1 (failed after it reached for the server; '
            'running it again is safe), reason Error.'])
        message = str(result)
        self.assertIn('execution this-run: Failed', message)
        self.assertIn('kept in the log workspace and is never fetched here', message)
        self.assertIn('`python infra/deploy.py --job-log`', message)
        self.assertIn('`--app-log <minutes>`', message)
        self.assertNotIn('MESSAGE-MARKER', message + self.printed())
        self.assertNotIn('portal', message)

    def test_a_verdict_azure_will_not_detail_is_still_a_verdict_and_changes_nothing_of_the_run(self):
        known = execution('this-run', 'Succeeded', '2026-10-02T18:00:03Z')
        refused = deploy.AzError('Bad Request: NoRegisteredProviderFound for the API version')
        for later in (refused, None, finished('a-third-run')):
            with self.subTest(later=later):
                self.clear()
                self.assertEqual(self.run_with(known, later), 'this-run')
                self.assertEqual(self.verdicts(), [
                    'Verdict: execution this-run: Succeeded, started 2026-10-02T18:00:03Z, '
                    'end not reported, exit code not reported, no reason given.'])
        self.clear()
        self.assertIsInstance(self.run_with(execution('this-run', 'Failed'), refused), RuntimeError)
        self.assertEqual(self.verdicts(), [
            'Verdict: execution this-run: Failed, start not reported, end not reported, '
            'exit code not reported, no reason given.'])

    def test_inside_actions_the_verdict_is_also_on_the_summary_page_and_nowhere_else(self):
        with tempfile.TemporaryDirectory() as directory:
            summary = os.path.join(directory, 'summary.md')
            deploy.os.environ['GITHUB_STEP_SUMMARY'] = summary
            self.run_with(execution('this-run', 'Succeeded'), finished('this-run'), in_actions=False)
            self.assertFalse(os.path.exists(summary), 'outside Actions the variable means nothing')
            self.run_with(execution('this-run', 'Succeeded'), finished('this-run'))
            with open(summary, encoding='utf-8') as page:
                self.assertEqual(page.read(), f'Migration: {VERDICT}\n')
            self.assertEqual(os.listdir(directory), ['summary.md'])

    def test_a_summary_page_that_cannot_be_written_fails_nothing(self):
        deploy.os.environ['GITHUB_STEP_SUMMARY'] = os.path.join(
            tempfile.gettempdir(), 'no-such-folder-here', 'summary.md')
        result = self.run_with(execution('this-run', 'Succeeded'), finished('this-run'))
        self.assertEqual(result, 'this-run')
        self.assertEqual(self.verdicts(), [VERDICT])


class RevisionTests(Offline):
    @patch('deploy.rest')
    def test_the_revision_before_this_run_does_not_pass_and_any_new_name_does(self, rest):
        old = resource(['bff', 'api'])
        old['properties'].update(latestRevisionName='before', latestReadyRevisionName='before')
        unready = copy.deepcopy(old)
        unready['properties'].update(latestRevisionName='app-d-x', latestReadyRevisionName='before')
        new = copy.deepcopy(old)
        new['properties'].update(latestRevisionName='app-d-x', latestReadyRevisionName='app-d-x')
        rest.side_effect = [old, unready, new]
        result = deploy.wait_revision('/app', {'bff': 'previous', 'api': 'previous'}, 'before')
        self.assertEqual(result, new)
        self.assertEqual(len(self.clock.sleeps), 2)

    @patch('deploy.rest')
    def test_failed_provisioning_is_a_failed_revision(self, rest):
        failed = resource(['bff', 'api'])
        failed['properties']['provisioningState'] = 'Failed'
        rest.return_value = failed
        with self.assertRaisesRegex(deploy.RevisionFailed, 'Failed'):
            deploy.wait_revision('/app', {'bff': 'new', 'api': 'new'}, 'before')

    @patch('deploy.rest')
    def test_a_revision_that_is_never_ready_is_a_failed_revision_after_fifteen_minutes(self, rest):
        unready = resource(['bff', 'api'])
        unready['properties'].update(latestRevisionName='app-d-x', latestReadyRevisionName='before')
        rest.return_value = unready
        with self.assertRaisesRegex(deploy.RevisionFailed, 'never became ready'):
            deploy.wait_revision('/app', {'bff': 'previous', 'api': 'previous'}, 'before')
        self.assertGreaterEqual(self.clock.now, 900)


class DeploymentTests(DeployCase):
    def test_success_moves_the_job_then_migrates_then_moves_the_app_then_smokes(self):
        self.deploy()
        self.assertEqual(self.steps(), ['job azurebank-migrate', 'migration', 'app d', 'smoke'])
        body = self.azure.app_patches()[0]
        self.assertEqual(deploy.images(body),
                         {name: f'ghcr.io/gurgant/azurebank-{name}:{NEW}' for name in ('bff', 'api')})
        self.assertRegex(body['properties']['template']['revisionSuffix'], r'^d-a{12}-[0-9a-f]{8}$')
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-migrate']),
                         {'migrate': f'ghcr.io/gurgant/azurebank-tools:{NEW}'})
        self.migration.assert_called_once_with(MIGRATE_ID, 600, in_actions=False)
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=False)

    def test_with_the_demo_off_the_pool_job_is_neither_read_nor_moved(self):
        # The app says the demo is off, and that is read from the app alone: whether a job of that
        # name exists is not asked. Here one does.
        self.azure.jobs['azurebank-pool'] = pool_resource()
        self.deploy()
        self.assertEqual([call for call in self.azure.calls if 'azurebank-pool' in call[1]], [])
        self.assertEqual(self.steps(), ['job azurebank-migrate', 'migration', 'app d', 'smoke'])
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-pool']),
                         {'pool': f'ghcr.io/gurgant/azurebank-tools:{OLD}'})
        said = [line.split(' ', 1)[1] for line in self.azure.printed_before_first_write.splitlines()]
        self.assertEqual([line for line in said if 'azurebank-pool' in line],
                         ['The app says the demo is off: the job azurebank-pool is not read and not moved.'])

    def test_the_app_request_carries_the_template_it_read_and_nothing_else(self):
        self.deploy()
        body = self.azure.app_patches()[0]
        self.assertEqual(set(body['properties']), {'template'})
        self.assertEqual(body['properties']['template']['scale'], {'minReplicas': 0, 'maxReplicas': 1})
        self.assertEqual(body['properties']['template']['containers'][0]['env'],
                         [{'name': 'KEY', 'secretRef': 'key'}])

    def test_failed_migration_never_patches_the_app(self):
        self.migration.side_effect = RuntimeError('migration failed')
        with self.assertRaisesRegex(RuntimeError, 'migration failed'):
            self.deploy()
        self.assertEqual(self.azure.writes(), [('PATCH', MIGRATE_ID)])
        self.smoke.assert_not_called()

    def test_invalid_sha_fails_before_azure(self):
        # A branch name, a short SHA, upper case, a digest: only the tag build-push writes is taken.
        for tag in ('main', 'a' * 7, 'a' * 39, 'a' * 41, 'A' * 40, 'sha256:' + 'a' * 40, 'a' * 40 + '\n'):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                deploy.deploy(SUBSCRIPTION, GROUP, tag)
        self.assertEqual(self.azure.calls, [])

    def test_a_job_timeout_over_fourteen_minutes_is_refused_before_any_change(self):
        self.azure.jobs['azurebank-migrate']['properties']['configuration']['replicaTimeout'] = 900
        with self.assertRaisesRegex(RuntimeError, 'timeout'):
            self.deploy()
        self.assertEqual(self.azure.writes(), [])

    def test_what_runs_now_is_printed_before_the_first_write(self):
        self.deploy()
        before = self.azure.printed_before_first_write
        self.assertIn('Running now', before)
        for image in ('azurebank-bff', 'azurebank-api', 'azurebank-tools'):
            self.assertIn(f'ghcr.io/gurgant/{image}:{OLD}', before)
        self.assertEqual(before.count(BEFORE), 2, 'the latest revision and the latest ready one')


def out_of_shape(what, *drift, when='nothing was changed', before='deploying'):
    """The whole sentence of a refusal for a shape, so that a test holds every word of it: what
    was found is in it only where the drift itself says so. `before` is what whoever was refused
    had asked for: a deployment, unless the test says otherwise."""
    return (f'{what} is not in the shape this script deploys onto ({when}): {"; ".join(drift)}. '
            f'Put it right with the template (infra/README.md) before {before}.')


def disagree(on, off):
    """Two containers that disagree, each side by its containers' names in their order."""
    return f'Demo__Enabled is true in {on} and not in {off}'


def neither(name):
    return (f"Demo__Enabled in the container '{name}' is something the template never writes "
            '(it writes the plain value true or false, once)')


class DemoStateTests(DeployCase):
    """Whether the app is the public demo is read from the app itself: the setting Demo__Enabled of
    its two containers, which infra/main.bicep writes on both from one switch."""

    def test_no_flag_or_false_on_both_is_off(self):
        # CONTROL: green before this change: a function that answers False for every app passes it.
        bare = flagged()
        for entry in bare['properties']['template']['containers']:
            del entry['env']
        for app in (flagged(), flagged('false', 'false'), flagged('false', None), flagged(None, 'false'), bare):
            self.assertIs(deploy.demo_of(app), False)

    def test_true_on_both_is_on(self):
        self.assertIs(deploy.demo_of(flagged('true', 'true')), True)
        # Whatever the case of the setting's name: infra/secrets.ps1 finds it so too (its -eq), and
        # the two must not read one app two ways.
        self.assertIs(deploy.demo_of(flagged('true', 'true', name='DEMO__ENABLED')), True)
        self.assertIs(deploy.demo_of(flagged([{'name': 'demo__enabled', 'value': 'true'}], 'true')), True)

    def test_two_containers_that_disagree_are_refused_before_any_change(self):
        for bff, api in (('true', 'false'), ('false', 'true'), ('true', None), (None, 'true')):
            on, off = (['bff'], ['api']) if bff == 'true' else (['api'], ['bff'])
            for options in ({}, {'app_only': True}):
                with self.subTest(bff=bff, api=api, **options):
                    self.azure.app = flagged(bff, api)
                    with self.assertRaises(deploy.ShapeError) as raised:
                        self.deploy(**options)
                    self.assertEqual(str(raised.exception), out_of_shape('The app', disagree(on, off)))
        self.assertEqual(self.azure.writes(), [])
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_a_container_too_many_is_named_when_the_other_two_say_the_demo_is_on(self):
        # The demo is read before the app's shape is checked. With the demo on in bff and api, a
        # third container that does not carry the setting is met here first: the refusal names the
        # containers on each side, so the one too many is in it. With the demo off the same app is
        # refused for its containers, as before (the drift test of template.containers).
        for extra, off in ((container('sidecar', 'ghcr.io/someone/else:latest'), ['sidecar']),
                           # What Azure could put in a name is repeated as Python writes it, on one line.
                           (container(STRANGE, 'ghcr.io/someone/else:latest'), [STRANGE]),
                           # An entry that is not an object has no name to give.
                           ('not an object', ['?'])):
            with self.subTest(off=off):
                self.azure.app = flagged('true', 'true')
                app_part(self.azure, 'template', 'containers').append(extra)
                with self.assertRaises(deploy.ShapeError) as raised:
                    self.deploy()
                self.assertEqual(str(raised.exception), out_of_shape('The app', disagree(['api', 'bff'], off)))
                self.assertNotIn('\n', str(raised.exception))
        self.assertEqual(self.azure.writes(), [])
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_a_value_that_is_neither_is_refused_and_never_printed(self):
        # What the template writes is the plain value true or false, once. Anything else was set
        # by hand: no side is chosen, and what was found is not repeated.
        flag = 'Demo__Enabled'
        strange = {
            'another word': 'VALUE-MARKER',
            'a capital': 'True',
            'nothing': '',
            'a space after it': 'true ',
            'a number': [{'name': flag, 'value': 1}],
            'a boolean': [{'name': flag, 'value': True}],
            'no value': [{'name': flag}],
            'a secret': [{'name': flag, 'secretRef': 'VALUE-MARKER'}],
            'twice': [{'name': flag, 'value': 'true'}] * 2,
            'twice, in two cases': [{'name': flag, 'value': 'true'}, {'name': flag.upper(), 'value': 'true'}],
        }
        for what, value in strange.items():
            for bff, api, named in ((value, value, ('bff', 'api')), (value, 'true', ('bff',)),
                                    ('false', value, ('api',))):
                with self.subTest(what=what, on=named):
                    self.azure.app = flagged(bff, api)
                    with self.assertRaises(deploy.ShapeError) as raised:
                        self.deploy()
                    self.assertEqual(str(raised.exception), out_of_shape('The app', *map(neither, named)))
                    self.assertNotIn('VALUE-MARKER', str(raised.exception) + self.printed())
        self.assertEqual(self.azure.writes(), [])
        self.migration.assert_not_called()
        self.smoke.assert_not_called()


def beside_a_pool_run(name, state, timeout=600):
    """The whole sentence of the refusal beside a pool run, so that a test holds every word of it."""
    return (f'Execution {name} of the job azurebank-pool is {state}: a pool run may still be in '
            'progress, and a deployment does not start beside one. Nothing was changed. A run is '
            f"expected to end within the job's timeout ({timeout} s): deploy again after that. If it "
            "is refused again then, the owner reads the job's executions and stops that one "
            '(infra/README.md, "When something fails").')


class PoolDeployTests(DeployCase):
    """With the demo on a deployment also moves the pool job: after the migration, so that the job
    never runs a new image on an old schema, and only once it has read the job and found it in shape."""

    STEPS = ['job azurebank-migrate', 'migration', 'job azurebank-pool', 'app d', 'smoke']

    def setUp(self):
        super().setUp()
        self.azure.turn_the_demo_on()

    def refused_before_any_change(self, named, drift):
        drift(self.azure)
        with self.assertRaises(deploy.ShapeError) as raised:
            self.deploy()
        self.assertEqual(str(raised.exception), out_of_shape('The job azurebank-pool', named))
        for hidden in ('MARKER', '* * * * *'):
            self.assertNotIn(hidden, self.printed())
        self.assertEqual(self.azure.writes(), [], 'nothing may be changed on a drifted job')
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_the_pool_job_moves_after_the_migration_and_before_the_app(self):
        self.deploy()
        self.assertEqual(self.steps(), self.STEPS)
        tools = f'ghcr.io/gurgant/azurebank-tools:{NEW}'
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-pool']), {'pool': tools})
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-migrate']), {'migrate': tools})
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=True)
        # CONTROL: green as written. The line that says the pool job is not read and not moved is
        # for a run with the demo off: this run reads that job and moves it. Seen red with the line
        # said whatever the app says.
        self.assertNotIn('the demo is off', self.printed())
        # What the job runs and what it is told travel back as they were read, and so does the
        # flag of the app's two containers: a deployment moves images and nothing else.
        moved = self.azure.jobs['azurebank-pool']['properties']['template']['containers'][0]
        self.assertEqual((moved['args'], moved['env']), (['recycle'], [{'name': 'KEY', 'secretRef': 'key'}]))
        self.assertEqual([entry['env'][-1] for entry in app_part(self.azure, 'template', 'containers')],
                         [{'name': 'Demo__Enabled', 'value': 'true'}] * 2)

    def test_a_failed_migration_leaves_the_pool_job_on_the_old_image(self):
        # CONTROL: green before this change. While the script did not know the pool job, the
        # migration's error, the one write and the old image all held. It is the guard of the
        # order now: with the pool job moved before the migration it fails.
        self.migration.side_effect = RuntimeError('migration failed')
        with self.assertRaisesRegex(RuntimeError, 'migration failed'):
            self.deploy()
        self.assertEqual(self.azure.writes(), [('PATCH', MIGRATE_ID)])
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-pool']),
                         {'pool': f'ghcr.io/gurgant/azurebank-tools:{OLD}'})

    def test_a_pool_job_that_cannot_be_read_stops_the_run_before_any_change(self):
        # Which of the two Azure answers for a job that is not there, to an identity whose role is
        # on each resource by itself, has not been seen. Neither is read as "there is no pool job".
        self.azure.refuse = lambda method, resource_id, body: resource_id == POOL_ID
        for refusal in ('Not Found: ResourceNotFound', 'Forbidden: AuthorizationFailed.'):
            with self.subTest(refusal=refusal):
                self.azure.refusal = refusal
                with self.assertRaises(RuntimeError) as raised:
                    self.deploy()
                self.assertIs(type(raised.exception), RuntimeError, 'the run says what it means, in its own words')
                self.assertEqual(str(raised.exception), (
                    'The app says the demo is on, and the job azurebank-pool could not be read: '
                    f'{refusal.rstrip(".")}. Nothing was changed. See infra/README.md, "When something fails".'))
        self.assertEqual(self.azure.writes(), [])
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_runs_of_the_pool_job_that_cannot_be_read_stop_the_run_in_its_own_words(self):
        # The job itself was read, and the list of its executions is refused or never answered:
        # whether a pool run is in progress is then not known. Azure's words alone would not say
        # that nothing was changed, nor where to look.
        self.azure.refuse = lambda method, resource_id, body: resource_id == POOL_ID + '/executions'
        for refusal in ('Forbidden: AuthorizationFailed.',
                        'The Azure CLI gave no answer in 180 s (GET /jobs/azurebank-pool/executions).'):
            with self.subTest(refusal=refusal):
                self.azure.refusal = refusal
                with self.assertRaises(RuntimeError) as raised:
                    self.deploy()
                self.assertIs(type(raised.exception), RuntimeError, 'the run says what it means, in its own words')
                self.assertEqual(str(raised.exception), (
                    'The executions of the job azurebank-pool could not be read, so whether a pool run is '
                    f'in progress is not known: {refusal.rstrip(".")}. Nothing was changed. See '
                    'infra/README.md, "When something fails".'))
        self.assertEqual(self.azure.writes(), [])
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_a_pool_run_in_progress_stops_the_deployment_before_any_change(self):
        now = datetime.datetime.now(datetime.timezone.utc)
        # CONTROL: green as written, the last case. A run in a state nobody named is taken for
        # alive for the pool job's own timeout and two minutes, 720 s here: one that began five
        # minutes ago still stops the deployment. Seen red with no timeout handed to the rule.
        five_minutes_ago = (now - datetime.timedelta(seconds=300)).isoformat()
        for state, started in (('Running', None), ('Processing', None), ('Unknown', now.isoformat()),
                               ('Unknown', five_minutes_ago)):
            with self.subTest(state=state, started=started):
                self.azure.pool_runs = [finished('an-earlier-run'), execution('pool-run', state, started)]
                with self.assertRaises(RuntimeError) as raised:
                    self.deploy()
                self.assertEqual(str(raised.exception), beside_a_pool_run('pool-run', state))
        self.assertEqual(self.azure.writes(), [])
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_a_pool_run_is_aged_against_the_pool_jobs_own_timeout(self):
        # CONTROL: green as written. Each job has a timeout of its own in infra/main.bicep, and the
        # migrate job's stays 600 here. Seen red with the migrate job handed to the rule in the pool
        # job's place: the first half on "RuntimeError not raised", the second on the refusal of a
        # run that is too old to be alive.
        now = datetime.datetime.now(datetime.timezone.utc)
        with self.subTest('840 s: a run in a state nobody named that began 800 s ago may be alive'):
            pool_configuration(self.azure).update(replicaTimeout=840)
            self.azure.pool_runs = [
                execution('pool-run', 'Unknown', (now - datetime.timedelta(seconds=800)).isoformat())]
            with self.assertRaises(RuntimeError) as raised:
                self.deploy()
            self.assertEqual(str(raised.exception), beside_a_pool_run('pool-run', 'Unknown', timeout=840))
            self.assertEqual(self.azure.writes(), [])
        with self.subTest('60 s: one that began 300 s ago is too old to be alive, and stops nothing'):
            pool_configuration(self.azure).update(replicaTimeout=60)
            self.azure.pool_runs = [
                execution('pool-run', 'Unknown', (now - datetime.timedelta(seconds=300)).isoformat())]
            self.deploy()
            self.assertEqual(self.steps(), self.STEPS)

    def test_a_pool_run_that_goes_on_blocking_is_told_the_way_out(self):
        # A run in a state nobody named and with no start time is taken for alive for as long as
        # it is listed so (in_progress): "deploy again after that" alone would be refused each
        # time, with no word of who ends it. The deployment identity cannot.
        self.azure.pool_runs = [execution('stuck', 'Pending')]
        with self.assertRaises(RuntimeError) as raised:
            self.deploy()
        self.assertEqual(str(raised.exception),
                         beside_a_pool_run('stuck', 'in a state this script does not know'))
        self.assertTrue(str(raised.exception).endswith(
            "deploy again after that. If it is refused again then, the owner reads the job's "
            'executions and stops that one (infra/README.md, "When something fails").'), str(raised.exception))
        self.assertEqual(self.azure.writes(), [])

    def test_a_pool_run_that_blocks_is_named_only_in_the_shape_expected(self):
        recent = datetime.datetime.now(datetime.timezone.utc).isoformat()
        unknown = 'in a state this script does not know'
        withheld = 'whose name is withheld'
        for blocking, name, state in ((execution(STRANGE, 'Running'), withheld, 'Running'),
                                      (execution('pool-run', STRANGE, recent), 'pool-run', unknown),
                                      (execution(STRANGE, {'state': STRANGE}, recent), withheld, unknown)):
            with self.subTest(name=name, state=state):
                self.azure.pool_runs = [blocking]
                with self.assertRaises(RuntimeError) as raised:
                    self.deploy(in_actions=True)
                # The sentence whole: no word of it is what Azure put in the name or in the state.
                # (It points at the runbook's "When something fails", so the word "something" of
                # STRANGE cannot be looked for by itself.)
                self.assertEqual(str(raised.exception), beside_a_pool_run(name, state))
                for unwanted in ('\n', '::', 'add-mask'):
                    self.assertNotIn(unwanted, str(raised.exception))
        self.assertEqual(self.azure.writes(), [])

    def test_pool_runs_that_ended_or_are_too_old_to_be_alive_do_not_stop_the_deployment(self):
        stale = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=2)).isoformat()
        self.azure.pool_runs = [finished('one'), finished('two', status='Failed', code=10),
                                execution('stale', 'Unknown', stale)]
        self.deploy()
        self.assertEqual(self.steps(), self.STEPS)

    def test_the_runs_of_the_pool_job_are_read_once_before_anything_is_changed_and_none_is_started(self):
        self.deploy()
        calls = [(method, resource_id) for method, resource_id, _ in self.azure.calls]
        self.assertEqual(calls.count(('GET', POOL_ID + '/executions')), 1)
        self.assertLess(calls.index(('GET', POOL_ID + '/executions')), calls.index(('PATCH', MIGRATE_ID)))
        self.assertEqual([call for call in calls if call[0] != 'GET' and call[1].startswith(POOL_ID)],
                         [('PATCH', POOL_ID)])

    def test_the_pool_job_is_read_again_after_it_moved_and_a_drift_stops_the_run(self):
        self.azure.drift_job_on_patch = lambda job: (
            'pool' in deploy.images(job) and job['properties']['configuration'].update(triggerType='Manual'))
        with self.assertRaises(deploy.ShapeError) as raised:
            self.deploy()
        self.assertEqual(str(raised.exception), out_of_shape(
            'The job azurebank-pool', "configuration.triggerType is 'Manual'", when='after its image moved'))
        self.assertEqual(self.steps(), self.STEPS[:3])
        self.assertEqual(self.azure.app_patches(), [])
        self.smoke.assert_not_called()

    def test_values_azure_leaves_out_of_the_pool_job_read_as_their_defaults(self):
        pool_configuration(self.azure).update(replicaRetryLimit=None, triggerType='schedule')
        del pool_configuration(self.azure)['scheduleTriggerConfig']['parallelism']
        self.azure.jobs['azurebank-pool']['properties']['template'].update(initContainers=[])
        pool_container(self.azure).update(command=None)
        self.deploy()
        self.assertEqual(self.steps(), self.STEPS)

    def test_a_timeout_at_either_end_of_the_templates_range_is_in_shape(self):
        # CONTROL: green as written. infra/main.bicep allows 60 and 840 themselves (the least and the
        # greatest value of its parameter poolTimeout), and a job written with either is one this
        # script deploys onto. Seen red with either end taken out of the range.
        for timeout in deploy.POOL_TIMEOUTS:
            with self.subTest(timeout=timeout):
                pool_configuration(self.azure).update(replicaTimeout=timeout)
                self.assertEqual(deploy.pool_drift(self.azure.jobs['azurebank-pool']), [])

    def test_what_runs_now_names_the_pool_jobs_image_too(self):
        self.deploy()
        (line,) = [line for line in self.azure.printed_before_first_write.splitlines() if 'Running now' in line]
        self.assertEqual(re.findall(r'ghcr\.io/gurgant/azurebank-[a-z]+:[0-9a-f]{40}', line),
                         [f'ghcr.io/gurgant/azurebank-{name}:{OLD}' for name in ('bff', 'api', 'tools', 'tools')])

    def test_a_pool_job_that_does_not_carry_exactly_the_apps_identity_is_refused(self):
        # The pool job signs in to the database as the app does: the same identity, and no other.
        job = self.azure.jobs['azurebank-pool']
        right = job['identity']
        for what, (block, _) in WRONG_IDENTITIES.items():
            with self.subTest(what=what):
                job['identity'] = block
                with self.assertRaises(deploy.ShapeError) as raised:
                    self.deploy()
                self.assertEqual(str(raised.exception), out_of_shape(
                    'The job azurebank-pool',
                    'identity is not exactly one user-assigned identity ending in /azurebank-app'))
                self.assertEqual(self.azure.writes(), [], 'nothing may be changed')
        job['identity'] = right
        self.deploy()
        self.assertEqual(self.steps(), self.STEPS)

    def test_with_the_demo_on_no_request_carries_an_identity_either(self):
        # The deployment, and the put-back after it: four bodies, each a location and a template.
        self.azure.fate['d'] = 'never'
        with self.assertRaisesRegex(RuntimeError, 'put back'):
            self.deploy()
        patches = [(resource_id, body) for method, resource_id, body in self.azure.calls if method == 'PATCH']
        self.assertEqual([resource_id for resource_id, _ in patches], [MIGRATE_ID, POOL_ID, APP_ID, APP_ID])
        for _, body in patches:
            self.assertEqual((sorted(body), sorted(body['properties'])),
                             (['location', 'properties'], ['template']))
            self.assertEqual([key for key in keys_of(body) if 'identit' in key.lower()], [])
        self.assertIn('identity', self.azure.jobs['azurebank-pool'], 'the job that was read did hold one')

    def test_a_refusal_that_asks_for_a_right_on_the_identity_stops_the_run_at_the_pool_job_too(self):
        # The pool job carries the app's database identity. What Azure answers when the deployment
        # identity changes that job has not been seen; if it asks for a right on the identity, the
        # run stops there as it does for the migrate job and for the app.
        self.azure.refusal = LINKED
        self.azure.refuse = lambda method, resource_id, body: method == 'PATCH' and resource_id == POOL_ID
        with self.assertRaises(deploy.IdentityRightAsked) as raised:
            self.deploy()
        self.assertTrue(str(raised.exception).startswith(
            'Azure asked for a right on a database identity before it would change the job '
            'azurebank-pool: stop here.'), str(raised.exception))
        self.assertEqual(self.azure.writes(), [('PATCH', MIGRATE_ID), ('PATCH', POOL_ID)],
                         'no retry, and the app is not touched')
        self.smoke.assert_not_called()


def app_part(azure, *path):
    node = azure.app['properties']
    for key in path:
        node = node[key]
    return node


def job_configuration(azure):
    return azure.jobs['azurebank-migrate']['properties']['configuration']


# field named in the refusal -> how the resource drifted
APP_DRIFTS = {
    'template.scale.minReplicas': lambda a: app_part(a, 'template', 'scale').update(minReplicas=1),
    'template.scale.maxReplicas': lambda a: app_part(a, 'template', 'scale').update(maxReplicas=2),
    'configuration.activeRevisionsMode':
        lambda a: app_part(a, 'configuration').update(activeRevisionsMode='Multiple'),
    'configuration.ingress.external': lambda a: app_part(a, 'configuration', 'ingress').update(external=False),
    'configuration.ingress.targetPort':
        lambda a: app_part(a, 'configuration', 'ingress').update(targetPort=5068),
    'configuration.ingress.allowInsecure':
        lambda a: app_part(a, 'configuration', 'ingress').update(allowInsecure=True),
    'configuration.ingress.additionalPortMappings':
        lambda a: app_part(a, 'configuration', 'ingress').update(
            additionalPortMappings=[{'external': True, 'targetPort': 5068}]),
    'template.containers': lambda a: app_part(a, 'template', 'containers').append(
        container('extra', 'ghcr.io/someone/else:latest')),
    'template.initContainers': lambda a: app_part(a, 'template').update(
        initContainers=[container('first', 'ghcr.io/someone/else:latest')]),
}
JOB_DRIFTS = {
    'configuration.triggerType': lambda a: job_configuration(a).update(triggerType='Schedule'),
    'configuration.manualTriggerConfig.parallelism':
        lambda a: job_configuration(a)['manualTriggerConfig'].update(parallelism=3),
    'configuration.replicaRetryLimit': lambda a: job_configuration(a).update(replicaRetryLimit=2),
    'template.initContainers': lambda a: a.jobs['azurebank-migrate']['properties']['template'].update(
        initContainers=[container('first', 'ghcr.io/someone/else:latest')]),
}


def pool_configuration(azure):
    return azure.jobs['azurebank-pool']['properties']['configuration']


def pool_container(azure):
    return azure.jobs['azurebank-pool']['properties']['template']['containers'][0]


# name of the test -> (what the refusal says, how the pool job drifted). The schedule's expression,
# the arguments and a command are named by what they should be, never by what was found.
POOL_DRIFTS = {
    'trigger': ("configuration.triggerType is 'Manual'",
                lambda a: pool_configuration(a).update(triggerType='Manual')),
    'schedules_parallelism': ('configuration.scheduleTriggerConfig.parallelism is 3',
                              lambda a: pool_configuration(a)['scheduleTriggerConfig'].update(parallelism=3)),
    'retry_limit': ('configuration.replicaRetryLimit is 2',
                    lambda a: pool_configuration(a).update(replicaRetryLimit=2)),
    'init_containers': ("template.initContainers is ['first']",
                        lambda a: a.jobs['azurebank-pool']['properties']['template'].update(
                            initContainers=[container('first', 'ghcr.io/someone/else:latest')])),
    'arguments': ("template.containers[0].args is not ['recycle']",
                  lambda a: pool_container(a).update(args=['seed-pool', '--VALUE-MARKER'])),
    # CONTROL: green as written. The arguments are `recycle` and nothing after it: the command
    # declares no option and no argument of its own
    # (backend/tools/AzureBank.Seeder/Commands/RecycleCommand.cs), so a word after it is not the
    # run the template writes. Seen red with only the first word compared.
    'arguments_after_recycle': ("template.containers[0].args is not ['recycle']",
                                lambda a: pool_container(a).update(args=['recycle', '--help'])),
    'identity': ('identity is not exactly one user-assigned identity ending in /azurebank-app',
                 lambda a: a.jobs['azurebank-pool'].update(identity=identity('azurebank-migrate'))),
    'schedules_expression': ("configuration.scheduleTriggerConfig.cronExpression is not '0 */4 * * *'",
                             lambda a: pool_configuration(a)['scheduleTriggerConfig'].update(
                                 cronExpression='* * * * *')),
    'timeout': ('configuration.replicaTimeout is 3600',
                lambda a: pool_configuration(a).update(replicaTimeout=3600)),
    # Beyond the eight above: the other end of the timeout's range, a timeout that is no number,
    # a job with no schedule to read, a command (it would leave `recycle` an argument of something
    # else), and a container that is not the one the template writes.
    'timeout_under_a_minute': ('configuration.replicaTimeout is 59',
                               lambda a: pool_configuration(a).update(replicaTimeout=59)),
    'timeout_that_is_no_number': ('configuration.replicaTimeout is True',
                                  lambda a: pool_configuration(a).update(replicaTimeout=True)),
    # CONTROL: green as written, 'timeout_that_is_text' here and 'schedule_that_is_text' below. A
    # timeout that is text, and text where the schedule's settings are due, are refused like any
    # other drift and are not an error of this script: each was seen ending in one (a TypeError, an
    # AttributeError) with the check of the type taken out of pool_drift.
    'timeout_that_is_text': ("configuration.replicaTimeout is '600'",
                             lambda a: pool_configuration(a).update(replicaTimeout='600')),
    # CONTROL: green as written. A job with no timeout is refused for its shape, before its
    # executions are read against that timeout: seen ending in a KeyError with the two swapped.
    'missing_timeout': ('configuration.replicaTimeout is None',
                        lambda a: pool_configuration(a).pop('replicaTimeout')),
    'schedule_that_is_text': ("configuration.scheduleTriggerConfig.cronExpression is not '0 */4 * * *'",
                              lambda a: pool_configuration(a).update(scheduleTriggerConfig='0 */4 * * *')),
    'missing_schedule': ("configuration.scheduleTriggerConfig.cronExpression is not '0 */4 * * *'",
                         lambda a: pool_configuration(a).pop('scheduleTriggerConfig')),
    'command': ('template.containers[0].command is set',
                lambda a: pool_container(a).update(command=['/bin/sh', '-c', 'VALUE-MARKER'])),
    'second_container': ("template.containers is ['pool', 'extra']",
                         lambda a: a.jobs['azurebank-pool']['properties']['template']['containers'].append(
                             container('extra', 'ghcr.io/someone/else:latest'))),
    'containers_name': ("template.containers is ['migrate']", lambda a: pool_container(a).update(name='migrate')),
}


class ShapeTests(DeployCase):
    def refused_before_any_change(self, field, drift, what):
        drift(self.azure)
        with self.assertRaises(deploy.ShapeError) as raised:
            self.deploy()
        self.assertIn(field, str(raised.exception))
        self.assertTrue(str(raised.exception).startswith(what + ' is not in the shape'), str(raised.exception))
        self.assertEqual(self.azure.writes(), [], 'nothing may be changed on a drifted resource')
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_values_azure_leaves_out_read_as_their_defaults(self):
        app_part(self.azure, 'template', 'scale').update(minReplicas=None)
        del app_part(self.azure, 'configuration', 'ingress')['allowInsecure']
        app_part(self.azure, 'configuration', 'ingress').update(additionalPortMappings=None)
        app_part(self.azure, 'template').update(initContainers=None)
        job_configuration(self.azure).update(replicaRetryLimit=None)
        self.azure.jobs['azurebank-migrate']['properties']['template'].update(initContainers=[])
        self.deploy()
        self.assertEqual(self.steps()[-1], 'smoke')

    def test_an_init_container_is_named_and_its_definition_is_not_printed(self):
        app_part(self.azure, 'template').update(initContainers=[
            {'name': 'first', 'image': 'ghcr.io/someone/else:latest',
             'env': [{'name': 'TOKEN', 'value': 'a-plain-value'}]}])
        with self.assertRaises(deploy.ShapeError) as raised:
            self.deploy()
        self.assertIn("template.initContainers is ['first']", str(raised.exception))
        self.assertNotIn('a-plain-value', str(raised.exception) + self.printed())
        self.assertEqual(self.azure.writes(), [])

    def test_an_app_without_ingress_is_refused_before_any_change(self):
        self.refused_before_any_change(
            'configuration.ingress.external', lambda a: app_part(a, 'configuration').update(ingress=None),
            'The app')

    def test_the_job_is_read_again_after_it_moved_and_a_drift_stops_the_run(self):
        self.azure.drift_job_on_patch = lambda job: job['properties']['configuration'].update(
            triggerType='Schedule')
        with self.assertRaisesRegex(deploy.ShapeError, 'configuration.triggerType'):
            self.deploy()
        self.migration.assert_not_called()
        self.assertEqual(self.azure.app_patches(), [])


def _drift_test(field, drift, what):
    def test(self):
        self.refused_before_any_change(field, drift, what)
    return test


for _what, _drifts in (('The app', APP_DRIFTS), ('The job azurebank-migrate', JOB_DRIFTS)):
    for _field, _drift in _drifts.items():
        _name = f"{_what.split()[1]}_{_field.replace('.', '_')}"
        setattr(ShapeTests, f'test_a_drift_in_the_{_name}_is_refused_before_any_change',
                _drift_test(_field, _drift, _what))


def _pool_drift_test(named, drift):
    def test(self):
        self.refused_before_any_change(named, drift)
    return test


for _name, (_named, _drift) in POOL_DRIFTS.items():
    setattr(PoolDeployTests, f'test_a_drift_in_the_pool_jobs_{_name}_is_refused_before_any_change',
            _pool_drift_test(_named, _drift))


def keys_of(node):
    """Every key anywhere in a request body."""
    if isinstance(node, dict):
        for key, value in node.items():
            yield key
            yield from keys_of(value)
    elif isinstance(node, list):
        for value in node:
            yield from keys_of(value)


BOTH = {'type': 'UserAssigned', 'userAssignedIdentities': {
    **identity('azurebank-app')['userAssignedIdentities'],
    **identity('azurebank-migrate')['userAssignedIdentities']}}
# what is wrong with the identity block -> the block, on the app and on the job
WRONG_IDENTITIES = {
    'missing': (None, None),
    'system-assigned': ({'type': 'SystemAssigned', 'principalId': IDENTITY_IDS[0]},) * 2,
    'both kinds': ({**identity('azurebank-app'), 'type': 'SystemAssigned, UserAssigned'},
                   {**identity('azurebank-migrate'), 'type': 'SystemAssigned, UserAssigned'}),
    "the other resource's": (identity('azurebank-migrate'), identity('azurebank-app')),
    'two of them': (BOTH, BOTH),
    'none attached': ({'type': 'UserAssigned', 'userAssignedIdentities': {}},) * 2,
    'attached as a list': ({'type': 'UserAssigned', 'userAssignedIdentities': ['azurebank-app']},
                           {'type': 'UserAssigned', 'userAssignedIdentities': ['azurebank-migrate']}),
    'one whose name only ends the same': (identity('not-azurebank-app'),
                                          identity('not-azurebank-migrate')),
}
# Azure's words when a write needs a right on an identity attached to the resource.
LINKED = ("Forbidden({\"error\":{\"code\":\"LinkedAuthorizationFailed\",\"message\":\"The client "
          "'<id>' with object id '<id>' has permission to perform action 'Microsoft.App/jobs/write' "
          "on scope '/subscriptions/<id>/resourceGroups/group/providers/Microsoft.App/jobs/"
          "azurebank-migrate'; however, it does not have permission to perform action(s) "
          "'Microsoft.ManagedIdentity/userAssignedIdentities/assign/action' on the linked scope(s) "
          "'/subscriptions/<id>/resourcegroups/group/providers/Microsoft.ManagedIdentity/"
          "userAssignedIdentities/azurebank-migrate' (respectively) or the linked scope(s) are "
          "invalid.\"}})")


class IdentityTests(DeployCase):
    """The app carries the identity azurebank-app and the job azurebank-migrate: each exactly one."""

    def test_an_app_or_a_job_that_does_not_carry_exactly_its_own_identity_is_refused(self):
        job = self.azure.jobs['azurebank-migrate']
        for what, (on_app, on_job) in WRONG_IDENTITIES.items():
            for resource, block, name, expected in (
                    (self.azure.app, on_app, 'The app', 'azurebank-app'),
                    (job, on_job, 'The job azurebank-migrate', 'azurebank-migrate')):
                with self.subTest(what=what, on=name):
                    right = resource['identity']
                    resource['identity'] = block
                    with self.assertRaises(deploy.ShapeError) as raised:
                        self.deploy()
                    resource['identity'] = right
                    self.assertEqual(str(raised.exception), (
                        f'{name} is not in the shape this script deploys onto (nothing was changed): '
                        f'identity is not exactly one user-assigned identity ending in /{expected}. '
                        'Put it right with the template (infra/README.md) before deploying.'))
                    self.assertEqual(self.azure.writes(), [], 'nothing may be changed')
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_a_wrong_identity_is_named_by_its_field_and_what_was_found_is_never_printed(self):
        # The ID of an identity holds the subscription, and its entry the client and principal IDs.
        self.azure.app['identity'] = {'type': 'UserAssigned', 'userAssignedIdentities': {
            '/subscriptions/SUBSCRIPTION-MARKER/resourcegroups/other/providers'
            '/Microsoft.ManagedIdentity/userAssignedIdentities/someone-elses':
                {'principalId': IDENTITY_IDS[0], 'clientId': IDENTITY_IDS[1]}}}
        with self.assertRaises(deploy.ShapeError) as raised:
            self.deploy(in_actions=True)
        for hidden in ('SUBSCRIPTION-MARKER', 'someone-elses', 'other', *IDENTITY_IDS):
            self.assertNotIn(hidden, str(raised.exception) + self.printed())

    def test_the_identity_is_recognised_whatever_the_case_azure_returns_its_id_in(self):
        for resource in (self.azure.app, self.azure.jobs['azurebank-migrate']):
            (key, entry), = resource['identity']['userAssignedIdentities'].items()
            resource['identity'] = {'type': 'userAssigned', 'userAssignedIdentities': {key.upper(): entry}}
        self.deploy()
        self.assertEqual(self.steps()[-1], 'smoke')

    def test_the_app_only_road_checks_the_apps_identity_too(self):
        self.azure.app['identity'] = None
        with self.assertRaisesRegex(deploy.ShapeError, 'ending in /azurebank-app'):
            self.deploy(app_only=True)
        self.assertEqual(self.azure.writes(), [])


class RequestTests(DeployCase):
    """What a PATCH carries, and the one refusal that is never worked around."""

    def test_no_request_ever_carries_an_identity(self):
        # The deploy, and the put-back after it: three bodies, each a location and a template.
        self.azure.fate['d'] = 'never'
        with self.assertRaisesRegex(RuntimeError, 'put back'):
            self.deploy()
        bodies = [body for method, _, body in self.azure.calls if method == 'PATCH']
        self.assertEqual(len(bodies), 3)
        for body in bodies:
            self.assertEqual((sorted(body), sorted(body['properties'])),
                             (['location', 'properties'], ['template']))
            self.assertEqual([key for key in keys_of(body) if 'identit' in key.lower()], [])
        self.assertIn('identity', self.azure.app, 'the resource that was read did hold one')

    def test_a_refusal_that_asks_for_a_right_on_the_identity_stops_the_run_and_is_not_tried_again(self):
        self.azure.refusal = LINKED
        for target, what, written in ((MIGRATE_ID, 'the job azurebank-migrate', 1), (APP_ID, 'the app', 2)):
            with self.subTest(target=what):
                self.azure.calls.clear()
                self.azure.refuse = lambda method, resource_id, body: (
                    method == 'PATCH' and resource_id == target)
                with self.assertRaises(deploy.IdentityRightAsked) as raised:
                    self.deploy()
                message = str(raised.exception)
                self.assertTrue(message.startswith('Azure asked for a right on a database identity '
                                                   f'before it would change {what}: stop here.'), message)
                self.assertIn('it was not tried again, and no role is to be added for it', message)
                self.assertIn('userAssignedIdentities/assign/action', message)
                writes = self.azure.writes()
                self.assertEqual(writes.count(('PATCH', target)), 1)
                self.assertEqual(len(writes), written, 'no retry, no put-back, nothing after it')
        self.smoke.assert_not_called()

    def test_any_other_refusal_of_a_request_stays_azures_own_error(self):
        self.azure.refuse = lambda method, resource_id, body: method == 'PATCH'
        for refusal in ('Forbidden: AuthorizationFailed',
                        'Forbidden: LinkedAuthorizationFailed on a subnet join/action',
                        "Forbidden: AuthorizationFailed for 'Microsoft.ManagedIdentity"
                        "/userAssignedIdentities/assign/action'"):
            with self.subTest(refusal=refusal):
                self.azure.refusal = refusal
                with self.assertRaises(deploy.AzError):
                    self.deploy()

    def test_that_refusal_ends_the_program_with_its_own_sentence(self):
        self.azure.refusal = LINKED
        self.azure.refuse = lambda method, resource_id, body: method == 'PATCH'
        environment = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP,
                       'IMAGE_TAG': NEW}
        with patch.dict(deploy.os.environ, environment, clear=True), \
                self.assertRaises(SystemExit) as raised:
            deploy.main([])
        self.assertTrue(str(raised.exception.code).startswith(
            'Azure asked for a right on a database identity'), raised.exception.code)


REFUSED_LISTING = subprocess.CompletedProcess(
    [], 1, stdout='',
    stderr="Forbidden({\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"The client "
           "'11111111-2222-3333-4444-555555555555' does not have authorization to perform action "
           "'Microsoft.App/containerApps/listSecrets/action'\"}})")


class SecretsListingTests(DeployCase):
    """The deployment identity must be refused the app's secrets, and that is tried, not assumed."""

    def setUp(self):
        super().setUp()
        self.run = self.start(patch('deploy.subprocess.run', return_value=REFUSED_LISTING))

    def test_a_refusal_lets_the_deployment_go_on_and_is_reported(self):
        self.deploy(expect_secrets_refused=True)
        self.assertEqual(self.steps(), ['job azurebank-migrate', 'migration', 'app d', 'smoke'])
        self.assertIn('the listing was refused', self.printed())
        command = self.run.call_args.args[0]
        self.assertEqual(command[1:4], ['rest', '--method', 'POST'])
        url = command[command.index('--url') + 1]
        self.assertTrue(url.startswith(f'https://management.azure.com{APP_ID}/listSecrets?'), url)
        self.assertEqual(command[command.index('--output') + 1], 'none')
        self.assertEqual(self.run.call_count, 1)

    def test_the_refusal_is_proved_before_anything_is_changed(self):
        self.run.side_effect = lambda *args, **kwargs: (
            self.assertEqual(self.azure.writes(), []), REFUSED_LISTING)[1]
        self.deploy(expect_secrets_refused=True)
        self.assertEqual(self.run.call_count, 1)

    def test_an_answer_stops_the_run_before_any_change(self):
        self.run.return_value = subprocess.CompletedProcess([], 0, stdout='SECRET-VALUE', stderr='')
        with self.assertRaisesRegex(RuntimeError, 'can list the secrets') as raised:
            self.deploy(expect_secrets_refused=True)
        self.assertEqual(self.azure.writes(), [])
        self.assertNotIn('SECRET-VALUE', str(raised.exception) + self.printed())

    def test_any_other_failure_proves_nothing_and_stops_the_run(self):
        self.run.return_value = subprocess.CompletedProcess(
            [], 1, stdout='', stderr='ERROR: Connection reset by peer')
        with self.assertRaisesRegex(RuntimeError, 'Could not prove'):
            self.deploy(expect_secrets_refused=True)
        self.assertEqual(self.azure.writes(), [])

    def test_a_listing_that_times_out_proves_nothing_and_names_no_identifier(self):
        self.run.side_effect = lambda command, **options: raise_(subprocess.TimeoutExpired(command, 180))
        with self.assertRaisesRegex(RuntimeError, 'Could not prove') as raised:
            self.deploy(expect_secrets_refused=True)
        self.assertIn('POST /containerApps/azurebank/listSecrets', str(raised.exception))
        self.assertNotIn(SUBSCRIPTION, str(raised.exception) + self.printed())
        self.assertEqual(self.azure.writes(), [])

    def test_the_owner_is_not_asked_to_be_refused(self):
        self.deploy()
        self.run.assert_not_called()
        self.assertNotIn('listing', self.printed())


class GateTests(DeployCase):
    """A deploy that fails after the app moved puts the app back on what it ran before."""

    def assert_put_back(self, raised):
        message = str(raised.exception)
        self.assertIn(f'put back to {OLD}', message)
        self.assertIn('the schema stays where the migration left it', message)
        first, second = self.azure.app_patches()
        template = second['properties']['template']
        suffix = template.pop('revisionSuffix')
        self.assertRegex(suffix, r'^b-b{12}-[0-9a-f]{8}$')
        self.assertNotEqual(suffix, first['properties']['template']['revisionSuffix'])
        original = app_resource()['properties']['template']
        del original['revisionSuffix']
        self.assertEqual(template, original, 'the template read at the start, images included')
        self.assertEqual(deploy.images(self.azure.app), deploy.images(app_resource()))

    def assert_diagnosed(self):
        printed = self.printed()
        for expected in ('provisioningState', 'runningState', 'healthState', 'provisioningError',
                         'Container bff failed its startup probe.', 'CrashLoopBackOff', 'restarts 7'):
            self.assertIn(expected, printed)

    def test_a_revision_that_never_gets_ready_is_diagnosed_and_the_app_is_put_back(self):
        self.azure.fate['d'] = 'never'
        with self.assertRaises(RuntimeError) as raised:
            self.deploy()
        self.assertGreaterEqual(self.clock.now, 900)
        self.assert_diagnosed()
        self.assert_put_back(raised)
        self.assertEqual(self.steps(), ['job azurebank-migrate', 'migration', 'app d', 'app b'])
        self.smoke.assert_not_called()

    def test_a_revision_that_ends_failed_is_diagnosed_and_the_app_is_put_back(self):
        self.azure.fate['d'] = 'failed'
        with self.assertRaises(RuntimeError) as raised:
            self.deploy()
        self.assertLess(self.clock.now, 900, 'a failure is not waited out')
        self.assert_diagnosed()
        self.assert_put_back(raised)

    def test_a_failed_smoke_test_is_diagnosed_and_the_app_is_put_back(self):
        self.smoke.side_effect = deploy.SmokeFailed('Smoke test failed: the sign-in probe got 500.')
        self.azure.revisions[BEFORE]['active'] = False
        with self.assertRaisesRegex(RuntimeError, 'the sign-in probe got 500') as raised:
            self.deploy()
        self.assertIn('runningState', self.printed())
        self.assert_put_back(raised)
        self.assertEqual(self.steps(), ['job azurebank-migrate', 'migration', 'app d', 'app b'])

    def test_an_app_read_back_out_of_shape_after_it_moved_is_diagnosed_and_put_back(self):
        # Only the deployment's request drifts: what the put-back sends is the template read at
        # the start, which passed the same check, and never the one that failed it.
        self.azure.drift_app_on_patch = lambda template: (
            template['revisionSuffix'].startswith('d-') and template['scale'].update(maxReplicas=5))
        with self.assertRaisesRegex(RuntimeError, r'\(after its images moved\): '
                                                  'template.scale.maxReplicas is 5') as raised:
            self.deploy()
        self.assertIsInstance(raised.exception.__cause__, deploy.ShapeError)
        self.assertIn('runningState', self.printed())
        self.assert_put_back(raised)
        self.assertEqual(self.steps(), ['job azurebank-migrate', 'migration', 'app d', 'app b'])
        self.smoke.assert_not_called()

    def test_an_unproven_smoke_test_is_never_put_back(self):
        self.smoke.side_effect = deploy.SmokeUnproven('Smoke test unproven: only 429.')
        with self.assertRaises(deploy.SmokeUnproven):
            self.deploy()
        self.assertEqual(len(self.azure.app_patches()), 1)
        self.assertEqual(deploy.images(self.azure.app),
                         {name: f'ghcr.io/gurgant/azurebank-{name}:{NEW}' for name in ('bff', 'api')})

    def test_a_put_back_that_never_gets_ready_says_the_app_may_be_broken(self):
        self.azure.fate.update(d='never', b='never')
        with self.assertRaisesRegex(RuntimeError, 'may be serving a broken revision') as raised:
            self.deploy()
        self.assertNotIn('was put back', str(raised.exception))
        self.assertEqual(len(self.azure.app_patches()), 2)

    def test_a_put_back_azure_refuses_says_the_app_may_be_broken(self):
        self.azure.fate['d'] = 'never'
        self.azure.refuse = lambda method, resource_id, body: (
            method == 'PATCH' and resource_id == APP_ID
            and body['properties']['template']['revisionSuffix'].startswith('b-'))
        with self.assertRaisesRegex(RuntimeError, 'may be serving a broken revision'):
            self.deploy()

    def test_an_old_revision_still_active_stops_before_the_smoke_test_and_puts_nothing_back(self):
        self.azure.keep_old_active = True
        with self.assertRaisesRegex(RuntimeError, f'{BEFORE} was still active'):
            self.deploy()
        self.assertGreaterEqual(self.clock.now, 180)
        self.smoke.assert_not_called()
        self.assertEqual(len(self.azure.app_patches()), 1)

    def test_the_old_revision_is_waited_out_before_the_first_request(self):
        self.azure.reads_before_old_is_inactive = 3
        self.deploy()
        events = self.azure.events
        self.assertEqual(events.count(f'read {BEFORE}'), 3)
        self.assertLess(max(i for i, e in enumerate(events) if e == f'read {BEFORE}'), events.index('smoke'))

    def test_a_revision_answer_that_does_not_say_is_not_taken_for_inactive(self):
        self.azure.keep_old_active = True
        del self.azure.revisions[BEFORE]['active']
        with self.assertRaisesRegex(RuntimeError, 'still active'):
            self.deploy()
        self.smoke.assert_not_called()

    def test_the_app_is_read_again_after_the_put_back(self):
        self.azure.fate['d'] = 'never'
        self.azure.drift_app_on_patch = lambda template: template['scale'].update(maxReplicas=5)
        with self.assertRaisesRegex(deploy.ShapeError, 'after the put-back'):
            self.deploy()

    def test_a_diagnosis_shows_no_address(self):
        # A replica's state is Azure's own text, and it may name where the replica runs.
        self.azure.fate['d'] = 'never'
        self.azure.replicas[0]['properties']['containers'][0]['runningStateDetails'] = (
            'Readiness probe failed: dial tcp 10.250.0.47:8080: connection refused')
        with self.assertRaisesRegex(RuntimeError, f'put back to {OLD}'):
            self.deploy()
        self.assertIn('Waiting (Readiness probe failed: dial tcp <address>:8080: connection refused)',
                      self.printed())
        self.assertNotIn('10.250.0.47', self.printed())

    def test_a_diagnosis_azure_refuses_does_not_hide_the_failure(self):
        self.azure.fate['d'] = 'never'
        self.azure.refuse = lambda method, resource_id, body: '/revisions/' in resource_id
        with self.assertRaisesRegex(RuntimeError, f'put back to {OLD}'):
            self.deploy()
        self.assertIn('Could not read why', self.printed())


class MaskTests(DeployCase):
    def setUp(self):
        super().setUp()
        self.smoke.side_effect = lambda url, **options: print(f'checked {url}')

    def test_in_actions_the_address_is_masked_before_any_line_that_holds_it(self):
        self.deploy(in_actions=True)
        lines = self.printed().splitlines()
        self.assertEqual(lines[0], f'::add-mask::{ADDRESS}', 'the mask is the first line of the run')
        holding = [index for index, line in enumerate(lines) if ADDRESS in line]
        self.assertGreater(len(holding), 1, 'a later line does hold the address')

    def test_outside_actions_no_workflow_command_is_printed(self):
        self.deploy()
        self.assertNotIn('::add-mask::', self.printed())

    def test_an_address_that_is_not_a_host_name_is_never_printed(self):
        strange = 'x.invalid\n::add-mask::something'
        app_part(self.azure, 'configuration', 'ingress').update(fqdn=strange)
        with self.assertRaisesRegex(RuntimeError, 'Unexpected application address') as raised:
            self.deploy(in_actions=True)
        self.assertNotIn('something', str(raised.exception) + self.printed())
        self.assertEqual(self.azure.writes(), [])


class AppOnlyTests(DeployCase):
    """The owner's road back: the app alone, from a terminal."""

    def test_it_moves_the_app_and_touches_no_job(self):
        self.deploy(app_only=True)
        self.assertEqual(self.steps(), ['app d', 'smoke'])
        self.assertEqual([call for call in self.azure.calls if '/jobs/' in call[1]], [])
        self.migration.assert_not_called()
        self.assertEqual(deploy.images(self.azure.app),
                         {name: f'ghcr.io/gurgant/azurebank-{name}:{NEW}' for name in ('bff', 'api')})
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=False)
        # CONTROL: green as written. This road says that no job is touched, and nothing of the
        # pool job: seen red with the demo-off line said here too.
        self.assertNotIn('azurebank-pool', self.printed())

    def test_with_the_demo_on_it_still_reads_no_job_and_tells_the_smoke_test_what_the_app_says(self):
        self.azure.turn_the_demo_on()
        self.deploy(app_only=True)
        self.assertEqual(self.steps(), ['app d', 'smoke'])
        self.assertEqual([call for call in self.azure.calls if '/jobs/' in call[1]], [])
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=True)
        self.assertNotIn('azurebank-pool', self.printed())

    def test_it_is_refused_inside_actions_before_any_call(self):
        with self.assertRaisesRegex(ValueError, 'refused inside GitHub Actions'):
            self.deploy(app_only=True, in_actions=True)
        self.assertEqual(self.azure.calls, [])

    def test_it_keeps_the_gate_and_the_put_back(self):
        self.azure.fate['d'] = 'never'
        with self.assertRaisesRegex(RuntimeError, f'put back to {OLD}'):
            self.deploy(app_only=True)
        self.assertEqual(self.steps(), ['app d', 'app b'])


CHECK_REFUSED = ("--check is refused inside GitHub Actions: it is the owner's read of the running app, "
                 'from a terminal. A workflow run checks the app at the end of its own deployment.')
ANOTHER_REVISION = ('an answer could still come from another revision: nothing was proved, nothing was '
                    "moved. Look at the app's revisions, then check again.")
ALONE = 'is the latest ready one and no other revision is active: what answers now is that revision.'
DEMO_OFF = 'The app says the demo is off: the job azurebank-pool is not read and no secret is listed.'
NO_ADDRESS = ('The app is in shape and reports no address (its ingress holds no host name), so there is '
              'nothing to ask: nothing was proved, nothing was moved.')
EQUAL = ("The pool job's PIN pepper and connection string are the app's: each was listed on both and "
         'compared here, and no value was shown.')
NOT_SHOWN = 'Nothing was moved, and no value was shown. See infra/README.md, "When something fails".'
PEPPER_NAMED = 'PIN pepper (its secret pin-pepper)'
CONNECTION_NAMED = 'connection string (its secret app-connection)'


def checked(latest, demo):
    """The last line of a check that passed."""
    return (f'Checked: {latest} is the latest ready revision and no other is active; '
            + ('the demo is on and the job azurebank-pool is in shape' if demo else 'the demo is off')
            + '; the smoke test passed; nothing was moved.')


def not_settled(state, latest, ready, waited=True):
    """The whole sentence of a check that found the app in no state to be checked."""
    return (f"{'After 300 s the app was' if waited else 'The app is'} not in a state to be checked "
            f'(its last update: {state!r}; its latest revision: {latest!r}; its latest ready '
            f'revision: {ready!r}). Until the latest revision is the latest ready one, {ANOTHER_REVISION}')


def not_alone(found, latest):
    """The whole sentence of a check that could not see the latest revision answering alone."""
    return (f"After 180 s the list of the app's revisions did not show {latest} answering alone "
            f'({found}), so {ANOTHER_REVISION}')


NOT_LISTED = 'the latest ready revision was not in the list'


def differs(*which):
    return (f"The pool job's {' and '.join(which)} {'differs' if len(which) == 1 else 'differ'} from "
            f"the app's. {NOT_SHOWN}")


def not_compared(*why):
    return f"The pool job's secrets could not be compared with the app's: {'; '.join(why)}. {NOT_SHOWN}"


def no_single_value(whose, name):
    return f'{whose} lists no single value of text for {name}'


class CheckTests(DeployCase):
    """`deploy.check`: the owner reads the running app and asks its address what a deployment asks
    at its end, and nothing is moved. It is the read-back of a run of the template, which is
    expected to make a revision outside the script: no migration, no smoke test and no put-back
    follow one."""

    def check(self, **options):
        return deploy.check(SUBSCRIPTION, GROUP, **options)

    def said(self):
        """What was printed, without the clock in front of each line."""
        return [line.split(' ', 1)[1] for line in self.printed().splitlines()]

    def asked(self):
        return [(method, resource_id) for method, resource_id, _ in self.azure.calls]

    def assert_nothing_was_moved(self):
        # No PATCH and no start. A listing of secrets is a POST too, and it asks for no change.
        self.assertEqual([call for call in self.azure.writes() if not call[1].endswith('/listSecrets')], [])
        self.migration.assert_not_called()

    def assert_nothing_else_was_printed(self, *lines):
        # CONTROL: green as written. Every line the check printed, the smoke test being a stand-in
        # in these tests: nothing of a listing is printed beside them, not how many secrets it
        # held and not how long a value was. Seen red with such a line printed after each listing.
        self.assertEqual(self.said(), [f'Revision {BEFORE} {ALONE}', *lines])

    def assert_no_secret_is_shown(self, text, *more):
        """Neither a value a listing answered, nor eight characters in a row of one."""
        values = {value for secrets in self.azure.secrets.values() for value in secrets.values()
                  if isinstance(value, str)} | set(more) | {PEPPER, CONNECTION}
        for value in values:
            for start in range(len(value) - 7):
                self.assertNotIn(value[start:start + 8], text)

    def test_it_reads_the_app_runs_the_smoke_test_and_writes_nothing(self):
        self.check()
        self.assertEqual(self.asked(), [('GET', APP_ID), ('GET', APP_ID + '/revisions')])
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=False)
        self.assertEqual(self.azure.writes(), [])
        self.migration.assert_not_called()
        self.assertEqual(self.said(), [f'Revision {BEFORE} {ALONE}', DEMO_OFF, checked(BEFORE, demo=False)])
        self.assertEqual(self.clock.sleeps, [], 'an app at rest is not waited for')

    def test_with_the_demo_on_it_reads_the_pool_job_too_and_still_moves_nothing(self):
        self.azure.turn_the_demo_on()
        self.check()
        self.assertEqual([call for call in self.asked() if call[0] == 'GET'],
                         [('GET', APP_ID), ('GET', APP_ID + '/revisions'), ('GET', POOL_ID)])
        self.assert_nothing_was_moved()
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=True)
        self.assertEqual(self.said()[-1], checked(BEFORE, demo=True))
        self.assertNotIn('the demo is off', self.printed())

    def test_it_is_refused_inside_actions_before_any_call(self):
        with self.assertRaises(ValueError) as raised:
            self.check(in_actions=True)
        self.assertEqual(str(raised.exception), CHECK_REFUSED)
        self.assertEqual(self.azure.calls, [])
        self.smoke.assert_not_called()
        self.assertEqual(self.printed(), '')

    def test_it_needs_the_subscription_and_the_resource_group(self):
        for subscription, group in (('', GROUP), (SUBSCRIPTION, '')):
            with self.assertRaisesRegex(ValueError, 'AZURE_SUBSCRIPTION_ID and AZURE_RESOURCE_GROUP must be set'):
                deploy.check(subscription, group)
        self.assertEqual(self.azure.calls, [])

    def test_it_checks_the_demo_as_the_app_says_it(self):
        self.check()
        self.azure.turn_the_demo_on()
        self.check()
        self.assertEqual([call.kwargs for call in self.smoke.call_args_list], [{'demo': False}, {'demo': True}])
        # Two containers that disagree, and a value the template never writes, stop the check as
        # they stop a deployment, before the address is asked anything.
        for app, drift in ((flagged('true', 'false'), disagree(['bff'], ['api'])),
                           (flagged('true', 'VALUE-MARKER'), neither('api'))):
            with self.subTest(drift=drift):
                self.azure.app = app
                with self.assertRaises(deploy.ShapeError) as raised:
                    self.check()
                self.assertEqual(str(raised.exception), out_of_shape('The app', drift, when='nothing was moved'))
        self.assertNotIn('VALUE-MARKER', self.printed())
        self.assertEqual(self.smoke.call_count, 2)
        self.assert_nothing_was_moved()

    def test_an_app_out_of_shape_or_with_no_address_is_not_checked(self):
        APP_DRIFTS['template.scale.maxReplicas'](self.azure)
        with self.assertRaises(deploy.ShapeError) as raised:
            self.check()
        self.assertEqual(str(raised.exception), out_of_shape(
            'The app', 'template.scale.maxReplicas is 2', when='nothing was moved'))
        self.azure.app = app_resource()
        app_part(self.azure, 'configuration').update(ingress=None)
        with self.assertRaises(deploy.ShapeError) as raised:
            self.check()
        self.assertEqual(str(raised.exception), out_of_shape(
            'The app', 'configuration.ingress.external is None', 'configuration.ingress.targetPort is None',
            when='nothing was moved'))
        self.azure.app = app_resource()
        app_part(self.azure, 'configuration', 'ingress').update(fqdn='x.invalid\n::add-mask::something')
        with self.assertRaisesRegex(RuntimeError, 'Unexpected application address') as raised:
            self.check()
        self.assertNotIn('add-mask', str(raised.exception) + self.printed())
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()

    def test_an_app_in_shape_that_reports_no_address_is_not_asked_anything(self):
        # An ingress that is in shape and holds no host name passes the shape check and leaves no
        # address to build a request from: the check ends there, before it reads the revisions.
        for what, change in (('no host name', lambda ingress: ingress.pop('fqdn')),
                             ('a host name that is nothing', lambda ingress: ingress.update(fqdn=None))):
            for demo in (False, True):
                with self.subTest(what=what, demo=demo):
                    self.azure.app = app_resource()
                    if demo:
                        self.azure.turn_the_demo_on()
                    change(app_part(self.azure, 'configuration', 'ingress'))
                    self.azure.calls.clear()
                    with self.assertRaises(RuntimeError) as raised:
                        self.check()
                    self.assertIs(type(raised.exception), RuntimeError)
                    self.assertEqual(str(raised.exception), NO_ADDRESS)
                    self.assertEqual(self.asked(), [('GET', APP_ID)], 'nothing else is read of such an app')
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()
        self.assertEqual(self.printed(), '')

    def test_it_waits_until_no_other_revision_is_active_before_it_asks_the_address(self):
        # A run of the template made a revision, and the one before is still active for a while:
        # until it is not, an answer could come from it.
        self.azure.reads_before_old_is_inactive = 3
        after = self.azure.a_new_revision_is_ready()
        self.check()
        events = self.azure.events
        self.assertEqual(events, ['read revisions'] * 3 + ['smoke'])
        self.assertEqual(self.clock.sleeps, [5, 5])
        self.assertEqual(self.said()[0], f'Revision {after} {ALONE}')
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=False)

    def test_another_revision_still_active_at_the_deadline_ends_the_check(self):
        self.azure.keep_old_active = True
        after = self.azure.a_new_revision_is_ready()
        with self.assertRaises(RuntimeError) as raised:
            self.check()
        self.assertIs(type(raised.exception), RuntimeError)
        self.assertEqual(str(raised.exception), not_alone('1 other revision had not gone inactive', after))
        self.assertGreaterEqual(self.clock.now, 180)
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()
        self.assertNotIn('Checked', self.printed())

    def test_a_revision_that_does_not_say_it_is_inactive_is_not_taken_for_inactive(self):
        after = self.azure.a_new_revision_is_ready()
        self.assertIs(self.azure.revisions[BEFORE]['active'], False, 'ARRANGE: the one before is inactive')
        latest = {'name': after, 'properties': {'active': True}}
        lists = {
            'no word of it': [latest, {'name': BEFORE, 'properties': {}}],
            'nothing': [latest, {'name': BEFORE, 'properties': {'active': None}}],
            'text': [latest, {'name': BEFORE, 'properties': {'active': 'false'}}],
            'zero': [latest, {'name': BEFORE, 'properties': {'active': 0}}],
            'properties that are not an object': [latest, {'name': BEFORE, 'properties': 'inactive'}],
            'no properties': [latest, {'name': BEFORE}],
            'an entry that is not an object': [latest, 'azurebank--before'],
            'an entry with no name that is active': [latest, {'properties': {'active': True}}],
        }
        for what, entries in lists.items():
            with self.subTest(what=what), patch.object(self.azure, 'revisions_call', return_value={'value': entries}):
                with self.assertRaises(RuntimeError) as raised:
                    self.check()
                self.assertEqual(str(raised.exception),
                                 not_alone('1 other revision had not gone inactive', after))
        two = [latest, {'name': BEFORE, 'properties': {'active': True}}, {'name': 'azurebank--older', 'properties': {}}]
        with patch.object(self.azure, 'revisions_call', return_value={'value': two}):
            with self.assertRaises(RuntimeError) as raised:
                self.check()
        self.assertEqual(str(raised.exception), not_alone('2 other revisions had not gone inactive', after))
        self.smoke.assert_not_called()

    def test_a_list_that_does_not_hold_the_latest_revision_proves_nothing(self):
        # No entry is active in any of these answers, and none is the list of this app's
        # revisions: a list that could not have shown an active revision does not show there is none.
        inactive = {'name': 'azurebank--older', 'properties': {'active': False}}
        answers = {'an empty list': {'value': []}, 'no list': {}, 'text for a list': {'value': 'x'},
                   'an answer that is a list': [inactive], 'only another revision': {'value': [inactive]}}
        for what, answer in answers.items():
            with self.subTest(what=what), patch.object(self.azure, 'revisions_call', return_value=answer):
                began = self.clock.now
                with self.assertRaises(RuntimeError) as raised:
                    self.check()
                self.assertEqual(str(raised.exception), not_alone(NOT_LISTED, BEFORE))
                self.assertGreaterEqual(self.clock.now - began, 180)
        # Both at once: another revision that is active, and no entry for the latest.
        active = {'name': 'azurebank--older', 'properties': {'active': True}}
        with patch.object(self.azure, 'revisions_call', return_value={'value': [active]}):
            with self.assertRaises(RuntimeError) as raised:
                self.check()
        self.assertEqual(str(raised.exception),
                         not_alone(f'1 other revision had not gone inactive, and {NOT_LISTED}', BEFORE))
        self.smoke.assert_not_called()

    def test_a_list_of_revisions_that_goes_on_on_another_page_proves_nothing(self):
        # The one answer shows no other revision active, and says there is more to read.
        self.azure.more_revisions = True
        with self.assertRaises(RuntimeError) as raised:
            self.check()
        self.assertIs(type(raised.exception), RuntimeError)
        self.assertEqual(str(raised.exception), (
            "The list of the app's revisions came with a link to a next page, and this script reads one "
            'answer: a revision that is still active could be on a page it did not read. An answer could '
            'then still come from another revision: nothing was proved, nothing was moved.'))
        self.assertEqual(self.clock.sleeps, [], 'reading the same page again would show nothing more')
        self.assertEqual(self.asked(), [('GET', APP_ID), ('GET', APP_ID + '/revisions')], 'the link is not followed')
        self.smoke.assert_not_called()

    def test_a_latest_revision_that_is_not_ready_is_not_checked(self):
        states = {
            'the latest is not the latest ready one': ({'latestRevisionName': 'azurebank--after'},
                                                       ('Succeeded', 'azurebank--after', BEFORE)),
            'an update still in progress': ({'provisioningState': 'InProgress'}, ('InProgress', BEFORE, BEFORE)),
            'no revision is ready': ({'latestReadyRevisionName': None}, ('Succeeded', BEFORE, None)),
            'no revision at all': ({'latestRevisionName': None, 'latestReadyRevisionName': None},
                                   ('Succeeded', None, None)),
        }
        for what, (found, told) in states.items():
            with self.subTest(what=what):
                self.azure.app = app_resource()
                self.azure.app['properties'].update(found)
                began = self.clock.now
                with self.assertRaises(RuntimeError) as raised:
                    self.check()
                self.assertIs(type(raised.exception), RuntimeError)
                self.assertEqual(str(raised.exception), not_settled(*told))
                self.assertGreaterEqual(self.clock.now - began, 300)
        self.assertEqual([call for call in self.asked() if call != ('GET', APP_ID)], [],
                         'nothing else is read of an app that is in no state to be checked')
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()

    def test_an_update_that_ended_failed_is_not_waited_out(self):
        for state in ('Failed', 'Canceled'):
            with self.subTest(state=state):
                self.azure.app['properties'].update(provisioningState=state)
                with self.assertRaises(RuntimeError) as raised:
                    self.check()
                self.assertEqual(str(raised.exception), not_settled(state, BEFORE, BEFORE, waited=False))
        self.assertEqual(self.clock.sleeps, [])
        self.smoke.assert_not_called()

    def test_an_update_that_ends_while_it_is_waited_for_is_checked_as_it_ended(self):
        # The run of the template that turns the demo on is still going when the check starts: the
        # app is read again until it has settled, and what it says then is what is checked.
        self.azure.turn_the_demo_on()
        settled = self.azure.app_call
        unsettled = flagged('false', 'false')
        unsettled['properties'].update(provisioningState='InProgress')
        answers = [unsettled, unsettled]
        with patch.object(self.azure, 'app_call',
                          side_effect=lambda method, body: answers.pop() if answers else settled(method, body)):
            self.check()
        self.assertEqual(self.asked()[:4], [('GET', APP_ID)] * 3 + [('GET', APP_ID + '/revisions')])
        self.assertEqual(self.clock.sleeps, [5, 5])
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=True)
        self.assertNotIn('the demo is off', self.printed())

    def test_with_the_demo_on_it_checks_the_pool_jobs_shape(self):
        self.azure.turn_the_demo_on()
        named, drift = POOL_DRIFTS['schedules_expression']
        drift(self.azure)
        with self.assertRaises(deploy.ShapeError) as raised:
            self.check()
        self.assertEqual(str(raised.exception),
                         out_of_shape('The job azurebank-pool', named, when='nothing was moved'))
        self.assertNotIn('* * * * *', str(raised.exception) + self.printed())
        self.smoke.assert_not_called()
        self.assertEqual([call for call in self.asked() if 'listSecrets' in call[1]], [],
                         'no secret is listed for a job that is out of shape')
        self.assert_nothing_was_moved()

    def test_with_the_demo_on_a_pool_job_that_cannot_be_read_ends_the_check(self):
        self.azure.turn_the_demo_on()
        self.azure.refuse = lambda method, resource_id, body: resource_id == POOL_ID
        self.azure.refusal = 'Not Found: ResourceNotFound'
        with self.assertRaises(RuntimeError) as raised:
            self.check()
        self.assertEqual(str(raised.exception), (
            'The app says the demo is on, and the job azurebank-pool could not be read: Not Found: '
            'ResourceNotFound. Nothing was changed. See infra/README.md, "When something fails".'))
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()

    def test_with_the_demo_off_no_job_is_read_and_no_secret_is_listed(self):
        # CONTROL: green before this change: a check that does nothing passes it. It is the guard
        # that a check of an app with the demo off lists nothing. Whether a job of that name
        # exists is not asked: here one does.
        self.azure.jobs['azurebank-pool'] = pool_resource()
        self.check()
        self.assertEqual([call for call in self.asked() if 'azurebank-pool' in call[1] or 'listSecrets' in call[1]],
                         [])
        self.assertEqual(self.azure.writes(), [])

    def test_a_smoke_test_that_fails_or_proves_nothing_ends_the_check_and_nothing_is_put_back(self):
        # The smoke test words its verdicts for a deployment. A check deployed nothing.
        for failure in (deploy.SmokeFailed('Smoke test failed: the sign-in probe got 500.'),
                        deploy.SmokeUnproven('Smoke test unproven: only 429. Deploy again.')):
            with self.subTest(failure=type(failure).__name__):
                self.smoke.side_effect = failure
                with self.assertRaises(type(failure)) as raised:
                    self.check()
                self.assertEqual(str(raised.exception), (
                    f'{failure} This was --check, not a deployment: nothing was moved and nothing is put '
                    'back; where that says to deploy again, check again.'))
        self.assertEqual(self.azure.writes(), [])
        self.assertNotIn('Checked', self.printed())

    def test_from_the_command_line_it_needs_no_image_tag_and_a_check_that_fails_exits_with_its_sentence(self):
        # CONTROL: green as written: it was written after the check. Seen red with the wait for
        # the other revisions taken out of the check (SystemExit not raised).
        environment = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP}
        with patch.dict(deploy.os.environ, environment, clear=True):
            self.assertIsNone(deploy.main(['--check']))
            self.assertEqual(self.said()[-1], checked(BEFORE, demo=False))
            self.azure.keep_old_active = True
            after = self.azure.a_new_revision_is_ready()
            with self.assertRaises(SystemExit) as raised:
                deploy.main(['--check'])
        self.assertEqual(raised.exception.code, not_alone('1 other revision had not gone inactive', after))
        self.assertEqual(self.azure.writes(), [])

    def test_with_the_demo_on_it_says_the_jobs_pepper_and_string_are_the_apps_and_shows_neither(self):
        self.azure.turn_the_demo_on()
        self.check()
        self.assert_nothing_else_was_printed(EQUAL, checked(BEFORE, demo=True))
        self.assert_no_secret_is_shown(self.printed())
        # Each listed once, after the job was found in shape and before the address is asked anything.
        self.assertEqual(self.azure.events, ['read revisions', 'read secrets of azurebank',
                                             'read secrets of azurebank-pool', 'smoke'])
        self.assertEqual(self.azure.writes(), [('POST', APP_ID + '/listSecrets'), ('POST', POOL_ID + '/listSecrets')])
        self.assertEqual([call for call in self.azure.versions if call[2] != deploy.API_VERSION], [])

    def test_a_pepper_that_differs_fails_the_check_and_is_never_shown(self):
        self.azure.turn_the_demo_on()
        other = 'Ua8hGq3zXw6nTm1bRv4cYk7pLd0sFj5e'
        # One sentence for every way to differ: it says which secret, and nothing of how.
        for what, value in (('another value', other), ('its last character', PEPPER[:-1] + '#'),
                            ('its case', PEPPER.swapcase()), ('a space after it', PEPPER + ' '),
                            ('only its beginning', PEPPER[:16]), ('twice over', PEPPER * 2)):
            with self.subTest(what=what):
                self.clear()
                self.azure.secrets[POOL_ID]['pin-pepper'] = value
                with self.assertRaises(RuntimeError) as raised:
                    self.check()
                self.assertIs(type(raised.exception), RuntimeError)
                self.assertEqual(str(raised.exception), differs(PEPPER_NAMED))
                self.assert_no_secret_is_shown(str(raised.exception) + self.printed(), other)
                self.assert_nothing_else_was_printed()
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()

    def test_a_connection_string_that_differs_and_both_that_differ_are_named(self):
        self.azure.turn_the_demo_on()
        other = 'Oe2xLm9vQa6tZs3dWk8cRj5yNh1uBf4g;Ti7pGb0wVq4n'
        for pepper, connection, which in ((PEPPER, other, [CONNECTION_NAMED]),
                                          (other, other, [PEPPER_NAMED, CONNECTION_NAMED])):
            with self.subTest(which=which):
                self.clear()
                self.azure.secrets[APP_ID].update({'pin-pepper': pepper, 'app-connection': connection})
                with self.assertRaises(RuntimeError) as raised:
                    self.check()
                self.assertEqual(str(raised.exception), differs(*which))
                self.assert_no_secret_is_shown(str(raised.exception) + self.printed(), other)
                self.assert_nothing_else_was_printed()
        self.smoke.assert_not_called()

    def test_only_the_two_secrets_the_job_holds_are_compared(self):
        # The app holds seven secrets the job does not, and the job may hold one the app does not:
        # none of them is compared, and none is a difference.
        self.azure.turn_the_demo_on()
        self.azure.secrets[APP_ID].update({'jwt-secret': 'Fs1dGa4hJq7kLw0zXe3cVr6bNt9mYu2i'})
        self.azure.secrets[POOL_ID].update({'another-secret': 'Ce5vBr8nMt1yQu4iWo7pEa0sDl3fGk6h'})
        self.check()
        self.assert_nothing_else_was_printed(EQUAL, checked(BEFORE, demo=True))
        self.assert_no_secret_is_shown(self.printed())

    def test_a_value_no_encoder_can_write_is_compared_and_never_quoted(self):
        # CONTROL: green as written. A listing is JSON, and JSON can spell half a character (a
        # lone surrogate). The comparison must not end in an encoder's own words: they quote the
        # character and say where in the value it stood, and the command line prints them. Seen
        # red with the two values encoded by the plain encoder.
        self.azure.turn_the_demo_on()
        odd = PEPPER[:9] + json.loads('"\\udc80"') + PEPPER[9:]
        self.azure.secrets[APP_ID]['pin-pepper'] = odd
        self.azure.secrets[POOL_ID]['pin-pepper'] = odd.replace('\udc80', '\udc81')
        environment = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP}
        with patch.dict(deploy.os.environ, environment, clear=True):
            with self.assertRaises(SystemExit) as raised:
                deploy.main(['--check'])
            self.assertEqual(raised.exception.code, differs(PEPPER_NAMED))
            self.assert_nothing_else_was_printed()
            self.clear()
            self.azure.secrets[POOL_ID]['pin-pepper'] = odd
            self.assertIsNone(deploy.main(['--check']))
        self.assert_nothing_else_was_printed(EQUAL, checked(BEFORE, demo=True))
        self.assert_nothing_was_moved()

    def test_a_listing_that_fails_fails_the_check(self):
        self.azure.turn_the_demo_on()
        for whose, resource_id in (('the app', APP_ID), ('the job azurebank-pool', POOL_ID)):
            for refusal in ('Forbidden: AuthorizationFailed.',
                            f'The Azure CLI gave no answer in 180 s (POST {deploy.short(resource_id)}/listSecrets).'):
                with self.subTest(whose=whose, refusal=refusal):
                    self.clear()
                    self.azure.refusal = refusal
                    self.azure.refuse = lambda method, target, body, wanted=resource_id + '/listSecrets': (
                        target == wanted)
                    with self.assertRaises(RuntimeError) as raised:
                        self.check()
                    self.assertIs(type(raised.exception), RuntimeError, 'the check says what it means')
                    self.assertEqual(str(raised.exception), (
                        f"The secrets of {whose} could not be listed, so the pool job's secrets could not "
                        f"be compared with the app's: {refusal.rstrip('.')}. Nothing was moved. See "
                        'infra/README.md, "When something fails".'))
                    self.assert_nothing_else_was_printed()
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()

    def test_a_listing_that_cannot_be_read_fails_the_check_in_its_own_words(self):
        # The CLI answered, and what it printed is not a listing: it stops short. A decoder's own
        # words say where in the answer it stopped, and of a listing not even that is shown: the
        # place is worked out from what stood before it. The second error is another decoder's,
        # one of bytes. It stands for any other ValueError a read could end in: that such a one
        # reaches the check from the CLI is not known.
        self.azure.turn_the_demo_on()
        cut = json.dumps({'value': [{'name': 'pin-pepper', 'value': PEPPER}]})[:60]
        with self.assertRaises(json.JSONDecodeError) as stops_short:
            json.loads(cut)
        with self.assertRaises(UnicodeDecodeError) as not_text:
            (cut.encode() + b'\xff').decode()
        self.assertIn('(char 43)', str(stops_short.exception), 'ARRANGE: the decoder says where it stopped')
        self.assertIn('position 60', str(not_text.exception), 'ARRANGE: the decoder says where it stopped')
        listing = self.azure.secrets_call
        for whose, resource_id in (('the app', APP_ID), ('the job azurebank-pool', POOL_ID)):
            for unread in (stops_short.exception, not_text.exception):
                with self.subTest(whose=whose, unread=type(unread).__name__), patch.object(
                        self.azure, 'secrets_call', side_effect=lambda target, body: (
                            raise_(unread) if target == resource_id else listing(target, body))):
                    self.clear()
                    # Either kind is one the command line prints as it is.
                    with self.assertRaises((RuntimeError, ValueError)) as raised:
                        self.check()
                    self.assertIs(type(raised.exception), RuntimeError, 'the check says what it means')
                    self.assertEqual(str(raised.exception), (
                        f"The secrets of {whose} could not be listed, so the pool job's secrets could not "
                        "be compared with the app's: the answer could not be read. Nothing was moved. See "
                        'infra/README.md, "When something fails".'))
                    self.assert_no_secret_is_shown(str(raised.exception) + self.printed())
                    self.assert_nothing_else_was_printed()
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()

    def test_a_listing_that_does_not_hold_one_value_for_each_secret_cannot_be_compared(self):
        self.azure.turn_the_demo_on()
        app, job = 'the app', 'the job azurebank-pool'
        pair = [{'name': 'pin-pepper', 'value': PEPPER}, {'name': 'app-connection', 'value': CONNECTION}]

        def without(name, *instead):
            return [entry for entry in pair if entry['name'] != name] + list(instead)

        # (what the app's listing answers, what the job's answers) -> why they cannot be compared
        cases = {
            'the job lacks the pepper': (pair, without('pin-pepper'), [no_single_value(job, 'pin-pepper')]),
            'the app lacks the string': (without('app-connection'), pair, [no_single_value(app, 'app-connection')]),
            'no value on the job': (pair, without('pin-pepper', {'name': 'pin-pepper'}),
                                    [no_single_value(job, 'pin-pepper')]),
            'a value that is not text': (pair, without('pin-pepper', {'name': 'pin-pepper', 'value': 7}),
                                         [no_single_value(job, 'pin-pepper')]),
            # Two that hold nothing are not "the app's": nothing was compared.
            'nothing on both': (without('pin-pepper', {'name': 'pin-pepper', 'value': ''}),) * 2 + (
                [no_single_value(app, 'pin-pepper'), no_single_value(job, 'pin-pepper')],),
            'a name twice': (pair + [{'name': 'pin-pepper', 'value': PEPPER}], pair,
                             [no_single_value(app, 'pin-pepper')]),
            'no list': ({'secrets': pair}, pair, [no_single_value(app, 'pin-pepper'),
                                                 no_single_value(app, 'app-connection')]),
            'entries that are not objects': (pair, ['pin-pepper', 'app-connection'],
                                             [no_single_value(job, 'pin-pepper'),
                                              no_single_value(job, 'app-connection')]),
        }
        for what, (of_the_app, of_the_job, why) in cases.items():
            answers = {APP_ID: of_the_app, POOL_ID: of_the_job}
            with self.subTest(what=what), patch.object(
                    self.azure, 'secrets_call', side_effect=lambda resource_id, body: (
                        answers[resource_id] if isinstance(answers[resource_id], dict)
                        else {'value': answers[resource_id]})):
                self.clear()
                with self.assertRaises(RuntimeError) as raised:
                    self.check()
                self.assertIs(type(raised.exception), RuntimeError)
                self.assertEqual(str(raised.exception), not_compared(*why))
                self.assert_no_secret_is_shown(str(raised.exception) + self.printed())
                self.assert_nothing_else_was_printed()
        self.smoke.assert_not_called()
        self.assert_nothing_was_moved()


class SmokeInTheCheckTests(Offline):
    """The real smoke test inside the real check: what the address answers decides how the check
    ends, and whatever it answers nothing is moved and nothing is put back."""

    def setUp(self):
        super().setUp()
        self.azure = FakeAzure(self.out)
        self.start(patch('deploy.rest', self.azure.rest))

    def test_the_four_answers_of_a_demo_pass_and_the_check_says_what_it_checked(self):
        self.azure.turn_the_demo_on()
        site = demo_site(REFUSED)
        self.start(patch('deploy.fetch', site.fetch))
        deploy.check(SUBSCRIPTION, GROUP)
        self.assertEqual([path for path, _ in site.requests],
                         ['/', '/health/ready', '/bff/auth/login', '/bff/auth/register'])
        lines = [line.split(' ', 1)[1] for line in self.printed().splitlines()]
        self.assertIn('It is the public demo', lines[-2])
        self.assertEqual(lines[-1], checked(BEFORE, demo=True))
        self.assertEqual([call for call in self.azure.writes() if not call[1].endswith('/listSecrets')], [])

    def test_an_answer_that_is_wrong_ends_the_check_and_the_app_is_left_as_it_is(self):
        self.azure.turn_the_demo_on()
        before = copy.deepcopy(self.azure.app)
        for site, verdict, said in (
                (demo_site(REFUSED, register=OPEN), deploy.SmokeFailed, 'the registration probe expected 403'),
                (demo_site(REFUSED, page=(200, 'text/html', SPA)), deploy.SmokeFailed, 'does not carry the tag'),
                (demo_site(LIMITED), deploy.SmokeUnproven, 'the sign-in probe got 429')):
            with self.subTest(said=said):
                self.start(patch('deploy.fetch', site.fetch))
                with self.assertRaises(verdict) as raised:
                    deploy.check(SUBSCRIPTION, GROUP)
                self.assertIn(said, str(raised.exception))
                self.assertTrue(str(raised.exception).endswith(
                    'This was --check, not a deployment: nothing was moved and nothing is put back; where '
                    'that says to deploy again, check again.'), str(raised.exception))
                self.assertNotIn('PLANTED', str(raised.exception) + self.printed())
        self.assertEqual([call for call in self.azure.writes() if not call[1].endswith('/listSecrets')], [])
        self.assertEqual(self.azure.app, before)
        self.assertNotIn('Checked', self.printed())


POOL_RUN_REFUSED = ('--pool-run is refused inside GitHub Actions: the pool job runs on its schedule, and a run '
                    'beside the schedule is started by the owner, from a terminal. A deployment moves that '
                    "job's image and never starts it.")
LINE_KEPT = 'What it printed is kept in the log workspace (infra/README.md, "Reading the logs").'
READ_WITH = ("`python infra/deploy.py --pool-log pool-run` reads the run's verdict again and prints the run's "
             'lines, once the log workspace has them: a line takes minutes to arrive.')
POOL_LOG_REFUSED = ('--pool-log is refused inside GitHub Actions: what the containers printed is read by the '
                    'owner, from a terminal, and never reaches a public log.')
START = ('POST', POOL_ID + '/start')
RUNS = ('GET', POOL_ID + '/executions')
# The words of the pool job's map for the codes a start by hand is tried with here, and the count
# of the run's summary line each signal points at.
LOW = ('done, with a signal: the pool was low (PoolLow)', 'was')
EMPTY = ('done, with a signal: the pool was empty (PoolEmpty)', 'was')
CEILING = ("done, with a signal: the day's claims held the top-up back (ClaimCeiling)", 'ceiling')
NOT_FINISHED = ('did not finish, and left no summary line; read its last line before it is started again', None)
REFUSED_ITSELF = ("refused before anything was opened; the job's configuration must change", None)
NOT_BUILT = ('needs a look: a copy could not be built (TopUpIncomplete)', 'free')
FOREIGN = ('needs a look: a user outside every copy exists (ForeignUsers)', 'foreignUsers')
NOT_DELETED = ('needs a look: a copy could not be deleted (DeleteFailed)', 'failed')
NOT_THE_TOOLS = ('not a code the tool itself exits with', None)


def pool_verdict(status='Succeeded', code=0, words='done', name='pool-run'):
    """The verdict of a run of the pool job that ended, as the owner's terminal shows it."""
    return (f'Verdict: execution {name}: {status}, started 2026-10-02T18:00:03Z, ended 2026-10-02T18:00:09Z '
            f'(6 s), exit code {code} ({words}), reason Completed.')


def points_at(count):
    return (f" The code is a signal to read the count '{count}' on the run's summary line "
            '(docs/runbooks/demo-pool.md, "The line").')


def where_printed(name):
    """Where what a run printed is read: the command, for a run whose name the command takes."""
    return READ_WITH if name == 'execution pool-run' else LINE_KEPT


def ended_well(code=0, words='done', count=None, name='execution pool-run', status='Succeeded'):
    """The last line of a start by hand whose run ended well, whole. That the exit code decided
    is said where it could be doubted: of a signal, and of a run whose verdict shows a status
    that says it failed."""
    decides = ' The exit code decides here, whatever status Azure gave the execution.'
    return (f'The pool run ended well ({name}: exit code {code}, {words}).'
            + (decides if count or status in ('Failed', 'Stopped', 'Degraded') else '')
            + (points_at(count) if count else '') + f' {where_printed(name)}')


def not_well(code, words, count=None, name='execution pool-run'):
    """The whole sentence of a start by hand whose run did not end well."""
    return (f'The pool run did not end well ({name}: exit code {code}, {words}). It was not started again.'
            + (points_at(count) if count else '') + f' {where_printed(name)}')


NO_CODE = ('The pool run ended and Azure reported no exit code for it (execution pool-run: exit code not '
           f'reported). How it ended is not guessed from its status, and it was not started again. {READ_WITH}')


def beside_another_pool_run(name, state, timeout=600):
    """The whole sentence of a start by hand that is refused beside a run in progress."""
    return (f'Execution {name} of the job azurebank-pool is {state}: a pool run may still be in '
            'progress, and a second run is not started beside one. Nothing was started. A run is '
            f"expected to end within the job's timeout ({timeout} s): start it again after that. If it "
            "is refused again then, the owner reads the job's executions and stops that one "
            '(infra/README.md, "When something fails").')


def read_failed(why, name='execution pool-run'):
    """The whole sentence of a start by hand whose run was started and then could not be read."""
    reads = ' How it ends is read with `python infra/deploy.py --pool-log pool-run`.'
    return (f"The pool run was started ({name}), and a read of the job's executions failed while its end was "
            f'waited for: {why}. It may still be in progress, and it was not started again.'
            + (reads if name == 'execution pool-run' else ''))


# What stands for Azure's words when an answer came, in a shape this script does not read: nothing
# of the answer is shown.
NOT_THE_LIST = 'the answer was not the list of executions this script reads'
START_NOT_READ = ('The job azurebank-pool was asked to start once, and an answer was not in the shape this '
                  'script reads before the run it made was known. The start is not sent again. Whether a run '
                  "began is read from the job's executions, before any second start (infra/README.md, \"When "
                  'something fails").')


def not_ended(name='pool-run', waited=720):
    """The whole sentence of a start by hand whose run was not seen ending."""
    reads = 'How it ends is read with `python infra/deploy.py --pool-log pool-run`. ' if name == 'pool-run' else ''
    return (f'Timed out waiting for execution {name} of the job azurebank-pool: it had not ended {waited} s '
            "after it was started here (the job's timeout and two minutes). It was not started again. While "
            f'it is listed as running, a deployment and a second start are refused. {reads}The owner can stop '
            'it: az containerapp job stop --name azurebank-pool --resource-group group --job-execution-name '
            + (name if name == 'pool-run' else '<its name>'))


class PoolRunTests(DeployCase):
    """`deploy.pool_run`: the owner starts the pool job once, by hand, and waits for that exact
    execution: the first fill, or a refill between two runs of the schedule. How the run ended is
    told by its exit code and never by its status alone."""

    def setUp(self):
        super().setUp()
        self.azure.turn_the_demo_on()
        self.azure.pool_outcome = ends_as(pool_finished())

    def pool_run(self, **options):
        return deploy.pool_run(SUBSCRIPTION, GROUP, **options)

    def ended(self):
        """How a start by hand ended: the name of its run, or the error it raised, so that a run
        expected to end well and refused instead fails its test on what the refusal says."""
        try:
            return self.pool_run()
        except RuntimeError as error:
            return error

    def said(self):
        """What was printed, without the clock in front of each line."""
        return [line.split(' ', 1)[1] for line in self.printed().splitlines()]

    def asked(self):
        return [(method, resource_id) for method, resource_id, _ in self.azure.calls]

    def again(self, *outcome, runs=()):
        """Forget the run before: what was printed and asked, and the job's executions."""
        self.clear()
        self.azure.calls.clear()
        self.azure.pool_runs = list(runs)
        self.azure.pool_to_come = []
        self.azure.pool_outcome = list(outcome)

    def test_it_starts_the_job_once_and_waits_for_its_own_execution(self):
        self.azure.pool_runs = [pool_finished('an-earlier-run')]
        self.azure.pool_outcome = [execution('pool-run', 'Running'), *ends_as(pool_finished())]
        self.assertEqual(self.ended(), 'pool-run')
        # The job, its executions once, the one start, and its executions until that run ended.
        self.assertEqual(self.asked(), [('GET', POOL_ID), RUNS, START, RUNS, RUNS, RUNS])
        # CONTROL: green as written. The start carries no body. A start is expected to take a
        # container for that one execution, which the shape check before the start never reads:
        # what runs is the job as the template wrote it, with no argument and no setting of the
        # start's own. Seen red with a start of the pool job that carries a container.
        self.assertEqual([body for method, _, body in self.azure.calls if method == 'POST'], [None])
        self.assertEqual(self.said(), ['Pool run execution pool-run started.',
                                       'Pool run execution pool-run: Running.',
                                       'Pool run execution pool-run: Succeeded.',
                                       pool_verdict(), ended_well()])
        # The exit code is read once, last, with the version that carries it.
        self.assertEqual([call for call in self.azure.versions if call[2] != deploy.API_VERSION],
                         [('GET', 'executions', '2026-07-01')])
        self.assertEqual(self.azure.versions[-1], ('GET', 'executions', '2026-07-01'))
        self.assertEqual(self.clock.sleeps, [5])
        # Nothing of the app is read, and nothing is moved: the job runs the image it had.
        self.assertEqual(self.steps(), ['pool run'])
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-pool']),
                         {'pool': f'ghcr.io/gurgant/azurebank-tools:{OLD}'})
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_a_run_that_found_the_pool_short_ends_well_and_says_so(self):
        # How Azure words an execution whose container exits with one of these codes has not been
        # seen. `Failed` is what is expected, and the code decides whatever the status says.
        for code, (words, count) in {10: LOW, 11: EMPTY, 15: CEILING}.items():
            for status in ('Failed', 'Succeeded'):
                with self.subTest(code=code, status=status):
                    self.again(*ends_as(pool_finished(status=status, code=code)))
                    self.assertEqual(self.ended(), 'pool-run')
                    self.assertEqual(self.said()[-2:], [pool_verdict(status, code, words),
                                                        ended_well(code, words, count)])
                    self.assertIn('done, with a signal', self.said()[-1])
                    self.assertIn(f"read the count '{count}'", self.said()[-1])
                    self.assertEqual(self.azure.writes(), [START])

    def test_a_run_that_needs_a_look_fails_and_names_its_code(self):
        # 137 stands for a code the tool does not exit with: a container ended from outside.
        failures = {1: NOT_FINISHED, 2: REFUSED_ITSELF, 12: NOT_BUILT, 13: FOREIGN, 14: NOT_DELETED,
                    137: NOT_THE_TOOLS}
        for code, (words, count) in failures.items():
            with self.subTest(code=code):
                self.again(*ends_as(pool_finished(status='Failed', code=code)))
                with self.assertRaises(RuntimeError) as raised:
                    self.pool_run()
                self.assertIs(type(raised.exception), RuntimeError)
                self.assertEqual(str(raised.exception), not_well(code, words, count))
                self.assertIn(f'exit code {code}, {words}', str(raised.exception))
                self.assertTrue(str(raised.exception).endswith(READ_WITH), 'where the line is read')
                self.assertEqual(self.said()[-1], pool_verdict('Failed', code, words))
                self.assertEqual(self.azure.writes(), [START])

    def test_the_exit_code_decides_and_never_the_status_alone(self):
        for status in ('Succeeded', 'Failed', 'Stopped', 'Degraded'):
            with self.subTest(status=status, code=0):
                self.again(*ends_as(pool_finished(status=status)))
                self.assertEqual(self.ended(), 'pool-run')
                # A run that ended well under a status that says it failed is told so: the line
                # above it shows that status, and the two would read as a contradiction.
                self.assertEqual(self.said()[-2:], [pool_verdict(status), ended_well(status=status)])
                self.assertEqual('The exit code decides here, whatever status' in self.said()[-1],
                                 status != 'Succeeded')
            with self.subTest(status=status, code=13):
                self.again(*ends_as(pool_finished(status=status, code=13)))
                with self.assertRaises(RuntimeError) as raised:
                    self.pool_run()
                self.assertEqual(str(raised.exception), not_well(13, *FOREIGN))
        # The status in question is the one the verdict's line shows, read with the later version:
        # the last line follows the line above it, whatever the polling saw.
        with self.subTest('the polling saw Succeeded and the verdict shows Failed'):
            self.again(execution('pool-run', 'Succeeded'), pool_finished(status='Failed'))
            self.assertEqual(self.ended(), 'pool-run')
            self.assertEqual(self.said()[-2:], [pool_verdict('Failed'), ended_well(status='Failed')])
        with self.subTest('the polling saw Failed and the verdict shows Succeeded'):
            # CONTROL: green as written. Seen red with the polling's status in the verdict's place.
            self.again(execution('pool-run', 'Failed'), pool_finished())
            self.assertEqual(self.ended(), 'pool-run')
            self.assertEqual(self.said()[-2:], [pool_verdict(), ended_well()])
        with self.subTest('the verdict shows a status that is not text'):
            # CONTROL: green as written. A status of another shape is not one that says the run
            # failed, and is not compared as one. Seen red with the status taken as Azure gave it.
            odd = pool_finished()
            odd['properties']['status'] = ['Failed']
            self.again(execution('pool-run', 'Succeeded'), odd)
            try:
                end = self.pool_run()
            except Exception as error:  # a TypeError is what this half is for
                end = error
            self.assertEqual(end, 'pool-run')
            self.assertIn(' execution pool-run: status not reported, ', self.said()[-2])
            self.assertEqual(self.said()[-1], ended_well())

    def test_the_code_that_decides_is_the_one_of_the_jobs_own_container(self):
        # CONTROL: green as written. An execution that lists a second container, which nothing in
        # this folder writes: the code that decides is the one of `pool`, as on the verdict's
        # line. Seen red with the migrate job's container read in its place.
        two = pool_finished(status='Failed', code=13)
        two['properties']['detailedStatus']['replicas'][0]['containers'].append({'name': 'migrate', 'code': 0})
        self.azure.pool_outcome = ends_as(two)
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        self.assertEqual(str(raised.exception), not_well(13, *FOREIGN))
        self.assertEqual(self.said()[-1], pool_verdict('Failed', 13, FOREIGN[0]))

    def test_a_code_azure_did_not_report_is_a_failure_and_is_not_guessed(self):
        started = '2026-10-02T18:00:03Z'
        for status in ('Succeeded', 'Failed'):
            # Neither read carries a code; a code that is text is not one; two containers and no
            # `pool` among them.
            bare = execution('pool-run', status, started)
            two = pool_finished(status=status)
            two['properties']['detailedStatus']['replicas'][0]['containers'] = [
                {'name': 'one', 'code': 0}, {'name': 'two', 'code': 0}]
            for what, outcome in (('no code on either read', [bare, bare]),
                                  ('a code that is text', ends_as(pool_finished(status=status, code='0'))),
                                  ('two containers, neither the pool', ends_as(two))):
                with self.subTest(status=status, what=what):
                    self.again(*outcome)
                    with self.assertRaises(RuntimeError) as raised:
                        self.pool_run()
                    self.assertIs(type(raised.exception), RuntimeError)
                    self.assertEqual(str(raised.exception), NO_CODE)
                    self.assertIn(f': {status}, started 2026-10-02T18:00:03Z, ', self.said()[-1])
                    self.assertIn(', exit code not reported, ', self.said()[-1])
                    self.assertEqual(self.azure.writes(), [START])

    def test_a_read_of_the_exit_code_that_fails_or_lacks_the_run_is_a_code_not_reported(self):
        # The polling saw the run end. The one read with the later version is refused, or answers
        # without that run: what is known is a status, and a status alone decides nothing.
        answer = self.azure.pool_call
        for what in ('refused', 'without the run'):
            with self.subTest(what=what):
                self.again(*ends_as(pool_finished()))
                reads = []

                def third_read(method, tail, what=what, reads=reads):
                    reads.append((method, tail))
                    if reads.count(('GET', 'executions')) == 3 and (method, tail) == ('GET', 'executions'):
                        if what == 'refused':
                            raise deploy.AzError('Bad Request: NoRegisteredProviderFound for the API version')
                        return {'value': [pool_finished('another-run', code=10)]}
                    return answer(method, tail)

                with patch.object(self.azure, 'pool_call', side_effect=third_read):
                    with self.assertRaises(RuntimeError) as raised:
                        self.pool_run()
                self.assertEqual(str(raised.exception), NO_CODE)
                self.assertEqual(self.azure.versions[-1], ('GET', 'executions', '2026-07-01'))
                self.assertEqual(self.said()[-1], 'Verdict: execution pool-run: Succeeded, start not reported, '
                                                  'end not reported, exit code not reported, no reason given.')

    def test_a_run_in_progress_blocks_the_start(self):
        now = datetime.datetime.now(datetime.timezone.utc)
        five_minutes_ago = (now - datetime.timedelta(seconds=300)).isoformat()
        for state, started in (('Running', None), ('Processing', None), ('Unknown', now.isoformat()),
                               ('Unknown', five_minutes_ago)):
            with self.subTest(state=state, started=started):
                self.again(*ends_as(pool_finished()),
                           runs=[pool_finished('an-earlier-run'), execution('another-run', state, started)])
                with self.assertRaises(RuntimeError) as raised:
                    self.pool_run()
                self.assertIs(type(raised.exception), RuntimeError)
                self.assertEqual(str(raised.exception), beside_another_pool_run('another-run', state))
                self.assertEqual(self.asked(), [('GET', POOL_ID), RUNS])
                self.assertEqual(self.printed(), '')

    def test_a_run_that_blocks_the_start_is_named_only_in_the_shape_expected(self):
        recent = datetime.datetime.now(datetime.timezone.utc).isoformat()
        unknown = 'in a state this script does not know'
        withheld = 'whose name is withheld'
        for blocking, name, state in ((execution(STRANGE, 'Running'), withheld, 'Running'),
                                      (execution('another-run', STRANGE, recent), 'another-run', unknown),
                                      (execution('stuck', 'Pending'), 'stuck', unknown)):
            with self.subTest(name=name, state=state):
                self.again(*ends_as(pool_finished()), runs=[blocking])
                with self.assertRaises(RuntimeError) as raised:
                    self.pool_run()
                self.assertEqual(str(raised.exception), beside_another_pool_run(name, state))
                for unwanted in ('\n', '::', 'add-mask', 'Pending'):
                    self.assertNotIn(unwanted, str(raised.exception))
                self.assertEqual(self.azure.writes(), [])

    def test_runs_that_ended_or_are_too_old_to_be_alive_do_not_block_the_start(self):
        # CONTROL: green as written, the two halves about the age. A run in a state nobody named is
        # taken for alive for the pool job's own timeout and two minutes, as a deployment takes it.
        now = datetime.datetime.now(datetime.timezone.utc)
        stale = (now - datetime.timedelta(days=2)).isoformat()
        self.azure.pool_runs = [pool_finished('one'), pool_finished('two', status='Failed', code=10),
                                execution('stale', 'Unknown', stale)]
        self.assertEqual(self.ended(), 'pool-run')
        old = execution('old', 'Unknown', (now - datetime.timedelta(seconds=800)).isoformat())
        with self.subTest('840 s: a run that began 800 s ago may be alive'):
            self.again(*ends_as(pool_finished()), runs=[old])
            pool_configuration(self.azure).update(replicaTimeout=840)
            with self.assertRaises(RuntimeError) as raised:
                self.pool_run()
            self.assertEqual(str(raised.exception), beside_another_pool_run('old', 'Unknown', timeout=840))
            self.assertEqual(self.azure.writes(), [])
        with self.subTest('600 s: the same run is too old to be alive'):
            self.again(*ends_as(pool_finished()), runs=[old])
            pool_configuration(self.azure).update(replicaTimeout=600)
            self.assertEqual(self.ended(), 'pool-run')

    def test_a_drifted_pool_job_is_refused_before_the_start(self):
        # Its schedule and its timeout among the rest: a job that somebody put on another schedule
        # is not started by hand either. A job with no timeout is refused for its shape, before
        # its executions are read against that timeout.
        for drifted in ('trigger', 'schedules_expression', 'timeout', 'missing_timeout', 'arguments', 'identity'):
            with self.subTest(drifted=drifted):
                self.again(*ends_as(pool_finished()))
                self.azure.jobs['azurebank-pool'] = pool_resource()
                named, drift = POOL_DRIFTS[drifted]
                drift(self.azure)
                with self.assertRaises(deploy.ShapeError) as raised:
                    self.pool_run()
                # The last words are a start's: nobody was deploying.
                self.assertEqual(str(raised.exception), out_of_shape(
                    'The job azurebank-pool', named, when='nothing was started', before='it is started'))
                self.assertTrue(str(raised.exception).endswith(
                    ' Put it right with the template (infra/README.md) before it is started.'), raised.exception)
                self.assertEqual(self.asked(), [('GET', POOL_ID)], 'not even its executions are read')
                for hidden in ('MARKER', '* * * * *'):
                    self.assertNotIn(hidden, str(raised.exception) + self.printed())

    def test_it_is_refused_inside_actions_before_any_call(self):
        with self.assertRaises(ValueError) as raised:
            self.pool_run(in_actions=True)
        self.assertEqual(str(raised.exception), POOL_RUN_REFUSED)
        self.assertEqual(self.azure.calls, [])
        self.assertEqual(self.printed(), '')

    def test_it_needs_the_subscription_and_the_resource_group(self):
        for subscription, group in (('', GROUP), (SUBSCRIPTION, '')):
            with self.assertRaisesRegex(ValueError, 'AZURE_SUBSCRIPTION_ID and AZURE_RESOURCE_GROUP must be set'):
                deploy.pool_run(subscription, group)
        self.assertEqual(self.azure.calls, [])

    def test_a_timeout_names_who_can_stop_it(self):
        self.azure.pool_outcome = [execution('pool-run', 'Running')]
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        self.assertIs(type(raised.exception), RuntimeError)
        self.assertEqual(str(raised.exception), not_ended())
        self.assertGreaterEqual(self.clock.now, 720)
        self.assertEqual(self.azure.writes(), [START], 'it is not started a second time')
        self.assertEqual(self.said(), ['Pool run execution pool-run started.',
                                       'Pool run execution pool-run: Running.'])
        # The wait is the pool job's own timeout and two minutes, not the migrate job's.
        self.again(execution('pool-run', 'Unknown'))
        pool_configuration(self.azure).update(replicaTimeout=60)
        before = self.clock.now
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        self.assertEqual(str(raised.exception), not_ended(waited=180))
        self.assertEqual(self.clock.now - before, 180)

    def test_an_exit_1_says_to_read_the_line_before_a_second_start(self):
        self.azure.pool_outcome = ends_as(pool_finished(status='Failed', code=1, message='Container pool failed'))
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        # CONTROL: green as written. The verdict is the line of the owner's terminal: what Azure
        # says of the run follows it, as it never does inside GitHub Actions. On exit 1 it is the
        # first thing there is to read, before the log workspace has a line. Seen red with the
        # verdict printed as a workflow run prints it.
        self.assertEqual(self.said()[-1],
                         pool_verdict('Failed', 1, NOT_FINISHED[0]) + ' Azure says: Container pool failed')
        message = str(raised.exception)
        self.assertIn('exit code 1, did not finish, and left no summary line; read its last line before it is '
                      'started again). It was not started again.', message)
        self.assertIn('`python infra/deploy.py --pool-log pool-run`', message)
        self.assertTrue(message.endswith(READ_WITH), message)
        self.assertEqual(self.azure.writes(), [START], 'nothing is started a second time')

    def test_a_pool_job_that_cannot_be_read_stops_before_any_start_in_its_own_words(self):
        # The job is written only with the demo on (infra/main.bicep). Whether it is there is not
        # asked of the app here: the job is read, and a job that cannot be read is not started.
        self.azure.refuse = lambda method, resource_id, body: resource_id == POOL_ID
        for refusal in ('Not Found: ResourceNotFound', 'Forbidden: AuthorizationFailed.'):
            with self.subTest(refusal=refusal):
                self.azure.refusal = refusal
                with self.assertRaises(RuntimeError) as raised:
                    self.pool_run()
                self.assertIs(type(raised.exception), RuntimeError, 'the run says what it means, in its own words')
                self.assertEqual(str(raised.exception), (
                    f'The job azurebank-pool could not be read: {refusal.rstrip(".")}. Nothing was started. '
                    'infra/main.bicep writes that job only with the demo on: see infra/README.md, "When '
                    'something fails".'))
        self.assertEqual(self.azure.writes(), [])
        self.assertEqual(self.printed(), '')

    def test_runs_that_cannot_be_read_stop_before_any_start_in_their_own_words(self):
        self.azure.refuse = lambda method, resource_id, body: resource_id == POOL_ID + '/executions'
        for refusal in ('Forbidden: AuthorizationFailed.',
                        'The Azure CLI gave no answer in 180 s (GET /jobs/azurebank-pool/executions).'):
            with self.subTest(refusal=refusal):
                self.azure.refusal = refusal
                with self.assertRaises(RuntimeError) as raised:
                    self.pool_run()
                self.assertIs(type(raised.exception), RuntimeError, 'the run says what it means, in its own words')
                self.assertEqual(str(raised.exception), (
                    'The executions of the job azurebank-pool could not be read, so whether a pool run is '
                    f'in progress is not known: {refusal.rstrip(".")}. Nothing was started. See '
                    'infra/README.md, "When something fails".'))
        self.assertEqual(self.azure.writes(), [])
        self.assertEqual(self.printed(), '')

    def test_a_start_azure_refuses_or_does_not_answer_is_said_in_its_own_words_and_not_sent_again(self):
        # Whether Azure lets a job that runs on a schedule be started by hand has not been tried
        # here. The first refusal is invented. After the second, a run may have begun.
        self.azure.refuse = lambda method, resource_id, body: (method, resource_id) == START
        for refusal in ('Bad Request: InvalidRequest.',
                        'The Azure CLI gave no answer in 180 s (POST /jobs/azurebank-pool/start).'):
            with self.subTest(refusal=refusal):
                self.clear()
                self.azure.calls.clear()
                self.azure.refusal = refusal
                with self.assertRaises(RuntimeError) as raised:
                    self.pool_run()
                self.assertIs(type(raised.exception), RuntimeError, 'the run says what it means, in its own words')
                self.assertEqual(str(raised.exception), (
                    'The job azurebank-pool was asked to start once, and Azure refused or failed a request '
                    f'before the run it made was known: {refusal.rstrip(".")}. The start is not sent again. '
                    "Whether a run began is read from the job's executions, before any second start "
                    '(infra/README.md, "When something fails").'))
                self.assertEqual(self.azure.writes(), [START])
                self.assertEqual(self.printed(), '')

    def test_a_read_that_fails_while_the_run_is_waited_for_says_that_the_run_goes_on(self):
        # The start was made. A read of the job's executions that Azure then refuses, or does not
        # answer, ends the wait: Azure's words alone would not say that a run was started, that it
        # may still be in progress, nor that nothing was started again.
        reads = []

        def the_second_read(method, resource_id, body):
            if (method, resource_id) == RUNS:
                reads.append(resource_id)
            return (method, resource_id) == RUNS and len(reads) == 2

        self.azure.refuse = the_second_read
        for refusal in ('Forbidden: AuthorizationFailed.',
                        'The Azure CLI gave no answer in 180 s (GET /jobs/azurebank-pool/executions).'):
            for run, name in (('pool-run', 'execution pool-run'), (STRANGE, 'execution whose name is withheld')):
                with self.subTest(refusal=refusal, name=name):
                    self.again(*ends_as(pool_finished(run)))
                    reads.clear()
                    self.azure.refusal = refusal
                    with self.assertRaises(RuntimeError) as raised:
                        self.pool_run()
                    self.assertIs(type(raised.exception), RuntimeError,
                                  'the run says what it means, in its own words')
                    self.assertEqual(str(raised.exception), read_failed(refusal.rstrip('.'), name))
                    for unwanted in ('\n', '::', 'add-mask'):
                        self.assertNotIn(unwanted, str(raised.exception))
                    self.assertEqual(self.azure.writes(), [START], 'nothing is started a second time')
                    self.assertEqual(self.said(), [f'Pool run {name} started.'])
        # A start that names no run looks for it among the executions: a read that fails there is
        # a request that failed before the run was known, and is said so.
        self.again(*ends_as(pool_finished()))
        reads.clear()
        self.azure.start_names_the_run = False
        self.azure.refusal = 'Forbidden: AuthorizationFailed.'
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        self.assertIs(type(raised.exception), RuntimeError)
        self.assertTrue(str(raised.exception).startswith(
            'The job azurebank-pool was asked to start once, and Azure refused or failed a request before the '
            'run it made was known: Forbidden: AuthorizationFailed. The start is not sent again.'),
            str(raised.exception))
        self.assertEqual(self.azure.writes(), [START])

    def test_an_answer_of_another_shape_once_the_start_was_sent_says_that_a_run_may_go_on(self):
        # The start was sent. A list of the job's executions that comes in a shape this script
        # does not read ends the command as a read that fails does: "An answer from Azure lacked
        # 'name'", or a traceback, would not say that a run was started, that it may still be in
        # progress, nor that nothing was started again. Every answer here is invented and none
        # was seen; nothing of one is shown.
        answer = self.azure.pool_call
        odd = {'an entry with no name': {'value': [{'properties': {'status': 'Running'}}]},
               'no list': {'value': None},
               'the run with no properties': {'value': [{'name': 'pool-run'}]},
               'the run with properties that are text': {'value': [{'name': 'pool-run', 'properties': STRANGE}]},
               'an entry that is text': {'value': [STRANGE]},
               'an answer that is a list': [STRANGE]}

        def ended(listing=None, start=None):
            """How a start by hand ends when the second read of the executions, the first one
            after the start, answers `listing`, or the start itself answers `start`."""
            reads = []
            the_list = ('GET', 'executions')

            def oddly(method, tail):
                reads.append((method, tail))
                if start is not None and (method, tail) == ('POST', 'start'):
                    answer(method, tail)
                    return copy.deepcopy(start)
                if listing is not None and (method, tail) == the_list and reads.count(the_list) == 2:
                    return copy.deepcopy(listing)
                return answer(method, tail)

            with patch.object(self.azure, 'pool_call', side_effect=oddly):
                try:
                    return self.pool_run()
                except Exception as error:  # a KeyError or a TypeError is what this test is for
                    return error

        for what, listing in odd.items():
            with self.subTest(what=what, met='while the run is waited for'):
                self.again(*ends_as(pool_finished()))
                end = ended(listing)
                self.assertIs(type(end), RuntimeError, f'{end!r}: the run says what it means, in its own words')
                self.assertEqual(str(end), read_failed(NOT_THE_LIST))
                self.assertNotIn('::', str(end) + self.printed())
                self.assertEqual(self.azure.writes(), [START], 'nothing is started a second time')
                self.assertEqual(self.said(), ['Pool run execution pool-run started.'])
        # A start that names no run looks for it among the executions, and meets the same answers
        # there before the run is known; so does a start whose own answer is not an object.
        self.azure.start_names_the_run = False
        for what, listing, start in (('an entry with no name', odd['an entry with no name'], None),
                                     ('no list', odd['no list'], None),
                                     ('an answer that is a list', odd['an answer that is a list'], None),
                                     ('a start answered with a list', None, [STRANGE])):
            with self.subTest(what=what, met='while the run is looked for'):
                self.again(*ends_as(pool_finished()))
                end = ended(listing, start)
                self.assertIs(type(end), RuntimeError, f'{end!r}: the run says what it means, in its own words')
                self.assertEqual(str(end), START_NOT_READ)
                self.assertEqual(self.azure.writes(), [START], 'the start is not sent again')
                self.assertEqual(self.printed(), '')

    def test_a_start_that_names_no_run_is_found_by_what_is_new_and_is_not_sent_again(self):
        self.azure.start_names_the_run = False
        self.azure.pool_runs = [pool_finished('an-earlier-run')]
        self.assertEqual(self.ended(), 'pool-run')
        self.assertEqual(self.azure.writes(), [START])
        self.assertEqual(self.said()[0], 'Pool run execution pool-run started.')
        self.assertEqual(self.said()[-1], ended_well())
        # A start that is accepted and makes no execution is looked for during one minute, and the
        # sentence is the start's own: nothing was deployed.
        self.again(runs=[pool_finished('an-earlier-run')])
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        self.assertEqual(str(raised.exception), 'The start was accepted but no execution appeared; inspect the '
                                                'job before it is started again.')
        self.assertEqual(self.azure.writes(), [START])
        self.assertEqual(self.clock.sleeps, [5] * 12)

    def test_a_name_or_a_status_of_another_shape_is_never_printed(self):
        self.azure.pool_outcome = [execution(STRANGE, STRANGE), execution(STRANGE, 'Failed'),
                                   pool_finished(STRANGE, status='Failed', code=13)]
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        withheld = 'execution whose name is withheld'
        for line in self.printed().splitlines():
            self.assertRegex(line, r'^\d\d:\d\d:\d\dZ ', 'a line this script did not begin')
        for unwanted in ('::', 'add-mask'):
            self.assertNotIn(unwanted, str(raised.exception) + self.printed())
        self.assertEqual(str(raised.exception), not_well(13, *FOREIGN, name=withheld))
        self.assertEqual(self.said(), [f'Pool run {withheld} started.', f'Pool run {withheld}: status not reported.',
                                       f'Pool run {withheld}: Failed.',
                                       pool_verdict('Failed', 13, FOREIGN[0], name='whose name is withheld')])
        # A run that is not seen ending is not named in the command that stops it either.
        self.again(execution(STRANGE, 'Running'))
        with self.assertRaises(RuntimeError) as raised:
            self.pool_run()
        self.assertEqual(str(raised.exception), not_ended(name='whose name is withheld'))

    def test_through_the_command_line_the_exit_code_of_the_run_is_how_the_command_ends(self):
        environment = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP}
        with patch.dict(deploy.os.environ, environment, clear=True):
            self.azure.pool_outcome = ends_as(pool_finished(status='Failed', code=13))
            with self.assertRaises(SystemExit) as raised:
                deploy.main(['--pool-run'])
            self.assertEqual(raised.exception.code, not_well(13, *FOREIGN))
            self.again(*ends_as(pool_finished(code='0')))
            with self.assertRaises(SystemExit) as raised:
                deploy.main(['--pool-run'])
            self.assertEqual(raised.exception.code, NO_CODE)
            # A run that ended with a signal ends the command well, whatever its status.
            self.again(*ends_as(pool_finished(status='Failed', code=10)))
            try:
                stopped = deploy.main(['--pool-run'])
            except SystemExit as error:
                stopped = error.code
            self.assertIsNone(stopped, 'the command ends well')
            self.assertEqual(self.said()[-1], ended_well(10, *LOW))


class MainTests(Offline):
    ENVIRONMENT = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP, 'IMAGE_TAG': NEW}

    def run_main(self, arguments, **extra):
        with patch.dict(deploy.os.environ, {**self.ENVIRONMENT, **extra}, clear=True):
            return deploy.main(arguments)

    @patch('deploy.app_log')
    @patch('deploy.job_log')
    @patch('deploy.deploy')
    def test_the_two_log_commands_deploy_nothing_and_need_no_image_tag(self, run, job_log, app_log):
        environment = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP}
        with patch.dict(deploy.os.environ, environment, clear=True):
            deploy.main(['--job-log'])
            deploy.main(['--job-log', 'azurebank-migrate-abc123'])
            deploy.main(['--app-log', '15'])
        self.assertEqual([call.args + (call.kwargs,) for call in job_log.call_args_list],
                         [(SUBSCRIPTION, GROUP, '', {'in_actions': False}),
                          (SUBSCRIPTION, GROUP, 'azurebank-migrate-abc123', {'in_actions': False})])
        app_log.assert_called_once_with(SUBSCRIPTION, GROUP, 15, in_actions=False)
        run.assert_not_called()

    @patch('deploy.check')
    @patch('deploy.deploy')
    def test_the_check_deploys_nothing_and_needs_no_image_tag(self, run, check):
        environment = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP}
        with patch.dict(deploy.os.environ, environment, clear=True):
            deploy.main(['--check'])
        check.assert_called_once_with(SUBSCRIPTION, GROUP, in_actions=False)
        run.assert_not_called()

    @patch('deploy.rest')
    @patch('deploy.deploy')
    def test_the_check_inside_actions_exits_non_zero_without_calling_azure(self, run, rest):
        with self.assertRaises(SystemExit) as raised:
            self.run_main(['--check'], GITHUB_ACTIONS='true')
        self.assertEqual(raised.exception.code, CHECK_REFUSED)
        rest.assert_not_called()
        run.assert_not_called()

    @patch('deploy.check')
    @patch('deploy.deploy')
    def test_a_deployment_and_the_road_back_run_no_check_of_their_own(self, run, check):
        # CONTROL: green before this change. Without the option the command line deploys, as it did.
        self.run_main([])
        self.run_main(['--app-only'])
        self.assertEqual(run.call_count, 2)
        check.assert_not_called()

    @patch('deploy.job_log')
    @patch('deploy.pool_run')
    @patch('deploy.deploy')
    def test_the_pool_commands_deploy_nothing_and_need_no_image_tag(self, run, pool_run, job_log):
        environment = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP}
        with patch.dict(deploy.os.environ, environment, clear=True):
            deploy.main(['--pool-run'])
            deploy.main(['--pool-log'])
            deploy.main(['--pool-log', 'azurebank-pool-abc123'])
        pool_run.assert_called_once_with(SUBSCRIPTION, GROUP, in_actions=False)
        pool = {'in_actions': False, 'job': 'azurebank-pool'}
        self.assertEqual([call.args + (call.kwargs,) for call in job_log.call_args_list],
                         [(SUBSCRIPTION, GROUP, '', pool), (SUBSCRIPTION, GROUP, 'azurebank-pool-abc123', pool)])
        run.assert_not_called()

    @patch('deploy.rest')
    @patch('deploy.az')
    @patch('deploy.deploy')
    def test_the_pool_log_inside_actions_exits_non_zero_without_calling_azure(self, run, az, rest):
        for arguments in (['--pool-log'], ['--pool-log', 'a-run']):
            with self.subTest(arguments=arguments):
                with self.assertRaises(SystemExit) as raised:
                    self.run_main(arguments, GITHUB_ACTIONS='true')
                self.assertEqual(raised.exception.code, POOL_LOG_REFUSED)
        az.assert_not_called()
        rest.assert_not_called()
        run.assert_not_called()

    @patch('deploy.rest')
    @patch('deploy.az')
    @patch('deploy.deploy')
    def test_a_pool_log_name_of_another_shape_exits_non_zero_without_calling_azure(self, run, az, rest):
        with self.assertRaises(SystemExit) as raised:
            self.run_main(['--pool-log', "a-run' or JobName != '"])
        self.assertEqual(raised.exception.code, "--pool-log takes an execution's name: letters, digits and "
                                                'hyphens, 100 at most. Nothing was read.')
        az.assert_not_called()
        rest.assert_not_called()
        run.assert_not_called()

    @patch('deploy.rest')
    @patch('deploy.deploy')
    def test_the_pool_run_inside_actions_exits_non_zero_without_calling_azure(self, run, rest):
        with self.assertRaises(SystemExit) as raised:
            self.run_main(['--pool-run'], GITHUB_ACTIONS='true')
        self.assertEqual(raised.exception.code, POOL_RUN_REFUSED)
        rest.assert_not_called()
        run.assert_not_called()

    @patch('deploy.pool_run')
    @patch('deploy.check')
    @patch('deploy.deploy')
    def test_no_other_command_starts_a_pool_run(self, run, check, pool_run):
        # CONTROL: green as soon as the function has its name, wired to the command line or not. A
        # deployment, the road back and the check start no run of the pool job.
        self.run_main([])
        self.run_main(['--app-only'])
        self.run_main(['--check'])
        self.assertEqual((run.call_count, check.call_count), (2, 1))
        pool_run.assert_not_called()

    @patch('deploy.rest')
    @patch('deploy.az')
    def test_the_two_log_commands_inside_actions_exit_non_zero_without_calling_azure(self, az, rest):
        for arguments in (['--job-log'], ['--job-log', 'a-run'], ['--app-log', '15']):
            with self.subTest(arguments=arguments):
                with self.assertRaises(SystemExit) as raised:
                    self.run_main(arguments, GITHUB_ACTIONS='true')
                self.assertIn('refused inside GitHub Actions', str(raised.exception.code))
        az.assert_not_called()
        rest.assert_not_called()

    @patch('deploy.rest')
    @patch('deploy.az')
    def test_a_job_log_name_of_another_shape_exits_non_zero_without_calling_azure(self, az, rest):
        with self.assertRaises(SystemExit) as raised:
            self.run_main(['--job-log', "a-run' or JobName != '"])
        self.assertIn("--job-log takes an execution's name", str(raised.exception.code))
        az.assert_not_called()
        rest.assert_not_called()

    def test_two_modes_at_once_are_refused_by_the_command_line(self):
        # The two cases with --check were green before the option existed: a command line the
        # parser does not know ends the same way. They hold the option in the one group of modes,
        # and so do the cases with --pool-run and with --pool-log.
        for arguments in (['--app-only', '--job-log'], ['--job-log', '--app-log', '5'],
                          ['--app-log', 'soon'], ['--check', '--app-only'], ['--check', '--job-log'],
                          ['--pool-run', '--app-only'], ['--pool-run', '--check'], ['--pool-run', 'now'],
                          ['--pool-log', '--job-log'], ['--pool-run', '--pool-log'], ['--pool-log', 'a', 'b']):
            with self.subTest(arguments=arguments), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as raised:
                    self.run_main(arguments)
                self.assertEqual(raised.exception.code, 2)

    @patch('deploy.deploy')
    def test_the_workflow_run_asks_for_the_mask_and_the_refusal(self, run):
        self.run_main([], GITHUB_ACTIONS='true', EXPECT_SECRETS_REFUSED='1')
        run.assert_called_once_with(SUBSCRIPTION, GROUP, NEW, app_only=False, in_actions=True,
                                    expect_secrets_refused=True)

    @patch('deploy.deploy')
    def test_the_owners_terminal_asks_for_neither(self, run):
        self.run_main(['--app-only'])
        run.assert_called_once_with(SUBSCRIPTION, GROUP, NEW, app_only=True, in_actions=False,
                                    expect_secrets_refused=False)

    @patch('deploy.rest')
    def test_app_only_inside_actions_exits_non_zero_without_calling_azure(self, rest):
        with self.assertRaises(SystemExit) as raised:
            self.run_main(['--app-only'], GITHUB_ACTIONS='true')
        self.assertIn('refused inside GitHub Actions', str(raised.exception.code))
        rest.assert_not_called()

    @patch('deploy.deploy', side_effect=deploy.AzError('Forbidden: AuthorizationFailed'))
    def test_a_refusal_by_azure_exits_non_zero_with_its_text(self, run):
        with self.assertRaises(SystemExit) as raised:
            self.run_main([])
        self.assertIn('AuthorizationFailed', str(raised.exception.code))

    @patch('deploy.deploy', side_effect=deploy.SmokeUnproven('Smoke test unproven.'))
    def test_an_unproven_run_exits_non_zero(self, run):
        with self.assertRaises(SystemExit) as raised:
            self.run_main([])
        self.assertEqual(raised.exception.code, 'Smoke test unproven.')

    def test_a_cli_that_never_answers_exits_non_zero_without_the_subscription(self):
        commands = []

        def never_answers(command, **options):
            commands.append(' '.join(command))
            raise subprocess.TimeoutExpired(command, options['timeout'])

        self.start(patch('deploy.subprocess.run', never_answers))
        with self.assertRaises(SystemExit) as raised:
            self.run_main([])
        self.assertIn('no answer in 180 s (GET /containerApps/azurebank)', str(raised.exception.code))
        self.assertIn(SUBSCRIPTION, commands[0], 'the command line did hold it')
        self.assertNotIn(SUBSCRIPTION, str(raised.exception.code) + self.printed())


WORKSPACE_ID = (f'/subscriptions/{SUBSCRIPTION}/resourceGroups/{GROUP}/providers'
                '/Microsoft.OperationalInsights/workspaces/azurebank-logs')
CUSTOMER = 'cccccccc-9999-4aaa-8bbb-cccccccccccc'
QUERY = ('rest', '--method', 'POST', '--url',
         f'https://api.loganalytics.io/v1/workspaces/{CUSTOMER}/query',
         '--resource', 'https://api.loganalytics.io')


def workspace(status='RespectQuota', customer=CUSTOMER):
    return {'properties': {'customerId': customer, 'workspaceCapping': {
        'dailyQuotaGb': 0.05, 'dataIngestionStatus': status,
        'quotaNextResetTime': '2026-10-03T07:00:00Z'}}}


def table(*rows, columns=('TimeGenerated', 'Log')):
    return {'tables': [{'name': 'PrimaryResult',
                        'columns': [{'name': name, 'type': 'string'} for name in columns],
                        'rows': [list(row) for row in rows]}]}


class LogTests(Offline):
    """--job-log and --app-log: the owner reads what the containers printed, from the workspace."""

    def setUp(self):
        super().setUp()
        self.workspace = workspace()
        earlier = finished('an-earlier-run', code=1)
        earlier['properties'].update(startTime='2026-10-01T09:00:00Z', endTime='2026-10-01T09:00:05Z')
        self.listed = [finished('this-run'), earlier]
        # The runs of the pool job: the latest ended with a signal, the one before needs a look.
        earlier_pool_run = pool_finished('an-earlier-pool-run', status='Failed', code=13)
        earlier_pool_run['properties'].update(startTime='2026-10-01T09:00:00Z', endTime='2026-10-01T09:00:05Z')
        self.pool_listed = [pool_finished(status='Failed', code=10), earlier_pool_run]
        self.answer = table(('2026-10-02T18:00:04Z', 'Applying migration 0001'),
                            ('2026-10-02T18:00:08Z', 'Done.'))
        self.reads = []
        self.queries = []
        self.start(patch('deploy.rest', self.rest))
        self.start(patch('deploy.az', self.az))

    def rest(self, method, resource_id, body=None, api_version=deploy.API_VERSION):
        self.reads.append((method, resource_id, api_version))
        if resource_id == WORKSPACE_ID:
            if isinstance(self.workspace, Exception):
                raise self.workspace
            return self.workspace
        if resource_id == MIGRATE_ID + '/executions':
            return {'value': self.listed}
        if resource_id == POOL_ID + '/executions':
            return {'value': self.pool_listed}
        raise AssertionError(f'unexpected call: {method} {resource_id}')

    def az(self, *args, what=None):
        # The body is a file that exists only while the call runs: read it here.
        with open(args[args.index('--body') + 1][1:], encoding='utf-8') as request:
            self.queries.append((args[:args.index('--body')], json.load(request)))
        return self.answer

    def said(self):
        """What was printed, without the clock in front of the lines that carry one."""
        return [re.sub(r'^\d\d:\d\d:\d\dZ ', '', line) for line in self.printed().splitlines()]

    def test_the_job_log_says_what_the_cap_is_doing_then_the_verdict_then_the_lines(self):
        deploy.job_log(SUBSCRIPTION, GROUP)
        self.assertEqual(self.said(), [
            'Log workspace azurebank-logs: daily cap 0.05 GB, ingestion RespectQuota, '
            'next reset 2026-10-03T07:00:00Z.',
            VERDICT, '2026-10-02T18:00:04Z Applying migration 0001', '2026-10-02T18:00:08Z Done.',
            '2 line(s).'])
        self.assertEqual(self.reads, [('GET', WORKSPACE_ID, '2023-09-01'),
                                      ('GET', MIGRATE_ID + '/executions', '2026-07-01')])
        # The latest execution's own lines, from two minutes before its start to five after its end.
        self.assertEqual(self.queries, [(QUERY, {
            'query': "ContainerAppConsoleLogs | where JobName == 'azurebank-migrate' "
                     "| where ContainerGroupName startswith 'this-run-' "
                     '| where TimeGenerated between (datetime(2026-10-02T17:58:03Z) .. '
                     'datetime(2026-10-02T18:05:09Z)) | order by TimeGenerated asc | take 5000 '
                     '| project TimeGenerated, Log',
            'timespan': '2026-10-02T17:58:03Z/2026-10-02T18:05:09Z'})])

    def test_the_job_log_asks_for_the_lines_of_the_execution_it_names_and_of_no_other(self):
        # Measured on 2026-10-03: the job's name and a period also matched the five lines of the
        # next execution, which started inside the margin. In the table each line carries its
        # execution's name, a hyphen and a suffix in ContainerGroupName (README.md, "Measured on
        # Azure").
        deploy.job_log(SUBSCRIPTION, GROUP)
        deploy.job_log(SUBSCRIPTION, GROUP, 'an-earlier-run')
        asked = [request['query'] for _, request in self.queries]
        group = r"\| where ContainerGroupName startswith '([^']*)' \|"
        self.assertEqual([re.findall(group, query) for query in asked], [['this-run-'], ['an-earlier-run-']])
        # The period stays: it bounds the read.
        self.assertEqual([query.count('| where TimeGenerated between (datetime(') for query in asked], [1, 1])
        self.assertEqual([request['timespan'] for _, request in self.queries],
                         ['2026-10-02T17:58:03Z/2026-10-02T18:05:09Z', '2026-10-01T08:58:00Z/2026-10-01T09:05:05Z'])

    def test_a_name_that_is_not_the_shape_of_one_is_refused_before_any_call(self):
        # The name goes into the query between quotes: only the shape of an execution's name may.
        for name in ("this-run' or JobName != '", 'this run', 'this-run\n', 'this_run', 'this-run|take 1',
                     'a' * 101):
            with self.subTest(name=name):
                with self.assertRaisesRegex(ValueError, "--job-log takes an execution's name") as raised:
                    deploy.job_log(SUBSCRIPTION, GROUP, name)
                self.assertNotIn(name, str(raised.exception), 'a name of another shape is never printed')
        self.assertEqual(self.reads + self.queries, [])
        self.assertEqual(self.printed(), '')

    def test_an_execution_azure_names_in_another_shape_gets_its_verdict_and_no_query(self):
        for name in ("this-run' or JobName != '", 'this run', 'a' * 101, None, 7):
            with self.subTest(name=name):
                self.clear()
                self.listed = [finished('this-run')]
                self.listed[0]['name'] = name
                with self.assertRaisesRegex(RuntimeError, 'is not the shape of one.*no query was sent') as raised:
                    deploy.job_log(SUBSCRIPTION, GROUP)
                self.assertIn('Verdict: execution whose name is withheld: Succeeded', self.said()[1])
                if isinstance(name, str):
                    self.assertNotIn(name, self.printed() + str(raised.exception))
        self.assertEqual(self.queries, [])

    def test_a_named_execution_is_the_one_read_and_a_name_nobody_has_is_an_error(self):
        deploy.job_log(SUBSCRIPTION, GROUP, 'an-earlier-run')
        self.assertIn('Verdict: execution an-earlier-run: Succeeded', self.said()[1])
        self.assertEqual(self.queries[0][1]['timespan'], '2026-10-01T08:58:00Z/2026-10-01T09:05:05Z')
        with self.assertRaisesRegex(RuntimeError, 'has no execution named no-such-run'):
            deploy.job_log(SUBSCRIPTION, GROUP, 'no-such-run')
        self.listed = []
        with self.assertRaisesRegex(RuntimeError, 'has no execution yet'):
            deploy.job_log(SUBSCRIPTION, GROUP)
        self.assertEqual(len(self.queries), 1)

    def test_on_the_owners_terminal_the_verdict_carries_what_azure_said(self):
        self.listed = [finished('this-run', status='Failed', code=1, message='MESSAGE-MARKER',
                                reason='Container failed to start')]
        deploy.job_log(SUBSCRIPTION, GROUP)
        self.assertIn("reason 'Container failed to start'. Azure says: MESSAGE-MARKER", self.said()[1])

    def test_a_cap_that_was_reached_is_said_before_an_answer_with_no_line(self):
        self.workspace, self.answer = workspace('OverQuota'), table()
        deploy.job_log(SUBSCRIPTION, GROUP)
        said = self.said()
        self.assertIn('ingestion OverQuota', said[0])
        self.assertTrue(said[1].startswith('The cap was reached: the workspace takes no line'), said[1])
        self.assertEqual(said[3], '0 line(s).')
        self.assertTrue(said[4].startswith('No line is not proof that nothing was printed'), said[4])

    def test_without_a_workspace_both_commands_say_that_nothing_is_kept(self):
        self.workspace = deploy.AzError(
            'Not Found({"error":{"code":"ResourceNotFound","message":"The Resource '
            "'Microsoft.OperationalInsights/workspaces/azurebank-logs' was not found.\"}})")
        for read in (lambda: deploy.job_log(SUBSCRIPTION, GROUP),
                     lambda: deploy.app_log(SUBSCRIPTION, GROUP, 15)):
            with self.assertRaisesRegex(RuntimeError, 'the logs are switched off and nothing is kept'):
                read()
        self.workspace = deploy.AzError('Forbidden: AuthorizationFailed')
        with self.assertRaisesRegex(deploy.AzError, 'AuthorizationFailed'):
            deploy.job_log(SUBSCRIPTION, GROUP)
        self.assertEqual(self.queries, [])

    def test_an_id_that_is_not_an_id_never_becomes_part_of_a_url(self):
        for customer in ('x/../../other?', None, '', f'{CUSTOMER}/query?x='):
            with self.subTest(customer=customer):
                self.workspace = workspace(customer=customer)
                with self.assertRaisesRegex(RuntimeError, 'did not report the ID a query is sent to'):
                    deploy.app_log(SUBSCRIPTION, GROUP, 15)
        self.assertEqual(self.queries, [])
        self.workspace = workspace(customer=CUSTOMER.upper())
        deploy.app_log(SUBSCRIPTION, GROUP, 15)
        self.assertEqual(self.queries[0][0], QUERY, 'parsed, and printed again')

    def test_the_app_log_reads_the_last_minutes_of_the_app(self):
        self.answer = table(('2026-10-02T18:00:04Z', 'api', 'a line of the api'),
                            ('2026-10-02T18:00:05Z', 'bff', 'a line of the bff'),
                            columns=('TimeGenerated', 'ContainerName', 'Log'))
        deploy.app_log(SUBSCRIPTION, GROUP, 15)
        self.assertEqual(self.said()[1:], ['2026-10-02T18:00:04Z api a line of the api',
                                           '2026-10-02T18:00:05Z bff a line of the bff', '2 line(s).'])
        self.assertEqual(self.reads, [('GET', WORKSPACE_ID, '2023-09-01')])
        self.assertEqual(self.queries, [(QUERY, {
            'query': "ContainerAppConsoleLogs | where ContainerAppName == 'azurebank' "
                     '| where TimeGenerated > ago(15m) | order by TimeGenerated asc | take 5000 '
                     '| project TimeGenerated, ContainerName, Log',
            'timespan': 'PT15M'})])

    def test_minutes_outside_one_day_are_refused_before_any_call(self):
        for minutes in (0, -5, 1441):
            with self.subTest(minutes=minutes), self.assertRaisesRegex(ValueError, 'between 1 and 1440'):
                deploy.app_log(SUBSCRIPTION, GROUP, minutes)
        self.assertEqual(self.reads + self.queries, [])

    def test_both_commands_are_refused_inside_actions_before_any_call(self):
        for read in (lambda: deploy.job_log(SUBSCRIPTION, GROUP, in_actions=True),
                     lambda: deploy.app_log(SUBSCRIPTION, GROUP, 15, in_actions=True)):
            with self.assertRaisesRegex(ValueError, 'refused inside GitHub Actions'):
                read()
        self.assertEqual(self.reads + self.queries, [])
        self.assertEqual(self.printed(), '')

    def test_an_execution_without_a_start_has_no_period_to_read(self):
        self.listed = [execution('this-run', 'Unknown')]
        with self.assertRaisesRegex(RuntimeError, 'no start time'):
            deploy.job_log(SUBSCRIPTION, GROUP)
        self.assertEqual(self.queries, [])

    POOL_LINE = ('pool: free=50 was=12 claimed=3 claims24h=5 clientsAtCap=0 seeded=38 deleted(expired=0 hardStop=0 '
                 'staleFree=0 failed=0) swept(idempotency=0 grants=0) tombstones=0 foreignUsers=0 ceiling=no '
                 'result=PoolLow')

    def pool_log(self, name='', **options):
        return deploy.job_log(SUBSCRIPTION, GROUP, name, job='azurebank-pool', **options)

    def test_the_pool_log_asks_for_the_pool_jobs_lines_and_reads_its_container(self):
        # The line is invented, in the form the tool prints it (docs/runbooks/demo-pool.md, "The line").
        self.answer = table(('2026-10-02T18:00:08Z', self.POOL_LINE))
        self.pool_log()
        self.assertEqual(self.reads, [('GET', WORKSPACE_ID, '2023-09-01'),
                                      ('GET', POOL_ID + '/executions', '2026-07-01')])
        # The verdict of the latest run, worded as the pool job's map words its exit code.
        self.assertEqual(self.said(), [
            'Log workspace azurebank-logs: daily cap 0.05 GB, ingestion RespectQuota, '
            'next reset 2026-10-03T07:00:00Z.',
            pool_verdict('Failed', 10, LOW[0]), f'2026-10-02T18:00:08Z {self.POOL_LINE}', '1 line(s).'])
        self.assertEqual(self.queries, [(QUERY, {
            'query': "ContainerAppConsoleLogs | where JobName == 'azurebank-pool' "
                     "| where ContainerGroupName startswith 'pool-run-' "
                     '| where TimeGenerated between (datetime(2026-10-02T17:58:03Z) .. '
                     'datetime(2026-10-02T18:05:09Z)) | order by TimeGenerated asc | take 5000 '
                     '| project TimeGenerated, Log',
            'timespan': '2026-10-02T17:58:03Z/2026-10-02T18:05:09Z'})])

    def test_the_pool_log_reads_the_run_it_names_and_the_code_of_the_jobs_own_container(self):
        # An execution that lists a second container, which nothing in this folder writes: the
        # code on the line is the one of `pool`.
        self.pool_listed[0]['properties']['detailedStatus']['replicas'][0]['containers'].append(
            {'name': 'migrate', 'code': 2})
        self.pool_log()
        self.assertEqual(self.said()[1], pool_verdict('Failed', 10, LOW[0]))
        self.clear()
        self.pool_log('an-earlier-pool-run')
        self.assertIn('Verdict: execution an-earlier-pool-run: Failed, ', self.said()[1])
        self.assertIn(f', exit code 13 ({FOREIGN[0]}), ', self.said()[1])
        self.assertIn("| where ContainerGroupName startswith 'an-earlier-pool-run-' |", self.queries[1][1]['query'])
        self.assertEqual(self.queries[1][1]['timespan'], '2026-10-01T08:58:00Z/2026-10-01T09:05:05Z')
        # A name nobody has, and a job that has not run yet, are said of the pool job.
        with self.assertRaises(RuntimeError) as raised:
            self.pool_log('no-such-run')
        self.assertEqual(str(raised.exception), 'The job azurebank-pool has no execution named no-such-run.')
        self.pool_listed = []
        with self.assertRaises(RuntimeError) as raised:
            self.pool_log()
        self.assertEqual(str(raised.exception), 'The job azurebank-pool has no execution yet.')
        self.assertEqual(len(self.queries), 2)

    def test_the_pool_log_is_refused_as_the_job_log_is_in_words_that_name_it(self):
        with self.assertRaises(ValueError) as raised:
            self.pool_log(in_actions=True)
        self.assertEqual(str(raised.exception), POOL_LOG_REFUSED)
        for name in ("pool-run' or JobName != '", 'pool run', 'pool-run\n', 'a' * 101):
            with self.subTest(name=name):
                # Either kind is one the command line prints as it is.
                with self.assertRaises((ValueError, RuntimeError)) as raised:
                    self.pool_log(name)
                self.assertIs(type(raised.exception), ValueError, 'refused before anything is read')
                self.assertEqual(str(raised.exception), "--pool-log takes an execution's name: letters, digits "
                                                        'and hyphens, 100 at most. Nothing was read.')
        self.assertEqual(self.reads + self.queries, [])
        self.assertEqual(self.printed(), '')

    def test_a_job_that_is_neither_of_the_two_is_never_asked_for(self):
        # The job's name goes into the query between quotes, as the execution's does: only the two
        # jobs this script knows may. The command line can name no other; this holds the function.
        for job in ("azurebank-pool' or JobName != '", 'another-job', ''):
            with self.subTest(job=job):
                # Either kind is one the command line prints as it is.
                with self.assertRaises((ValueError, LookupError)) as raised:
                    deploy.job_log(SUBSCRIPTION, GROUP, job=job)
                self.assertIs(type(raised.exception), ValueError, 'the refusal is said in its own words')
                self.assertEqual(str(raised.exception), 'A log is read for the migrate job or for the pool job, '
                                                        'and for no other. Nothing was read.')
        self.assertEqual(self.reads + self.queries, [])
        self.assertEqual(self.printed(), '')

    def test_the_job_log_still_words_a_code_as_the_migrate_job_does(self):
        # CONTROL: green before this change. A migration that exited with 10 is not a pool run:
        # its line says what it said, and its lines are asked for under the migrate job's name.
        self.listed = [finished('this-run', status='Failed', code=10)]
        deploy.job_log(SUBSCRIPTION, GROUP)
        self.assertIn(', exit code 10 (not a code the tool itself exits with), ', self.said()[1])
        self.assertIn("| where JobName == 'azurebank-migrate' |", self.queries[0][1]['query'])
        self.assertEqual(self.reads[-1], ('GET', MIGRATE_ID + '/executions', '2026-07-01'))


class WholeRunTests(Offline):
    """`deploy.deploy` with the real `run_migration`, as the workflow runs it: what a public log
    gets from a migration, and what a deployment never asks for."""

    def setUp(self):
        super().setUp()
        self.azure = FakeAzure(self.out)
        self.azure.executions = []
        self.start(patch('deploy.rest', self.azure.rest))
        self.start(patch('deploy.az', side_effect=AssertionError('a deployment sent a log query')))
        self.smoke = self.start(patch('deploy.smoke'))

    def verdicts(self):
        return [line.split(' ', 1)[1] for line in self.printed().splitlines() if 'Verdict:' in line]

    def test_a_deployment_prints_the_verdict_and_fetches_nothing_the_job_printed(self):
        deploy.deploy(SUBSCRIPTION, GROUP, NEW, in_actions=True)
        self.assertEqual(self.verdicts(), [VERDICT])
        # Every call is on the app or on a job: nothing reads the log workspace.
        self.assertEqual({resource_id.split('/providers/')[1].split('/')[0]
                          for _, resource_id, _ in self.azure.calls}, {'Microsoft.App'})
        self.assertEqual([call for call in self.azure.versions if call[2] != deploy.API_VERSION],
                         [('GET', 'executions', '2026-07-01')])
        self.smoke.assert_called_once()

    def test_with_the_demo_on_a_deployment_reads_the_pool_jobs_runs_and_starts_the_migration_only(self):
        self.azure.turn_the_demo_on()
        self.azure.pool_runs = [finished('a-pool-run', status='Failed', code=10)]
        deploy.deploy(SUBSCRIPTION, GROUP, NEW, in_actions=True)
        # One verdict, the migration's: a pool run is read for whether it is over, and no line of
        # a deployment says how it ended.
        self.assertEqual(self.verdicts(), [VERDICT])
        self.assertNotIn('a-pool-run', self.printed())
        calls = [(method, resource_id) for method, resource_id, _ in self.azure.calls]
        self.assertEqual([call for call in calls if call[0] == 'POST'], [('POST', MIGRATE_ID + '/start')])
        self.assertEqual(calls.count(('GET', POOL_ID + '/executions')), 1)
        self.assertEqual({resource_id.split('/providers/')[1].split('/')[0] for _, resource_id in calls},
                         {'Microsoft.App'})
        # The pool job's runs are read with the usual version: only the verdict needs the later one.
        self.assertEqual([call for call in self.azure.versions if call[2] != deploy.API_VERSION],
                         [('GET', 'executions', '2026-07-01')])
        self.smoke.assert_called_once_with(f'https://{ADDRESS}', demo=True)

    def test_a_failed_migration_prints_its_verdict_and_leaves_the_app_alone(self):
        self.azure.outcome = finished('this-run', status='Failed', code=2, reason='Error')
        with self.assertRaisesRegex(RuntimeError, 'did not succeed .execution this-run: Failed.'):
            deploy.deploy(SUBSCRIPTION, GROUP, NEW, in_actions=True)
        self.assertEqual(self.verdicts(), [
            'Verdict: execution this-run: Failed, started 2026-10-02T18:00:03Z, '
            'ended 2026-10-02T18:00:09Z (6 s), exit code 2 (refused before any connection; '
            'the configuration must change), reason Error.'])
        self.assertEqual(self.azure.app_patches(), [])
        self.smoke.assert_not_called()


SPA = ('<!doctype html><title>AzureBank</title><div id="root"></div>'
       '<script type="module" src="/assets/index-hash.js"></script>')
# The four answers below are not invented: they were observed on 2026-10-02 on a local stack of
# this code (compose.yaml, Production images, SQL Server 2022), for POST /bff/auth/login with
# deploy.SMOKE_LOGIN. /health/ready answered 200 "Healthy" in all four situations.
#   REFUSED      the schema is there and the address is unknown
#   LIMITED      the shared sign-in limit is spent (the answer carries Retry-After: 60)
#   NO_TABLES    the database exists and holds no table: what a deployment without a migration is
#   NO_DATABASE  the server is there and the database is not
REFUSED = (401, 'application/json',
           '{"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,'
           '"detail":"Invalid email or password.","instance":"/api/auth/login",'
           '"errorCode":"INVALID_CREDENTIALS","traceId":"dc85e0cb0e40bb6fc1402f5b3ddc6812"}')
LIMITED = (429, 'application/json',
           '{"type":"https://httpstatuses.com/429","title":"Too Many Requests","status":429,'
           '"detail":"Too many requests. Please retry later.","instance":"/bff/auth/login",'
           '"errorCode":"RATE_LIMIT_EXCEEDED","traceId":"5661db9e82fddf4bd7377d5f271683f4"}')
NO_TABLES = (500, 'application/json',
             '{"type":"https://httpstatuses.com/500","title":"Internal Server Error","status":500,'
             '"detail":"An unexpected error occurred. Please try again later.","instance":"/api/auth/login",'
             '"traceId":"29c72c7410160addbe4edaa4bf659693"}')
NO_DATABASE = (503, 'application/json',
               '{"type":"https://httpstatuses.com/503","title":"Service Unavailable","status":503,'
               '"detail":"The service is temporarily unavailable. Try again shortly.",'
               '"instance":"/api/auth/login","errorCode":"SERVICE_UNAVAILABLE",'
               '"traceId":"3b7bc662e57734de951062361ab2d548","retryAfterSeconds":10}')
SITE = 'https://example.invalid'
# The page as the BFF serves it on the public demo: the same page with the demo's tag in it
# (backend/src/AzureBank.Bff/Extensions/SpaHostingExtensions.cs).
DEMO_SPA = SPA.replace('<script', deploy.DEMO_TAG + '<script')
DEMO_PAGE = (200, 'text/html', DEMO_SPA)
# The answers to POST /bff/auth/register. None of them was observed on a running stack. CLOSED is
# the BFF's own refusal on the demo, member for member as its tests hold it for the body {}
# (backend/tests/AzureBank.Bff.Tests/DemoClaimTests.cs: 403 with the demo on, 400 with it off).
# OPEN and NOT_VALID are invented: a door that is not closed, answering as if it had registered
# somebody, and answering the empty body as a body that fails the rules. What they hold beside a
# status and an error code is planted: no line and no error may show it.
CLOSED = (403, 'application/json',
          '{"type":"https://httpstatuses.com/403","title":"Forbidden","status":403,'
          '"detail":"Registration is closed on this demo.","instance":"/bff/auth/register",'
          '"errorCode":"REGISTRATION_CLOSED","traceId":"0af7651916cd43dd8448eb211c80319c"}')
OPEN = (201, 'application/json',
        '{"success":true,"data":{"user":{"email":"PLANTED-VALUE@example.invalid"}},'
        '"message":"PLANTED-VALUE"}')
NOT_VALID = (400, 'application/json',
             '{"status":400,"errorCode":"VALIDATION_ERROR","errors":{"Email":["PLANTED-VALUE"]}}')
# What a connection that is dropped raises, as urllib and http.client raise it. None of them is a
# urllib.error.URLError: each was an uncaught exception before the smoke test took it for silence.
DROPPED = (
    ConnectionResetError(104, 'Connection reset by peer'),
    ConnectionAbortedError(10053, 'An established connection was aborted'),
    ConnectionRefusedError(111, 'Connection refused'),
    http.client.RemoteDisconnected('Remote end closed connection without response'),
    http.client.IncompleteRead(b'<!doctype'),
    http.client.BadStatusLine('not HTTP'),
    TimeoutError('timed out'),
)


class Site:
    """Stands in for `deploy.fetch`: the page, the readiness answer, the sign-in answers and the
    answers to a registration. Each is one answer or a list of answers in order (the last one
    repeats); an exception is raised. Unless a test says otherwise the door for a registration is
    open, as it is with the demo off: a request that reaches it would register somebody."""

    def __init__(self, *sign_in, page=(200, 'text/html', SPA), ready=(200, 'text/plain', 'Healthy'),
                 register=OPEN):
        self.answers = {'/': page if isinstance(page, list) else [page],
                        '/health/ready': ready if isinstance(ready, list) else [ready],
                        '/bff/auth/login': list(sign_in),
                        '/bff/auth/register': register if isinstance(register, list) else [register]}
        self.requests = []

    def fetch(self, opener, url, body=None):
        path = urllib.parse.urlsplit(url).path
        self.requests.append((path, body))
        answers = self.answers[path]
        answer = answers.pop(0) if len(answers) > 1 else answers[0]
        if isinstance(answer, Exception):
            raise answer
        return answer

    def sign_ins(self):
        return [path for path, body in self.requests if body is not None]

    def registrations(self):
        """The bodies sent to the door for a registration."""
        return [body for path, body in self.requests if path == '/bff/auth/register']


def demo_site(*sign_in, **answers):
    """The site as the public demo answers: the page carries the tag, and registration is closed."""
    return Site(*sign_in, **{'page': DEMO_PAGE, 'register': CLOSED, **answers})


PASSED = ('Smoke passed: https://example.invalid/ is the SPA, /health/ready is Healthy, and a sign-in '
          'for an unknown address was refused by the API after it asked the database.')
WRONG_PAGE = ("Smoke test failed: the page or the readiness answer was wrong after 300 s (/ -> 200, "
              "/health/ready -> 200 'Healthy').")
TAG = '<meta name="azurebank-demo" content="true">'


def backend_source(*path):
    """A source file of the backend, as text, from the checkout these tests are in. Nothing is
    built or run: it is read so that what the script expects of the BFF is held to what the BFF's
    own code writes."""
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, os.pardir, 'backend', 'src', *path), encoding='utf-8') as source:
        return source.read()


def seeder_source(*path):
    """A source file of the tool the pool job runs (backend/tools/AzureBank.Seeder), as text, read
    the same way and for the same reason: what the script expects of a run of `recycle` is held
    to what that tool's own code writes."""
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, os.pardir, 'backend', 'tools', 'AzureBank.Seeder', *path),
              encoding='utf-8') as source:
        return source.read()


def not_closed(got):
    """The whole sentence of a registration that was not refused as closed."""
    return ('Smoke test failed: the registration probe expected 403 REGISTRATION_CLOSED and got '
            f'{got}. On the public demo a registration must be refused as closed, whatever its '
            'body: this answer was not that refusal.')


def registration_unproven(last):
    return (f'Smoke test unproven: in 4 tries the registration probe got {last} last. The page, the '
            'readiness answer and the sign-in answer were right. Nothing was proved wrong and '
            'nothing is put back: whether the demo keeps registration closed is not known. Deploy '
            'again; or, from a terminal, run `python infra/deploy.py --check`, which asks the address '
            'the same four questions and moves nothing; or ask by hand: POST /bff/auth/register with '
            'the body {} must answer 403 REGISTRATION_CLOSED.')


class SmokeTests(Offline):
    def site(self, *sign_in, **pages):
        site = Site(*sign_in, **pages)
        self.start(patch('deploy.fetch', site.fetch))
        return site

    def demo_site(self, *sign_in, **answers):
        site = demo_site(*sign_in, **answers)
        self.start(patch('deploy.fetch', site.fetch))
        return site

    def said(self):
        """What was printed, without the clock in front of each line."""
        return [line.split(' ', 1)[1] for line in self.printed().splitlines()]

    def test_with_the_demo_off_the_line_of_a_pass_is_the_one_it_was(self):
        # CONTROL: green before this change. With `demo` left out, and with it False, a pass
        # prints the one line it printed before the smoke test knew the demo.
        for options in ({}, {'demo': False}):
            with self.subTest(**options):
                self.clear()
                self.site(REFUSED)
                deploy.smoke(SITE, **options)
                self.assertEqual(self.said(), [PASSED])

    def test_with_the_demo_on_the_page_carries_the_tag_and_registration_is_closed(self):
        site = self.demo_site(REFUSED)
        deploy.smoke(SITE, demo=True)
        self.assertEqual(site.requests, [('/', None), ('/health/ready', None),
                                         ('/bff/auth/login', deploy.SMOKE_LOGIN),
                                         ('/bff/auth/register', {})])
        self.assertEqual(self.clock.sleeps, [])
        self.assertEqual(self.said(), [
            PASSED + " It is the public demo: the page carries the demo's tag, and a registration "
                     'with an empty body was refused as closed.'])

    def test_the_tag_and_the_refusal_are_the_ones_the_bff_writes(self):
        # CONTROL: green as written. The three are read in the BFF's own sources, as text: a tag,
        # a code or a route changed there fails here, and not at a deployment with the demo on,
        # whose smoke test would take the new one for a wrong answer. Seen red with each of the
        # three sources changed.
        spa = backend_source('AzureBank.Bff', 'Extensions', 'SpaHostingExtensions.cs')
        self.assertEqual(re.findall(r'const string DemoTag = "((?:[^"\\]|\\.)*)";', spa),
                         [deploy.DEMO_TAG.replace('"', '\\"')])
        codes = backend_source('AzureBank.Shared', 'Constants', 'ErrorCodes.cs')
        self.assertEqual(re.findall(r'const string RegistrationClosed = "([^"]*)";', codes),
                         [deploy.REGISTRATION_CLOSED])
        # The route: the controller's one prefix, and the word of its one action for a registration.
        controller = backend_source('AzureBank.Bff', 'Controllers', 'BffAuthController.cs')
        self.assertEqual(controller.count('[HttpPost("register")]'), 1)
        self.assertEqual([f'/{prefix}/register' for prefix in re.findall(r'(?m)^\[Route\("([^"]*)"\)\]', controller)],
                         [deploy.REGISTER_PATH])
        # And what these tests type is what the script holds.
        self.assertEqual((deploy.DEMO_TAG, deploy.REGISTRATION_CLOSED, deploy.REGISTER_PATH),
                         (TAG, 'REGISTRATION_CLOSED', '/bff/auth/register'))
        self.assertTrue(deploy.is_closed(*CLOSED))
        self.assertTrue(deploy.is_spa(*DEMO_PAGE), 'the tag does not stop the page from being the SPA')

    def test_with_the_demo_on_a_page_without_the_tag_fails(self):
        site = self.demo_site(REFUSED, page=(200, 'text/html', SPA))
        with self.assertRaises(deploy.SmokeFailed) as raised:
            deploy.smoke(SITE, demo=True)
        self.assertEqual(str(raised.exception), (
            f'{WRONG_PAGE} The page does not carry the tag of the public demo ({TAG}), and the '
            "app's containers say the demo is on."))
        self.assertGreaterEqual(self.clock.now, 300)
        self.assertGreater(len(site.requests), 60, 'it is asked again until the deadline')
        self.assertEqual(site.sign_ins(), [], 'no door is tried behind a page that is wrong')

    def test_with_the_demo_off_a_page_with_the_tag_fails(self):
        for options in ({}, {'demo': False}):
            with self.subTest(**options):
                began = self.clock.now
                site = self.site(REFUSED, page=DEMO_PAGE)
                with self.assertRaises(deploy.SmokeFailed) as raised:
                    deploy.smoke(SITE, **options)
                self.assertEqual(str(raised.exception), (
                    f'{WRONG_PAGE} The page carries the tag of the public demo ({TAG}), and the '
                    "app's containers say the demo is off."))
                self.assertGreaterEqual(self.clock.now - began, 300)
                self.assertEqual(site.sign_ins(), [])

    def test_a_page_that_gets_its_tag_right_before_the_deadline_passes(self):
        # A revision that just began to answer is asked again, as for any other wrong page.
        site = self.demo_site(REFUSED, page=[(200, 'text/html', SPA), (200, 'text/html', SPA), DEMO_PAGE])
        deploy.smoke(SITE, demo=True)
        self.assertEqual(self.clock.sleeps, [5, 5])
        self.assertEqual(len(site.registrations()), 1)

    def test_a_page_that_is_wrong_another_way_says_nothing_of_the_tag(self):
        # CONTROL: green before this change. The tag is named only on a page that is the SPA: a
        # page that is something else, or no answer at all, fails in the words it failed in before.
        for demo in (False, True):
            for page, told in (((200, 'text/html', '<!doctype html><title>Welcome</title>' + TAG), '200'),
                               ((503, 'text/html', DEMO_SPA), '503')):
                with self.subTest(demo=demo, page=told):
                    self.demo_site(REFUSED, page=page)
                    with self.assertRaises(deploy.SmokeFailed) as raised:
                        deploy.smoke(SITE, demo=demo)
                    self.assertEqual(str(raised.exception), WRONG_PAGE.replace('/ -> 200', f'/ -> {told}'))

    def test_with_the_demo_off_no_registration_is_ever_sent(self):
        # CONTROL: green before this change, and the guard that the smoke test cannot register
        # somebody: with the demo off the door is open (here it answers as if it had registered
        # the caller), and no request may reach it, whatever the other answers are.
        dropped = ConnectionResetError(104, 'Connection reset by peer')
        for options in ({}, {'demo': False}):
            for sign_in in ((REFUSED,), (LIMITED, REFUSED), (LIMITED,), ((404, 'text/plain', ''),),
                            (NO_TABLES,), (dropped,), (dropped, REFUSED)):
                for page in ((200, 'text/html', SPA), DEMO_PAGE):
                    with self.subTest(sign_in=[str(answer)[:20] for answer in sign_in], tag=page is DEMO_PAGE,
                                      **options):
                        site = self.site(*sign_in, page=page)
                        with contextlib.suppress(deploy.SmokeFailed, deploy.SmokeUnproven):
                            deploy.smoke(SITE, **options)
                        self.assertEqual(site.registrations(), [])
                        self.assertEqual(set(site.sign_ins()) - {'/bff/auth/login'}, set())

    def test_with_the_demo_on_the_registration_is_asked_only_after_the_sign_in_was_refused(self):
        # CONTROL: green before this change. A sign-in that fails or proves nothing ends the smoke
        # test where it ended before: the second probe is not sent after it.
        for sign_in, verdict in (((404, 'text/plain', ''), deploy.SmokeFailed), (NO_TABLES, deploy.SmokeFailed),
                                 (LIMITED, deploy.SmokeUnproven)):
            with self.subTest(sign_in=sign_in[0]):
                site = self.demo_site(sign_in)
                with self.assertRaisesRegex(verdict, 'the sign-in probe'):
                    deploy.smoke(SITE, demo=True)
                self.assertEqual(site.registrations(), [])

    def test_an_open_registration_fails_and_its_answer_is_never_printed(self):
        site = self.demo_site(REFUSED, register=OPEN)
        with self.assertRaises(deploy.SmokeFailed) as raised:
            deploy.smoke(SITE, demo=True)
        self.assertEqual(str(raised.exception), not_closed('201 with no error code of the shape expected'))
        for hidden in ('PLANTED', 'example.invalid"', 'success'):
            self.assertNotIn(hidden, str(raised.exception) + self.printed())
        # Tried as the sign-in is: four times, and never with a body that could register anybody.
        self.assertEqual(site.registrations(), [{}] * 4)
        self.assertEqual(self.clock.sleeps, [20, 20, 20])

    def test_a_registration_refused_with_another_code_fails(self):
        for answer, got in (
                ((403, 'application/json', '{"status":403,"errorCode":"AUTH_FORBIDDEN"}'), '403 AUTH_FORBIDDEN'),
                # The code under another status, and an open door that answers the empty body.
                ((200, 'application/json', '{"errorCode":"REGISTRATION_CLOSED"}'), '200 REGISTRATION_CLOSED'),
                (NOT_VALID, '400 VALIDATION_ERROR'),
                # A 403 that is not the BFF's: no error code, one that is not text, one of another
                # shape, a body that is not JSON, JSON that is not an object.
                ((403, 'application/json', '{"status":403,"detail":"PLANTED-VALUE"}'), None),
                ((403, 'application/json', '{"errorCode":{"PLANTED-VALUE":1}}'), None),
                ((403, 'application/json', '{"errorCode":"PLANTED-VALUE in another shape"}'), None),
                ((403, 'application/json', '{"errorCode":"registration_closed"}'), None),
                ((403, 'application/json', '{"errorCode":"REGISTRATION_CLOSED\\nPLANTED-VALUE"}'), None),
                ((403, 'text/html', '<html>REGISTRATION_CLOSED PLANTED-VALUE</html>'), None),
                ((403, 'application/json', '["REGISTRATION_CLOSED", "PLANTED-VALUE"]'), None),
                # CONTROL: green as written. Forty characters is the longest code a line shows: one
                # more, and none is shown. Seen red with the length taken out of the shape.
                ((403, 'application/json', json.dumps({'errorCode': 'A' * 41})), None),
                ((403, 'application/json', json.dumps({'errorCode': 'B' * 40})), '403 ' + 'B' * 40)):
            with self.subTest(answer=answer[2][:40]):
                self.clear()
                site = self.demo_site(REFUSED, register=answer)
                with self.assertRaises(deploy.SmokeFailed) as raised:
                    deploy.smoke(SITE, demo=True)
                self.assertEqual(str(raised.exception), not_closed(
                    got or f'{answer[0]} with no error code of the shape expected'))
                for hidden in ('PLANTED', 'registration_closed', '\n'):
                    self.assertNotIn(hidden, str(raised.exception) + self.printed().rstrip('\n'))
                self.assertEqual(len(site.registrations()), 4)

    def test_a_rate_limited_registration_probe_is_unproven(self):
        site = self.demo_site(REFUSED, register=LIMITED)
        with self.assertRaises(deploy.SmokeUnproven) as raised:
            deploy.smoke(SITE, demo=True)
        self.assertEqual(str(raised.exception), registration_unproven('429 (rate limited)'))
        self.assertEqual(site.registrations(), [{}] * 4)
        self.assertEqual(self.clock.sleeps, [65, 65, 65], 'each wait is a full window of the shared limit')

    def test_a_registration_that_gets_no_answer_is_unproven_and_is_tried_again(self):
        for dropped in (urllib.error.URLError('timed out'), *DROPPED):
            with self.subTest(dropped=type(dropped).__name__):
                self.clock.sleeps.clear()
                site = self.demo_site(REFUSED, register=dropped)
                with self.assertRaises(deploy.SmokeUnproven) as raised:
                    deploy.smoke(SITE, demo=True)
                self.assertEqual(str(raised.exception), registration_unproven('no answer'))
                self.assertEqual(len(site.registrations()), 4)
                self.assertEqual(self.clock.sleeps, [20, 20, 20])

    def test_a_registration_refused_as_closed_after_a_rate_limit_or_a_drop_passes(self):
        site = self.demo_site(REFUSED, register=[LIMITED, ConnectionResetError(104, 'reset'), NOT_VALID, CLOSED])
        deploy.smoke(SITE, demo=True)
        self.assertEqual(len(site.registrations()), 4)
        self.assertEqual(self.clock.sleeps, [65, 20, 20])
        self.assertEqual(len(self.said()), 1, 'one line, and only once every answer was right')

    def test_the_verdict_of_the_registration_probe_is_its_last_try(self):
        self.demo_site(REFUSED, register=[LIMITED, LIMITED, LIMITED, OPEN])
        with self.assertRaisesRegex(deploy.SmokeFailed, 'got 201'):
            deploy.smoke(SITE, demo=True)
        self.demo_site(REFUSED, register=[OPEN, LIMITED])
        with self.assertRaises(deploy.SmokeUnproven):
            deploy.smoke(SITE, demo=True)
        self.assertEqual(self.printed(), '', 'a run that did not pass never says it passed')

    def test_page_healthy_and_refused_sign_in_pass(self):
        site = self.site(REFUSED)
        deploy.smoke(SITE)
        self.assertEqual(site.requests, [('/', None), ('/health/ready', None),
                                         ('/bff/auth/login', deploy.SMOKE_LOGIN)])
        self.assertEqual(self.clock.sleeps, [])

    def test_the_sign_in_goes_to_the_bffs_own_door_never_to_the_proxied_path(self):
        site = self.site(LIMITED, (404, 'text/plain', ''), REFUSED)
        deploy.smoke(SITE)
        self.assertEqual(set(site.sign_ins()), {'/bff/auth/login'})
        self.assertNotIn('/api/auth/login', [path for path, _ in site.requests])

    def test_the_password_passes_the_bffs_own_rule_so_the_request_reaches_the_api(self):
        # backend/src/AzureBank.Shared/Constants/ValidationRules.cs, PasswordPattern
        self.assertRegex(deploy.SMOKE_LOGIN['password'],
                         r'^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^a-zA-Z0-9])[\x20-\x7E]{8,128}$')
        self.assertTrue(deploy.SMOKE_LOGIN['email'].endswith('.invalid'), 'nobody can register it')

    def test_a_degraded_readiness_is_not_a_pass(self):
        site = self.site(REFUSED, ready=(200, 'text/plain', 'Degraded'))
        with self.assertRaisesRegex(deploy.SmokeFailed, 'Degraded'):
            deploy.smoke(SITE)
        self.assertGreaterEqual(self.clock.now, 300)
        self.assertEqual(site.sign_ins(), [])

    def test_a_generic_page_is_not_the_spa(self):
        self.site(REFUSED, page=(200, 'text/html', '<!doctype html><title>Welcome</title>'))
        with self.assertRaisesRegex(deploy.SmokeFailed, 'Smoke test failed'):
            deploy.smoke(SITE)

    def test_a_404_on_the_sign_in_fails_after_four_tries(self):
        site = self.site((404, 'text/plain', ''))
        with self.assertRaisesRegex(deploy.SmokeFailed, 'got 404'):
            deploy.smoke(SITE)
        self.assertEqual(len(site.sign_ins()), 4)
        self.assertEqual(self.clock.sleeps, [20, 20, 20])

    def test_an_outage_that_ends_before_the_last_try_passes(self):
        site = self.site(NO_DATABASE, NO_DATABASE, NO_DATABASE, REFUSED)
        deploy.smoke(SITE)
        self.assertEqual(len(site.sign_ins()), 4)

    def test_a_database_that_was_never_migrated_fails_though_the_app_says_healthy(self):
        for answer, status in ((NO_TABLES, 500), (NO_DATABASE, 503)):
            with self.subTest(status=status):
                self.site(answer)
                with self.assertRaisesRegex(deploy.SmokeFailed, f'got {status}'):
                    deploy.smoke(SITE)

    def test_a_401_with_another_code_fails(self):
        self.site((401, 'application/problem+json', '{"status":401,"errorCode":"ACCOUNT_LOCKED"}'))
        with self.assertRaisesRegex(deploy.SmokeFailed, 'ACCOUNT_LOCKED'):
            deploy.smoke(SITE)

    def test_the_code_under_another_status_fails(self):
        self.site((403, 'application/problem+json', '{"status":403,"errorCode":"INVALID_CREDENTIALS"}'))
        with self.assertRaisesRegex(deploy.SmokeFailed, 'got 403'):
            deploy.smoke(SITE)

    def test_a_401_that_is_not_json_fails(self):
        self.site((401, 'text/html', '<html>INVALID_CREDENTIALS</html>'))
        with self.assertRaises(deploy.SmokeFailed):
            deploy.smoke(SITE)

    def test_only_429_is_unproven_and_each_wait_is_a_full_window(self):
        site = self.site(LIMITED)
        with self.assertRaisesRegex(deploy.SmokeUnproven, '429'):
            deploy.smoke(SITE)
        self.assertEqual(len(site.sign_ins()), 4)
        self.assertEqual(self.clock.sleeps, [65, 65, 65])

    def test_429_then_the_refusal_passes(self):
        site = self.site(LIMITED, REFUSED)
        deploy.smoke(SITE)
        self.assertEqual(len(site.sign_ins()), 2)
        self.assertEqual(self.clock.sleeps, [65])

    def test_no_answer_at_all_is_unproven(self):
        self.site(urllib.error.URLError('timed out'))
        with self.assertRaisesRegex(deploy.SmokeUnproven, 'no answer'):
            deploy.smoke(SITE)
        self.assertEqual(self.clock.sleeps, [20, 20, 20])

    def test_a_dropped_connection_on_the_sign_in_is_no_answer_and_is_tried_again(self):
        for dropped in DROPPED:
            with self.subTest(dropped=type(dropped).__name__):
                self.clock.sleeps.clear()
                site = self.site(dropped)
                with self.assertRaisesRegex(deploy.SmokeUnproven, 'no answer'):
                    deploy.smoke(SITE)
                self.assertEqual(len(site.sign_ins()), 4)
                self.assertEqual(self.clock.sleeps, [20, 20, 20])

    def test_a_dropped_sign_in_then_the_refusal_passes(self):
        site = self.site(http.client.RemoteDisconnected('Remote end closed connection without response'),
                         ConnectionResetError(104, 'Connection reset by peer'), REFUSED)
        deploy.smoke(SITE)
        self.assertEqual(len(site.sign_ins()), 3)

    def test_a_dropped_connection_on_the_page_is_asked_again_until_the_deadline(self):
        for dropped in DROPPED:
            with self.subTest(dropped=type(dropped).__name__):
                began = self.clock.now
                site = self.site(REFUSED, page=dropped)
                with self.assertRaisesRegex(deploy.SmokeFailed, f'/ -> no answer .{type(dropped).__name__}'):
                    deploy.smoke(SITE)
                self.assertGreaterEqual(self.clock.now - began, 300)
                self.assertGreater(len(site.requests), 60)
                self.assertEqual(site.sign_ins(), [])

    def test_a_dropped_connection_on_the_readiness_answer_is_asked_again_until_the_deadline(self):
        site = self.site(REFUSED, ready=ConnectionResetError(104, 'Connection reset by peer'))
        with self.assertRaisesRegex(deploy.SmokeFailed,
                                    '/ -> 200, /health/ready -> no answer .ConnectionResetError'):
            deploy.smoke(SITE)
        self.assertGreaterEqual(self.clock.now, 300)
        self.assertEqual(site.sign_ins(), [])

    def test_a_page_that_answers_after_dropped_connections_passes(self):
        reset = ConnectionResetError(104, 'Connection reset by peer')
        site = self.site(REFUSED, page=[reset, reset, (200, 'text/html', SPA)],
                         ready=[http.client.RemoteDisconnected('closed'), (200, 'text/plain', 'Healthy')])
        deploy.smoke(SITE)
        self.assertEqual(self.clock.sleeps, [5, 5, 5])
        self.assertEqual(len(site.sign_ins()), 1)

    def test_the_verdict_is_the_last_try(self):
        failure = (500, 'application/problem+json', '{"errorCode":"INTERNAL_ERROR"}')
        self.site(LIMITED, LIMITED, LIMITED, failure)
        with self.assertRaisesRegex(deploy.SmokeFailed, 'got 500'):
            deploy.smoke(SITE)
        self.clock.sleeps.clear()
        self.site(failure, LIMITED)
        with self.assertRaises(deploy.SmokeUnproven):
            deploy.smoke(SITE)
        self.assertEqual(self.clock.sleeps, [20, 65, 65])

    def test_an_error_status_is_an_answer_not_an_exception(self):
        headers = Message()
        headers['Content-Type'] = 'application/json; charset=utf-8'
        error = deploy.urllib.error.HTTPError(SITE, 401, 'Unauthorized', headers,
                                              MagicMock(read=lambda n: REFUSED[2].encode()))
        opener = MagicMock()
        opener.open.side_effect = error
        self.assertEqual(deploy.fetch(opener, SITE, {}), REFUSED)

    def test_redirects_are_not_followed(self):
        self.assertIsNone(deploy.NoRedirect().redirect_request(None, None, 302, '', {}, SITE))


def http_answer(status, content_type, body, *headers):
    lines = [f'HTTP/1.1 {status} Answer', f'Content-Type: {content_type}',
             f'Content-Length: {len(body.encode())}', 'Connection: close', *headers]
    return '\r\n'.join(lines).encode() + b'\r\n\r\n' + body.encode()


HANG_UP, RESET = 'hang up', 'reset'
GOOD_PAGE = http_answer(200, 'text/html', SPA)
GOOD_READY = http_answer(200, 'text/plain', 'Healthy')
GOOD_REFUSAL = http_answer(401, 'application/json; charset=utf-8', REFUSED[2])
GOOD_DEMO_PAGE = http_answer(200, 'text/html', DEMO_SPA)
GOOD_CLOSED = http_answer(403, 'application/json; charset=utf-8', CLOSED[2])


class LocalSite:
    """A real server on 127.0.0.1, for what only a real connection can do: hang up, reset, answer
    something that is not HTTP, redirect. Each path answers with bytes, HANG_UP or RESET, or with a
    list of those in order (the last one repeats). A path nobody named hangs up."""

    def __init__(self, routes):
        self.routes = {path: answers if isinstance(answers, list) else [answers]
                       for path, answers in routes.items()}
        self.requests = []
        self.closing = False
        self.listener = socket.create_server(('127.0.0.1', 0))
        self.url = f'http://127.0.0.1:{self.listener.getsockname()[1]}'
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()

    def close(self):
        # A connection of our own wakes the thread that waits in accept(); closing the listener
        # from here would not, everywhere.
        self.closing = True
        with contextlib.suppress(OSError), socket.create_connection(self.listener.getsockname(), 5):
            pass
        self.thread.join(5)
        self.listener.close()

    def paths(self):
        return [path for _, path, _, _ in self.requests]

    def serve(self):
        while True:
            try:
                connection, _ = self.listener.accept()
            except OSError:
                return
            with connection:
                if self.closing:
                    return
                connection.settimeout(5)
                try:
                    head, body = self.read_request(connection)
                except OSError:
                    continue
                method, path = head.split(' ')[:2]
                self.requests.append((method, path, head.lower(), body))
                answers = self.routes.get(path, [HANG_UP])
                answer = answers.pop(0) if len(answers) > 1 else answers[0]
                if answer == RESET:
                    # Closing with a zero linger sends a reset in place of an orderly end.
                    connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER,
                                          struct.pack('hh' if os.name == 'nt' else 'ii', 1, 0))
                elif answer != HANG_UP:
                    connection.sendall(answer)

    @staticmethod
    def read_request(connection):
        """The whole request: closing on unread bytes would turn an answer into a reset."""
        data = b''
        while b'\r\n\r\n' not in data:
            chunk = connection.recv(65536)
            if not chunk:
                raise OSError('the client went away')
            data += chunk
        head, _, body = data.partition(b'\r\n\r\n')
        length = re.search(rb'(?im)^content-length: *(\d+)', head)
        while length and len(body) < int(length.group(1)):
            chunk = connection.recv(65536)
            if not chunk:
                raise OSError('the client went away')
            body += chunk
        return head.decode(), body.decode()


class RealConnectionTests(Offline):
    """`deploy.smoke` with the real `deploy.fetch`, against a server on this machine. Time is still
    a counter. What these prove that a stand-in for `fetch` cannot: which exceptions urllib really
    raises when a connection is dropped, and that every one of them ends in a verdict."""

    def site(self, routes=None):
        site = LocalSite({'/': GOOD_PAGE, '/health/ready': GOOD_READY, '/bff/auth/login': GOOD_REFUSAL,
                          **(routes or {})})
        self.addCleanup(site.close)
        return site

    def test_the_three_requests_as_they_are_really_sent_pass(self):
        site = self.site()
        deploy.smoke(site.url)
        self.assertEqual([(method, path) for method, path, _, _ in site.requests],
                         [('GET', '/'), ('GET', '/health/ready'), ('POST', '/bff/auth/login')])
        _, _, head, body = site.requests[-1]
        self.assertIn('content-type: application/json', head)
        self.assertNotIn('cookie:', head)
        self.assertEqual(json.loads(body), deploy.SMOKE_LOGIN)

    def test_the_four_requests_of_a_demo_as_they_are_really_sent_pass(self):
        site = self.site({'/': GOOD_DEMO_PAGE, '/bff/auth/register': GOOD_CLOSED})
        deploy.smoke(site.url, demo=True)
        self.assertEqual([(method, path) for method, path, _, _ in site.requests],
                         [('GET', '/'), ('GET', '/health/ready'), ('POST', '/bff/auth/login'),
                          ('POST', '/bff/auth/register')])
        for _, path, head, _ in site.requests:
            self.assertNotIn('cookie:', head, f'{path}: no request carries a cookie')
        _, _, head, body = site.requests[-1]
        self.assertIn('content-type: application/json', head)
        self.assertEqual(body, '{}', 'an empty object: a body that could register nobody')

    def test_a_registration_the_server_hangs_up_on_is_unproven_not_a_crash(self):
        for drop in (HANG_UP, RESET, b'this is not HTTP\r\n\r\n'):
            with self.subTest(drop=drop):
                self.clock.sleeps.clear()
                site = self.site({'/': GOOD_DEMO_PAGE, '/bff/auth/register': drop})
                with self.assertRaisesRegex(deploy.SmokeUnproven, 'the registration probe got no answer last'):
                    deploy.smoke(site.url, demo=True)
                self.assertEqual(site.paths().count('/bff/auth/register'), 4)
                self.assertEqual(self.clock.sleeps, [20, 20, 20])

    def test_a_sign_in_the_server_hangs_up_on_is_unproven_not_a_crash(self):
        site = self.site({'/bff/auth/login': HANG_UP})
        with self.assertRaisesRegex(deploy.SmokeUnproven, 'no answer'):
            deploy.smoke(site.url)
        self.assertEqual(site.paths().count('/bff/auth/login'), 4)
        self.assertEqual(self.clock.sleeps, [20, 20, 20])

    def test_a_sign_in_the_server_resets_is_unproven_not_a_crash(self):
        site = self.site({'/bff/auth/login': RESET})
        with self.assertRaisesRegex(deploy.SmokeUnproven, 'no answer'):
            deploy.smoke(site.url)
        self.assertEqual(site.paths().count('/bff/auth/login'), 4)

    def test_a_sign_in_answered_with_something_that_is_not_http_is_unproven_not_a_crash(self):
        site = self.site({'/bff/auth/login': b'this is not HTTP\r\n\r\n'})
        with self.assertRaisesRegex(deploy.SmokeUnproven, 'no answer'):
            deploy.smoke(site.url)
        self.assertEqual(site.paths().count('/bff/auth/login'), 4)

    def test_a_page_the_server_hangs_up_on_fails_at_the_deadline_not_at_the_first_drop(self):
        for drop in (HANG_UP, RESET):
            with self.subTest(drop=drop):
                site = self.site({'/': drop})
                with self.assertRaisesRegex(deploy.SmokeFailed, '/ -> no answer'):
                    deploy.smoke(site.url, timeout=30)
                self.assertEqual(site.paths(), ['/'] * 7)

    def test_two_dropped_connections_then_the_page_pass(self):
        site = self.site({'/': [HANG_UP, RESET, GOOD_PAGE]})
        deploy.smoke(site.url)
        self.assertEqual(site.paths(), ['/', '/', '/', '/health/ready', '/bff/auth/login'])

    def test_a_redirect_is_an_answer_and_is_not_followed(self):
        site = self.site({'/': http_answer(302, 'text/plain', '', 'Location: /elsewhere'),
                          '/elsewhere': GOOD_PAGE})
        with self.assertRaisesRegex(deploy.SmokeFailed, '/ -> 302'):
            deploy.smoke(site.url, timeout=30)
        self.assertNotIn('/elsewhere', site.paths())
        self.assertNotIn('/bff/auth/login', site.paths())


class SmokeInTheGateTests(Offline):
    """The real smoke test inside the real deploy: what a rate limit and a wrong answer lead to."""

    def setUp(self):
        super().setUp()
        self.azure = FakeAzure(self.out)
        self.start(patch('deploy.rest', self.azure.rest))
        self.start(patch('deploy.run_migration'))

    def test_a_rate_limited_sign_in_leaves_the_new_revision_and_exits_unproven(self):
        self.start(patch('deploy.fetch', Site(LIMITED).fetch))
        with self.assertRaisesRegex(deploy.SmokeUnproven, 'not put back'):
            deploy.deploy(SUBSCRIPTION, GROUP, NEW)
        self.assertEqual(len(self.azure.app_patches()), 1)

    def test_a_wrong_sign_in_answer_puts_the_app_back(self):
        self.start(patch('deploy.fetch', Site((404, 'text/plain', '')).fetch))
        with self.assertRaisesRegex(RuntimeError, f'put back to {OLD}'):
            deploy.deploy(SUBSCRIPTION, GROUP, NEW)
        self.assertEqual(len(self.azure.app_patches()), 2)

    def test_a_page_that_stops_answering_after_the_app_moved_puts_the_app_back(self):
        self.start(patch('deploy.fetch', Site(REFUSED, page=ConnectionResetError(104, 'reset')).fetch))
        with self.assertRaisesRegex(RuntimeError, f'no answer.*put back to {OLD}'):
            deploy.deploy(SUBSCRIPTION, GROUP, NEW)
        self.assertEqual(len(self.azure.app_patches()), 2)

    def test_a_sign_in_whose_connection_is_dropped_leaves_the_new_revision_and_exits_unproven(self):
        dropped = http.client.RemoteDisconnected('Remote end closed connection without response')
        self.start(patch('deploy.fetch', Site(dropped).fetch))
        with self.assertRaisesRegex(deploy.SmokeUnproven, 'no answer'):
            deploy.deploy(SUBSCRIPTION, GROUP, NEW)
        self.assertEqual(len(self.azure.app_patches()), 1)

    def test_in_actions_the_smoke_line_comes_after_the_mask(self):
        self.start(patch('deploy.fetch', Site(REFUSED).fetch))
        deploy.deploy(SUBSCRIPTION, GROUP, NEW, in_actions=True)
        lines = self.printed().splitlines()
        holding = [index for index, line in enumerate(lines) if ADDRESS in line]
        self.assertEqual(lines[holding[0]], f'::add-mask::{ADDRESS}')
        self.assertIn('Smoke passed', lines[holding[-1]])

    def test_with_the_demo_on_the_four_answers_of_a_demo_pass_and_nothing_is_put_back(self):
        self.azure.turn_the_demo_on()
        site = demo_site(REFUSED)
        self.start(patch('deploy.fetch', site.fetch))
        deploy.deploy(SUBSCRIPTION, GROUP, NEW)
        self.assertEqual([path for path, _ in site.requests],
                         ['/', '/health/ready', '/bff/auth/login', '/bff/auth/register'])
        self.assertEqual(len(self.azure.app_patches()), 1)
        self.assertIn('It is the public demo', self.printed().splitlines()[-1])

    def test_with_the_demo_on_an_open_registration_puts_the_app_back(self):
        self.azure.turn_the_demo_on()
        for answer, got in ((OPEN, '201 with no error code of the shape expected'),
                            (NOT_VALID, '400 VALIDATION_ERROR')):
            with self.subTest(got=got):
                self.azure.calls.clear()
                self.start(patch('deploy.fetch', demo_site(REFUSED, register=answer).fetch))
                with self.assertRaises(RuntimeError) as raised:
                    deploy.deploy(SUBSCRIPTION, GROUP, NEW)
                self.assertEqual(len(self.azure.app_patches()), 2, 'the deployment, and the put-back')
                self.assertTrue(str(raised.exception).startswith(
                    f'{not_closed(got)} The app was put back to '), str(raised.exception))
                self.assertNotIn('PLANTED', str(raised.exception) + self.printed())

    def test_with_the_demo_on_a_page_without_the_tag_puts_the_app_back(self):
        self.azure.turn_the_demo_on()
        site = demo_site(REFUSED, page=(200, 'text/html', SPA))
        self.start(patch('deploy.fetch', site.fetch))
        with self.assertRaisesRegex(RuntimeError, 'does not carry the tag of the public demo.*put back to'):
            deploy.deploy(SUBSCRIPTION, GROUP, NEW)
        self.assertEqual(len(self.azure.app_patches()), 2)
        self.assertEqual(site.sign_ins(), [])

    def test_with_the_demo_on_a_rate_limited_registration_leaves_the_new_revision_and_exits_unproven(self):
        self.azure.turn_the_demo_on()
        self.start(patch('deploy.fetch', demo_site(REFUSED, register=LIMITED).fetch))
        with self.assertRaises(deploy.SmokeUnproven) as raised:
            deploy.deploy(SUBSCRIPTION, GROUP, NEW)
        self.assertEqual(str(raised.exception), registration_unproven('429 (rate limited)'))
        self.assertEqual(len(self.azure.app_patches()), 1, 'nothing is put back')


if __name__ == '__main__':
    unittest.main()
