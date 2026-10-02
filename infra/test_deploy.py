"""Offline tests of the deployment script's decisions.

Every Azure and HTTP answer below is invented here. The tests prove what the script does with an
answer; what Azure and the app really answer is read on the first deployment (README.md, "Not
measured yet"). Time is a counter: a wait of fifteen minutes costs nothing.
"""

import contextlib
import copy
import datetime
from email.message import Message
import io
import subprocess
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
OLD = 'b' * 40
NEW = 'a' * 40
ADDRESS = 'azurebank.example.invalid'
BEFORE = 'azurebank--before'


def container(name, image):
    return {'name': name, 'image': image, 'env': [{'name': 'KEY', 'secretRef': 'key'}],
            'resources': {'cpu': 0.25, 'memory': '0.5Gi'}}


def app_resource(tag=OLD):
    return {
        'location': 'italynorth',
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
        'properties': {
            'provisioningState': 'Succeeded',
            'configuration': {
                'triggerType': 'Manual', 'replicaRetryLimit': 0, 'replicaTimeout': 600,
                'manualTriggerConfig': {'parallelism': 1, 'replicaCompletionCount': 1},
            },
            'template': {'containers': [container(name, f'ghcr.io/gurgant/azurebank-tools:{tag}')]},
        },
    }


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


def execution(name, status, started=None):
    properties = {'status': status}
    if started:
        properties['startTime'] = started
    return {'name': name, 'properties': properties}


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
    """What `deploy.rest` talks to: one app, its revisions and replicas, and the jobs."""

    def __init__(self, out):
        self.out = out
        self.app = app_resource()
        self.jobs = {'azurebank-migrate': job_resource()}
        self.revisions = {BEFORE: {'active': True, 'provisioningState': 'Provisioned',
                                   'runningState': 'Running', 'healthState': 'Healthy'}}
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
        self.calls = []
        self.events = []
        self.printed_before_first_write = None

    def writes(self):
        return [(method, resource_id) for method, resource_id, _ in self.calls if method != 'GET']

    def app_patches(self):
        return [body for method, resource_id, body in self.calls
                if method == 'PATCH' and resource_id == APP_ID]

    def rest(self, method, resource_id, body=None):
        self.calls.append((method, resource_id, copy.deepcopy(body)))
        if method != 'GET' and self.printed_before_first_write is None:
            self.printed_before_first_write = self.out.getvalue()
        if self.refuse and self.refuse(method, resource_id, body):
            raise deploy.AzError('Forbidden: AuthorizationFailed')
        if resource_id == APP_ID:
            return self.app_call(method, body)
        if resource_id.startswith(APP_ID + '/revisions/'):
            return self.revision_call(resource_id[len(APP_ID + '/revisions/'):])
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
        if self.reads_before_old_is_inactive:
            self.reads_before_old_is_inactive -= 1
            if not self.reads_before_old_is_inactive:
                self.deactivate_all_but(self.app['properties']['latestReadyRevisionName'])
        self.events.append(f'read {tail}')
        return {'name': tail, 'properties': copy.deepcopy(self.revisions[tail])}

    def job_call(self, method, tail, body):
        name, _, rest = tail.partition('/')
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


class DeployCase(Offline):
    """`deploy.deploy` against FakeAzure, with a stand-in for the migration and for the smoke test."""

    def setUp(self):
        super().setUp()
        self.azure = FakeAzure(self.out)
        self.start(patch('deploy.rest', self.azure.rest))
        self.migration = self.start(patch(
            'deploy.run_migration', side_effect=lambda *args: self.azure.events.append('migration')))
        self.smoke = self.start(patch(
            'deploy.smoke', side_effect=lambda *args: self.azure.events.append('smoke')))

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
        start.assert_called_once_with('POST', '/job/start')

    @patch('deploy.rest', return_value={})
    @patch('deploy.executions')
    def test_a_start_that_returns_no_name_is_found_by_what_is_new(self, executions, start):
        old = execution('old-run', 'Succeeded')
        executions.side_effect = [[old], [old], [old, execution('new-run', 'Running')],
                                  [old, execution('new-run', 'Succeeded')]]
        self.assertEqual(deploy.run_migration('/job', 600), 'new-run')
        self.assertEqual(start.call_count, 1)

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
        self.migration.assert_called_once_with(MIGRATE_ID, 600)
        self.smoke.assert_called_once_with(f'https://{ADDRESS}')

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
        with self.assertRaises(ValueError):
            deploy.deploy(SUBSCRIPTION, GROUP, 'main')
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


class OrderTests(DeployCase):
    """A second job, as the pool job will be: it must not run a new image on an old schema."""

    def setUp(self):
        super().setUp()
        self.start(patch.dict(deploy.JOBS, {'azurebank-pool': 'pool'}))
        self.azure.jobs['azurebank-pool'] = job_resource('pool')

    def test_the_other_jobs_move_after_the_migration_and_before_the_app(self):
        self.deploy()
        self.assertEqual(self.steps(), ['job azurebank-migrate', 'migration', 'job azurebank-pool',
                                        'app d', 'smoke'])
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-pool']),
                         {'pool': f'ghcr.io/gurgant/azurebank-tools:{NEW}'})

    def test_a_failed_migration_leaves_the_other_jobs_on_the_old_image(self):
        self.migration.side_effect = RuntimeError('migration failed')
        with self.assertRaisesRegex(RuntimeError, 'migration failed'):
            self.deploy()
        self.assertEqual(self.azure.writes(), [('PATCH', MIGRATE_ID)])
        self.assertEqual(deploy.images(self.azure.jobs['azurebank-pool']),
                         {'pool': f'ghcr.io/gurgant/azurebank-tools:{OLD}'})


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
}
JOB_DRIFTS = {
    'configuration.triggerType': lambda a: job_configuration(a).update(triggerType='Schedule'),
    'configuration.manualTriggerConfig.parallelism':
        lambda a: job_configuration(a)['manualTriggerConfig'].update(parallelism=3),
    'configuration.replicaRetryLimit': lambda a: job_configuration(a).update(replicaRetryLimit=2),
}


class ShapeTests(DeployCase):
    def refused_before_any_change(self, field, drift):
        drift(self.azure)
        with self.assertRaises(deploy.ShapeError) as raised:
            self.deploy()
        self.assertIn(field, str(raised.exception))
        self.assertEqual(self.azure.writes(), [], 'nothing may be changed on a drifted resource')
        self.migration.assert_not_called()
        self.smoke.assert_not_called()

    def test_values_azure_leaves_out_read_as_their_defaults(self):
        app_part(self.azure, 'template', 'scale').update(minReplicas=None)
        del app_part(self.azure, 'configuration', 'ingress')['allowInsecure']
        app_part(self.azure, 'configuration', 'ingress').update(additionalPortMappings=None)
        job_configuration(self.azure).update(replicaRetryLimit=None)
        self.deploy()
        self.assertEqual(self.steps()[-1], 'smoke')

    def test_an_app_without_ingress_is_refused_before_any_change(self):
        self.refused_before_any_change(
            'configuration.ingress.external', lambda a: app_part(a, 'configuration').update(ingress=None))

    def test_the_app_is_read_again_after_it_moved_and_a_drift_stops_the_run(self):
        self.azure.drift_app_on_patch = lambda template: template['scale'].update(maxReplicas=5)
        with self.assertRaisesRegex(deploy.ShapeError, 'template.scale.maxReplicas'):
            self.deploy()
        self.assertEqual(len(self.azure.app_patches()), 1, 'a drift is reported, not put back')
        self.smoke.assert_not_called()

    def test_the_job_is_read_again_after_it_moved_and_a_drift_stops_the_run(self):
        self.azure.drift_job_on_patch = lambda job: job['properties']['configuration'].update(
            triggerType='Schedule')
        with self.assertRaisesRegex(deploy.ShapeError, 'configuration.triggerType'):
            self.deploy()
        self.migration.assert_not_called()
        self.assertEqual(self.azure.app_patches(), [])


def _drift_test(field, drift):
    def test(self):
        self.refused_before_any_change(field, drift)
    return test


for _field, _drift in {**APP_DRIFTS, **JOB_DRIFTS}.items():
    _name = _field.replace('.', '_')
    setattr(ShapeTests, f'test_a_drift_in_{_name}_is_refused_before_any_change', _drift_test(_field, _drift))


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

    def test_a_diagnosis_azure_refuses_does_not_hide_the_failure(self):
        self.azure.fate['d'] = 'never'
        self.azure.refuse = lambda method, resource_id, body: '/revisions/' in resource_id
        with self.assertRaisesRegex(RuntimeError, f'put back to {OLD}'):
            self.deploy()
        self.assertIn('Could not read why', self.printed())


class MaskTests(DeployCase):
    def setUp(self):
        super().setUp()
        self.smoke.side_effect = lambda url: print(f'checked {url}')

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

    def test_it_is_refused_inside_actions_before_any_call(self):
        with self.assertRaisesRegex(ValueError, 'refused inside GitHub Actions'):
            self.deploy(app_only=True, in_actions=True)
        self.assertEqual(self.azure.calls, [])

    def test_it_keeps_the_gate_and_the_put_back(self):
        self.azure.fate['d'] = 'never'
        with self.assertRaisesRegex(RuntimeError, f'put back to {OLD}'):
            self.deploy(app_only=True)
        self.assertEqual(self.steps(), ['app d', 'app b'])


class MainTests(Offline):
    ENVIRONMENT = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP, 'IMAGE_TAG': NEW}

    def run_main(self, arguments, **extra):
        with patch.dict(deploy.os.environ, {**self.ENVIRONMENT, **extra}, clear=True):
            return deploy.main(arguments)

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


class Site:
    """Stands in for `deploy.fetch`: the page, the readiness answer, and the sign-in answers in order
    (the last one repeats)."""

    def __init__(self, *sign_in, page=(200, 'text/html', SPA), ready=(200, 'text/plain', 'Healthy')):
        self.page, self.ready, self.sign_in = page, ready, list(sign_in)
        self.requests = []

    def fetch(self, opener, url, body=None):
        path = urllib.parse.urlsplit(url).path
        self.requests.append((path, body))
        if path == '/':
            return self.page
        if path == '/health/ready':
            return self.ready
        answer = self.sign_in.pop(0) if len(self.sign_in) > 1 else self.sign_in[0]
        if isinstance(answer, Exception):
            raise answer
        return answer

    def sign_ins(self):
        return [path for path, body in self.requests if body is not None]


class SmokeTests(Offline):
    def site(self, *sign_in, **pages):
        site = Site(*sign_in, **pages)
        self.start(patch('deploy.fetch', site.fetch))
        return site

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

    def test_in_actions_the_smoke_line_comes_after_the_mask(self):
        self.start(patch('deploy.fetch', Site(REFUSED).fetch))
        deploy.deploy(SUBSCRIPTION, GROUP, NEW, in_actions=True)
        lines = self.printed().splitlines()
        holding = [index for index, line in enumerate(lines) if ADDRESS in line]
        self.assertEqual(lines[holding[0]], f'::add-mask::{ADDRESS}')
        self.assertIn('Smoke passed', lines[holding[-1]])


if __name__ == '__main__':
    unittest.main()
