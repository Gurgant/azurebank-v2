"""Offline tests for deployment failure gates; no Azure credentials or network."""

import copy
from email.message import Message
import unittest
from unittest.mock import MagicMock, patch

import deploy


def resource(names):
    return {
        'location': 'italynorth',
        'properties': {
            'provisioningState': 'Succeeded',
            'configuration': {'replicaTimeout': 600},
            'template': {
                'revisionSuffix': 'previous',
                'scale': {'minReplicas': 0, 'maxReplicas': 1},
                'containers': [
                    {'name': name, 'image': 'previous',
                     'env': [{'name': 'KEY', 'secretRef': 'key'}],
                     'resources': {'cpu': 0.25, 'memory': '0.5Gi'}}
                    for name in names
                ],
            },
        },
    }


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


class MigrationTests(unittest.TestCase):
    @patch('deploy.time.sleep')
    @patch('deploy.az', return_value={'name': 'this-run'})
    @patch('deploy.execution_list')
    def test_waits_for_exact_execution_not_historical_success(self, executions, start, sleep):
        old = {'name': 'old-run', 'properties': {'status': 'Succeeded'}}
        running = {'name': 'this-run', 'properties': {'status': 'Running'}}
        success = {'name': 'this-run', 'properties': {'status': 'Succeeded'}}
        executions.side_effect = [[old], [old], [old, running], [old, success]]
        deploy.run_migration('group', 600)
        self.assertEqual(start.call_count, 1)
        self.assertEqual(sleep.call_count, 2)

    def test_non_success_states_fail_closed(self):
        for status in ['Failed', 'Stopped', 'Unknown', None]:
            with self.subTest(status=status), patch('deploy.execution_list', side_effect=[
                [], [{'name': 'run', 'properties': {'status': status}}]
            ]), patch('deploy.az', return_value={'name': 'run'}):
                with self.assertRaises(RuntimeError):
                    deploy.run_migration('group', 600)

    @patch('deploy.az')
    @patch('deploy.execution_list', return_value=[
        {'name': 'previous', 'properties': {'status': 'Running'}}
    ])
    def test_cancelled_workflows_cannot_overlap_a_running_job(self, executions, start):
        with self.assertRaises(RuntimeError):
            deploy.run_migration('group', 600)
        start.assert_not_called()

    @patch('deploy.execution_list', return_value=[])
    @patch('deploy.az', return_value={})
    def test_ambiguous_start_is_not_repeated(self, start, executions):
        with self.assertRaises(RuntimeError):
            deploy.run_migration('group', 600)
        self.assertEqual(start.call_count, 1)

    @patch('deploy.time.monotonic', side_effect=[0, 1000])
    @patch('deploy.execution_list', return_value=[])
    @patch('deploy.az', return_value={'name': 'run'})
    def test_polling_timeout_fails(self, start, executions, clock):
        with self.assertRaisesRegex(RuntimeError, 'timed out'):
            deploy.run_migration('group', 600)


class DeploymentTests(unittest.TestCase):
    @patch('deploy.smoke')
    @patch('deploy.run_migration', side_effect=RuntimeError('migration failed'))
    @patch('deploy.wait_resource')
    @patch('deploy.rest')
    def test_failed_migration_never_patches_the_app(self, rest, wait, migrate, smoke):
        rest.side_effect = [resource(['bff', 'api']), resource(['migrate']), {}]
        with self.assertRaisesRegex(RuntimeError, 'migration failed'):
            deploy.deploy('subscription-placeholder', 'group', 'a' * 40)
        self.assertEqual([call.args[0] for call in rest.call_args_list], ['GET', 'GET', 'PATCH'])
        self.assertTrue(rest.call_args_list[-1].args[1].endswith('/jobs/azurebank-migrate'))
        smoke.assert_not_called()

    @patch('deploy.smoke')
    @patch('deploy.run_migration')
    @patch('deploy.wait_resource')
    @patch('deploy.rest')
    def test_success_patches_both_images_after_migration_then_smokes(self, rest, wait, migrate, smoke):
        app = resource(['bff', 'api'])
        app['properties']['configuration']['ingress'] = {'fqdn': 'example.invalid'}
        rest.side_effect = [app, resource(['migrate']), {}, {}]
        wait.return_value = app
        calls = []
        migrate.side_effect = lambda *args: calls.append('migration')
        smoke.side_effect = lambda *args: calls.append('smoke')
        deploy.deploy('subscription-placeholder', 'group', 'a' * 40)
        app_update = rest.call_args_list[-1]
        self.assertTrue(app_update.args[1].endswith('/containerApps/azurebank'))
        self.assertEqual(set(deploy.images(app_update.args[2])), {'bff', 'api'})
        self.assertTrue(wait.call_args.kwargs['expected_revision'].startswith('azurebank--d-'))
        self.assertEqual(calls, ['migration', 'smoke'])

    @patch('deploy.time.sleep')
    @patch('deploy.rest')
    def test_previous_ready_revision_does_not_pass(self, rest, sleep):
        old = resource(['bff', 'api'])
        old['properties'].update(latestRevisionName='old', latestReadyRevisionName='old')
        new = copy.deepcopy(old)
        new['properties'].update(latestRevisionName='new', latestReadyRevisionName='new')
        rest.side_effect = [old, new]
        deploy.wait_resource('/app', {'bff': 'previous', 'api': 'previous'}, expected_revision='new')
        sleep.assert_called_once()

    @patch('deploy.rest')
    def test_failed_provisioning_is_not_smoke_tested(self, rest):
        failed = resource(['bff', 'api'])
        failed['properties']['provisioningState'] = 'Failed'
        rest.return_value = failed
        with self.assertRaisesRegex(RuntimeError, 'Failed'):
            deploy.wait_resource('/app', {'bff': 'new', 'api': 'new'})

    @patch('deploy.rest')
    def test_invalid_sha_fails_before_azure(self, rest):
        with self.assertRaises(ValueError):
            deploy.deploy('subscription-placeholder', 'group', 'main')
        rest.assert_not_called()


class SmokeTests(unittest.TestCase):
    @staticmethod
    def response(body, content_type='text/html', status=200):
        response = MagicMock()
        response.__enter__.return_value = response
        response.status = status
        response.read.return_value = body.encode()
        response.headers = Message()
        response.headers['Content-Type'] = content_type
        return response

    @patch('deploy.urllib.request.build_opener')
    def test_spa_html_and_ready_200_pass(self, build_opener):
        spa = '<!doctype html><title>AzureBank</title><div id="root"></div><script type="module" src="/assets/index-hash.js"></script>'
        build_opener.return_value.open.side_effect = [self.response(spa), self.response('Healthy', 'text/plain')]
        deploy.smoke('https://example.invalid')
        self.assertEqual([call.args[0] for call in build_opener.return_value.open.call_args_list],
                         ['https://example.invalid/', 'https://example.invalid/health/ready'])

    @patch('deploy.time.sleep')
    @patch('deploy.time.monotonic', side_effect=[0, 0, 301])
    @patch('deploy.urllib.request.build_opener')
    def test_generic_success_page_is_not_the_spa(self, build_opener, clock, sleep):
        build_opener.return_value.open.side_effect = [
            self.response('<!doctype html><title>Welcome</title>'), self.response('Healthy', 'text/plain')]
        with self.assertRaisesRegex(RuntimeError, 'Smoke test failed'):
            deploy.smoke('https://example.invalid')

    def test_redirects_are_not_followed(self):
        self.assertIsNone(deploy.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://example.invalid'))


if __name__ == '__main__':
    unittest.main()
