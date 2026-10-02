"""Offline tests of the two PowerShell scripts and of the two templates.

The scripts run for real, in PowerShell 7, against a stand-in for the Azure CLI: they touch neither
Azure nor the real parameter folder. The templates are compiled by the Bicep CLI and the compiled
JSON is read. What Azure itself answers is checked on the first deployment (README.md).

On a developer's machine a missing tool skips its tests; in CI a missing tool is an error.
"""

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest

HERE = pathlib.Path(__file__).resolve().parent
TAG = 'a' * 40
RULE = 'owner-while-creating-users'


def tool(name):
    path = shutil.which(name)
    if not path and os.environ.get('CI') == 'true':
        raise RuntimeError(f'{name} is not installed on this runner; in CI these tests may not be skipped.')
    return path


PWSH = tool('pwsh')
BICEP = tool('bicep')

FAKE_AZ = textwrap.dedent('''
    import json, os, sys
    args = sys.argv[1:]
    with open(os.environ['FAKE_AZ_LOG'], 'a', encoding='utf-8') as log:
        log.write(json.dumps(args) + '\\n')
    state = os.environ.get('FAKE_AZ_STATE', 'empty')
    live = json.loads(os.environ.get('FAKE_AZ_LIVE', '{}'))
    url = args[args.index('--url') + 1] if '--url' in args else ''
    def out(value):
        print(json.dumps(value)); sys.exit(0)
    def fail(text):
        sys.stderr.write(text); sys.exit(1)
    if state == 'broken':
        fail('ERROR: the network is down')
    if args[:2] == ['account', 'show']:
        out({'id': '00000000-0000-0000-0000-000000000000'})
    if args[:2] == ['account', 'get-access-token']:
        out({'accessToken': 'not-a-token'})
    if args[:3] == ['ad', 'signed-in-user', 'show']:
        account = {'id': '11111111-1111-1111-1111-111111111111', 'userPrincipalName': 'owner@example.invalid',
                   'mail': None if state == 'no-mailbox' else 'owner.mailbox@example.invalid'}
        out(account)
    if args[:2] == ['resource', 'list']:
        if state == 'resources-unreadable':
            fail('ERROR: the listing timed out')
        environment = [{'name': 'azurebank-env', 'type': 'Microsoft.App/managedEnvironments'}]
        # Azure does not promise the case of a type name: the action group comes back in lower case.
        app = [{'name': 'azurebank', 'type': 'Microsoft.App/containerApps'},
               {'name': 'azurebank-migrate', 'type': 'Microsoft.App/jobs'},
               {'name': 'azurebank-owner', 'type': 'microsoft.insights/actiongroups'}]
        out([] if state in ('empty', 'no-mailbox') else environment if state == 'foundation' else app + environment)
    if 'managedEnvironments/azurebank-env?' in url:
        destination = os.environ.get('FAKE_AZ_DESTINATION', 'azure-monitor')
        logs = None if destination == '(absent)' else {'destination': destination}
        out({'properties': {'appLogsConfiguration': logs}})
    if 'actionGroups/azurebank-owner?' in url:
        out({'properties': {'emailReceivers': [{'name': 'owner', 'emailAddress': 'deployed.alerts@example.invalid'}]}})
    if 'containerApps/azurebank/listSecrets' in url:
        names = ['app-connection', 'jwt-secret', 'idempotency-hash-key', 'stepup-binding-key',
                 'service-key', 'audit-chain-key', 'audit-anchor-key', 'pin-pepper']
        if state == 'key-missing':
            names.remove('pin-pepper')
        out({'value': [{'name': n, 'value': live[n]} for n in names]})
    if 'containerApps/azurebank?' in url:
        out({'properties': {'template': {'containers': [
            {'name': 'bff', 'image': 'ghcr.io/gurgant/azurebank-bff:' + live['tag']},
            {'name': 'api', 'image': 'ghcr.io/gurgant/azurebank-api:' + live['tag']}]}}})
    if args[:3] == ['sql', 'server', 'list']:
        out([{'name': 'azurebank-test', 'fullyQualifiedDomainName': 'azurebank-test.invalid'}])
    if args[:3] == ['sql', 'server', 'firewall-rule']:
        rules_file = os.environ['FAKE_AZ_RULES']
        with open(rules_file, encoding='utf-8') as source:
            rules = json.load(source)
        name = args[args.index('--name') + 1] if '--name' in args else None
        if args[3] == 'list':
            if state == 'list-fails-at-the-end' and len(open(os.environ['FAKE_AZ_LOG']).readlines()) > 3:
                fail('ERROR: the network is down')
            out([{'name': rule} for rule in rules])
        if args[3] == 'delete':
            if state == 'delete-fails':
                fail('ERROR: the delete was refused')
            rules = [rule for rule in rules if rule != name]
        if args[3] == 'create':
            rules.append(name)
        with open(rules_file, 'w', encoding='utf-8') as target:
            json.dump(rules, target)
        if args[3] == 'delete' and state == 'delete-says-it-failed':
            fail('ERROR: the answer was lost')
        out({})
    sys.stderr.write('fake az: unexpected call ' + ' '.join(args)); sys.exit(2)
''')

# sql-principals.ps1 reaches the database through its function Open-Database and nowhere else.
# PowerShell looks a command name up among the aliases before the functions, so the alias this
# harness defines puts a stand-in behind that name, and the script itself runs unchanged.
# STAND_IN_DATABASE says how the stand-in behaves; it logs each opening and the statement it is given.
STAND_IN_DATABASE = textwrap.dedent('''
    param([string]$Script, [string]$ParameterFile)

    class Refusal : System.Exception {
        [int]$Number
        Refusal([int]$number, [string]$message) : base($message) { $this.Number = $number }
    }

    $global:StandInOpenings = 0
    function Open-StandInDatabase([string]$Token) {
        $modes = $env:STAND_IN_DATABASE -split ','
        $global:StandInOpenings++
        Add-Content -LiteralPath $env:STAND_IN_LOG -Value "open with $Token"
        if ($modes -contains 'refused' -and $global:StandInOpenings -eq 1) {
            throw [Refusal]::new(40615, "Cannot open server 'azurebank-test' requested by the login. " +
                "Client with IP address '203.0.113.7' is not allowed to access the server.")
        }
        if ($modes -contains 'login-failed') {
            throw [Refusal]::new(18456, "Login failed for the token. Client with IP address '203.0.113.7'.")
        }
        # The real parameter collection, on a command that is connected to nothing.
        $command = [pscustomobject]@{
            CommandText = ''; CommandTimeout = 0
            Parameters  = [System.Data.SqlClient.SqlCommand]::new().Parameters
        }
        $command | Add-Member -MemberType ScriptMethod -Name ExecuteReader -Value {
            $modes = $env:STAND_IN_DATABASE -split ','
            $bound = [ordered]@{}
            foreach ($parameter in $this.Parameters) {
                $bound[$parameter.ParameterName] = [ordered]@{
                    type = [string]$parameter.SqlDbType; size = $parameter.Size; value = $parameter.Value
                }
            }
            [ordered]@{ text = $this.CommandText; timeout = $this.CommandTimeout; parameters = $bound } |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $env:STAND_IN_STATEMENT
            if ($modes -contains 'statement-fails') { throw 'The statement was refused by the stand-in.' }
            $table = [System.Data.DataTable]::new()
            $null = $table.Columns.Add('user')
            $null = $table.Columns.Add('roles')
            $appRoles = if ($modes -contains 'wrong-roles') { 'db_owner' } else { 'db_datareader, db_datawriter' }
            $null = $table.Rows.Add('azurebank_app', $appRoles)
            $null = $table.Rows.Add('azurebank_migrator', 'db_datareader, db_datawriter, db_ddladmin')
            , $table.CreateDataReader()
        }
        $connection = [pscustomobject]@{ Command = $command }
        $connection | Add-Member -MemberType ScriptMethod -Name CreateCommand -Value { $this.Command }
        $connection | Add-Member -MemberType ScriptMethod -Name Dispose -Value {
            Add-Content -LiteralPath $env:STAND_IN_LOG -Value 'dispose'
        }
        $connection
    }
    Set-Alias -Name Open-Database -Value Open-StandInDatabase

    & $Script -ParameterFile $ParameterFile
''')
ADDRESS = '203.0.113.7'

LIVE = {
    'tag': 'b' * 40,
    'app-connection': 'Server=tcp:x.database.windows.net,1433;Database=AzureBank;Authentication=Active Directory '
                      'Managed Identity;User ID=11111111-aaaa-4bbb-8ccc-000000000001;Encrypt=True',
    'jwt-secret': 'live-jwt-0000000000', 'idempotency-hash-key': 'live-idem-000000000',
    'stepup-binding-key': 'live-stepup-0000000', 'service-key': 'live-service-000000',
    'audit-chain-key': 'live-chain-00000000', 'audit-anchor-key': 'live-anchor-0000000',
    'pin-pepper': 'live-pepper-0000000',
}
# The application secrets the template takes and the script writes. No database credential is among them.
SEVEN = ['jwtSecret', 'idempotencyHashKey', 'stepUpBindingKey', 'serviceCredentialBffKey', 'auditChainKey',
         'auditAnchorKey', 'securityPinPepper']
# Identifiers and names, not secrets: they may appear in a call or in the report's last line.
NOT_SECRET = ('imageTag', 'entraAdminObjectId', 'entraAdminLogin')


class ScriptCase(unittest.TestCase):
    """A temporary folder, a stand-in `az` first on PATH, and a log of every call made to it."""

    def setUp(self):
        self.temp = pathlib.Path(tempfile.mkdtemp(prefix='azurebank-scripts-test-'))
        self.addCleanup(shutil.rmtree, self.temp, ignore_errors=True)
        shim = self.temp / 'shim'
        shim.mkdir()
        (shim / 'fake_az.py').write_text(FAKE_AZ, encoding='utf-8')
        if os.name == 'nt':
            (shim / 'az.cmd').write_text(f'@"{sys.executable}" "%~dp0fake_az.py" %*\r\n', encoding='utf-8')
        else:
            az = shim / 'az'
            az.write_text(f'#!/bin/sh\nexec "{sys.executable}" "$(dirname "$0")/fake_az.py" "$@"\n', encoding='utf-8')
            az.chmod(0o755)
        self.shim = shim
        self.folder = self.temp / 'private'
        self.log = self.temp / 'az.log'
        self.rules = self.temp / 'rules.json'
        self.rules.write_text(json.dumps(['AllowAzureServices']), encoding='utf-8')

    def run_script(self, script, *args, state='empty', **environment):
        env = dict(os.environ, FAKE_AZ_STATE=state, FAKE_AZ_LIVE=json.dumps(LIVE), FAKE_AZ_LOG=str(self.log),
                   FAKE_AZ_RULES=str(self.rules), PATH=str(self.shim) + os.pathsep + os.environ['PATH'])
        env.pop('AZUREBANK_ALERT_EMAIL', None)
        env.update(environment)
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-File', str(HERE / script), *args],
                              capture_output=True, text=True, env=env, timeout=180)

    @staticmethod
    def said(result):
        """Standard error as one line of plain text: PowerShell folds a long error message and may colour it."""
        text = re.sub(r'\x1b\[[0-9;]*m', '', result.stderr)
        return re.sub(r'\s*\n\s*(\|\s*)?', ' ', text)

    def calls(self):
        if not self.log.exists():
            return []
        return [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()]


@unittest.skipUnless(PWSH, 'PowerShell 7 (pwsh) is not installed')
class SecretsScriptTests(ScriptCase):
    def secrets(self, *args, **options):
        return self.run_script('secrets.ps1', *args, '-Directory', str(self.folder), **options)

    def parameters(self):
        document = json.loads((self.folder / 'parameters.json').read_text(encoding='utf-8'))
        return {name: entry['value'] for name, entry in document['parameters'].items()}

    def assert_nothing_leaked(self, result, values):
        self.assertEqual(result.stdout, '', 'standard output must stay empty')
        calls = self.log.read_text(encoding='utf-8') if self.log.exists() else ''
        for name, value in values.items():
            if isinstance(value, str) and len(value) >= 16 and name not in NOT_SECRET:
                self.assertNotIn(value, result.stderr, f'{name} is in the report')
                self.assertNotIn(value, calls, f'{name} went onto an az command line')

    def test_the_foundation_file_holds_who_the_administrator_is_and_no_secret(self):
        result = self.secrets('-Action', 'New')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters(), {'entraAdminObjectId': '11111111-1111-1111-1111-111111111111',
                                             'entraAdminLogin': 'owner@example.invalid', 'deployApp': False})
        self.assertIn("keepLogs: not written, the template's default applies", result.stderr)
        self.assertEqual(result.stdout, '')

    def test_a_foundation_file_never_asks_a_deployed_app_for_its_secrets(self):
        result = self.secrets('-Action', 'New', state='deployed')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call for call in self.calls() if 'listSecrets' in ' '.join(call)], [])
        self.assertEqual(sorted(self.parameters()), ['deployApp', 'entraAdminLogin', 'entraAdminObjectId', 'keepLogs'])

    def test_no_database_password_is_generated_read_or_written(self):
        for state in ('empty', 'deployed'):
            with self.subTest(state=state):
                result = self.secrets('-Action', 'New', '-DeployApp', *(['-ImageTag', TAG] if state == 'empty' else []),
                                      state=state)
                self.assertEqual(result.returncode, 0, result.stderr)
                written = (self.folder / 'parameters.json').read_text(encoding='utf-8')
                for word in ('password', 'sql', 'connection', 'Server='):
                    self.assertNotIn(word.lower(), written.lower())
                # The job holds one secret, its connection string: nothing here asks for it.
                self.assertEqual([call for call in self.calls() if 'jobs/' in ' '.join(call)], [])

    @unittest.skipUnless(os.name == 'nt', 'the access list is the Windows half')
    def test_the_folder_is_open_to_its_owner_only(self):
        self.assertEqual(self.secrets('-Action', 'New').returncode, 0)
        listing = subprocess.run(['icacls', str(self.folder)], capture_output=True, text=True).stdout
        entries = [line for line in listing.splitlines() if ':(' in line]
        self.assertEqual(len(entries), 1, listing)
        self.assertIn('(F)', entries[0])

    @unittest.skipIf(os.name == 'nt', 'the mode bits are the other half')
    def test_the_folder_and_file_modes_are_owner_only(self):
        self.assertEqual(self.secrets('-Action', 'New').returncode, 0)
        self.assertEqual(self.folder.stat().st_mode & 0o777, 0o700)
        self.assertEqual((self.folder / 'parameters.json').stat().st_mode & 0o777, 0o600)

    def test_first_app_file_generates_seven_distinct_values_and_prints_none(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG)
        self.assertEqual(result.returncode, 0, result.stderr)
        values = self.parameters()
        self.assertEqual(sorted(values), sorted(['entraAdminObjectId', 'entraAdminLogin', 'deployApp', 'imageTag',
                                                 'alertEmail', *SEVEN]))
        self.assertEqual(values['imageTag'], TAG)
        self.assertIs(values['deployApp'], True)
        self.assertEqual(len({values[name] for name in SEVEN}), 7)
        for name in SEVEN:
            self.assertGreaterEqual(len(values[name]), 44, f'{name}: at least 32 random bytes')
            self.assertIn(f'{name}: generated', result.stderr)
        self.assertEqual(result.stderr.count(': generated'), 7)
        self.assert_nothing_leaked(result, values)

    def test_the_alerts_write_to_the_accounts_own_mailbox_and_the_report_does_not_show_it(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters()['alertEmail'], 'owner.mailbox@example.invalid')
        self.assertIn("alertEmail: the signed-in account's own mailbox", result.stderr)
        self.assertNotIn('owner.mailbox', result.stderr)

    def test_the_variable_names_another_mailbox_and_the_report_does_not_show_it(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG,
                              AZUREBANK_ALERT_EMAIL='alerts.elsewhere@example.invalid')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters()['alertEmail'], 'alerts.elsewhere@example.invalid')
        self.assertIn('alertEmail: from AZUREBANK_ALERT_EMAIL', result.stderr)
        self.assertNotIn('alerts.elsewhere', result.stderr + self.log.read_text(encoding='utf-8'))

    def test_the_parameter_names_the_mailbox_before_anything_else_does(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-AlertEmail', 'given@example.invalid',
                              state='deployed', AZUREBANK_ALERT_EMAIL='alerts.elsewhere@example.invalid')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters()['alertEmail'], 'given@example.invalid')
        self.assertIn('alertEmail: from -AlertEmail', result.stderr)
        self.assertNotIn('given@', result.stderr + self.log.read_text(encoding='utf-8'))
        self.assertEqual([call for call in self.calls() if 'actionGroups' in ' '.join(call)], [])

    def test_a_mailbox_that_is_not_an_address_is_refused(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, '-AlertEmail', 'not an address')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertEqual(self.calls(), [])

    def test_the_mailbox_the_deployed_alerts_write_to_is_kept_when_none_is_named(self):
        result = self.secrets('-Action', 'New', '-DeployApp', state='deployed')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters()['alertEmail'], 'deployed.alerts@example.invalid')
        self.assertIn('alertEmail: kept from the deployed resource', result.stderr)
        self.assertNotIn('deployed.alerts', result.stderr)

    def test_an_account_without_a_mailbox_and_no_variable_stops_the_run(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, state='no-mailbox')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('-AlertEmail', result.stderr)
        self.assertIn('AZUREBANK_ALERT_EMAIL', result.stderr)
        self.assertFalse((self.folder / 'parameters.json').exists())

    def test_keep_logs_is_what_the_deployed_environment_does_now(self):
        for destination, expected in (('azure-monitor', True), ('none', False), ('(absent)', False)):
            with self.subTest(destination=destination):
                result = self.secrets('-Action', 'New', state='foundation', FAKE_AZ_DESTINATION=destination)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIs(self.parameters()['keepLogs'], expected)
                self.assertIn('keepLogs: kept from the deployed resource', result.stderr)

    def test_logs_off_writes_false_whatever_is_deployed(self):
        for state in ('empty', 'foundation', 'deployed'):
            with self.subTest(state=state):
                self.log.unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-LogsOff', state=state)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIs(self.parameters()['keepLogs'], False)
                self.assertIn('keepLogs: false, asked for with -LogsOff', result.stderr)
                self.assertEqual([call for call in self.calls() if 'managedEnvironments' in ' '.join(call)], [])

    def test_an_environment_that_sends_its_logs_somewhere_else_stops_the_run(self):
        result = self.secrets('-Action', 'New', state='foundation', FAKE_AZ_DESTINATION='log-analytics')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("sends its logs to 'log-analytics'", self.said(result))
        self.assertFalse((self.folder / 'parameters.json').exists())

    def test_a_second_run_keeps_what_the_first_wrote(self):
        self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG)
        first = self.parameters()
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG)
        second = self.parameters()
        self.assertEqual(second, first)
        self.assertEqual(result.stderr.count(': kept from the earlier file'), 7)
        self.assertIn('jwtSecret: kept from the earlier file', result.stderr)
        self.assert_nothing_leaked(result, second)

    def test_a_deployed_app_is_the_authority_and_its_images_are_left_alone(self):
        result = self.secrets('-Action', 'New', '-DeployApp', state='deployed')
        self.assertEqual(result.returncode, 0, result.stderr)
        values = self.parameters()
        self.assertEqual(values['imageTag'], LIVE['tag'])
        self.assertEqual({name: values[name] for name in SEVEN}, {
            'jwtSecret': LIVE['jwt-secret'], 'idempotencyHashKey': LIVE['idempotency-hash-key'],
            'stepUpBindingKey': LIVE['stepup-binding-key'], 'serviceCredentialBffKey': LIVE['service-key'],
            'auditChainKey': LIVE['audit-chain-key'], 'auditAnchorKey': LIVE['audit-anchor-key'],
            'securityPinPepper': LIVE['pin-pepper']})
        self.assertIs(values['keepLogs'], True)
        self.assertNotIn('generated', result.stderr)
        self.assert_nothing_leaked(result, values)

    def test_another_image_tag_is_refused_once_the_app_exists(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, state='deployed')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertEqual(result.stdout, '')

    def test_an_unreadable_azure_is_not_an_absent_secret(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, state='broken')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertEqual(result.stdout, '')

    def test_a_resource_list_that_fails_is_not_an_empty_resource_group(self):
        # Read as "no app yet", it would generate seven new secrets for an app that holds seven.
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, state='resources-unreadable')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('az resource list failed', result.stderr)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertNotIn('generated', result.stderr)

    def test_a_deployed_app_missing_a_key_stops_the_run(self):
        result = self.secrets('-Action', 'New', '-DeployApp', state='key-missing')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertNotIn('generated', result.stderr)

    def test_an_app_file_without_a_tag_is_refused(self):
        result = self.secrets('-Action', 'New', '-DeployApp')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())

    def test_remove_leaves_nothing(self):
        self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG)
        for name in ('what-if.json', 'budget.json'):
            (self.folder / name).write_text('{}', encoding='utf-8')
        result = self.secrets('-Action', 'Remove')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.folder.exists())
        self.assertEqual(result.stdout, '')
        self.assertEqual(self.calls()[-1][:3], ['ad', 'signed-in-user', 'show'], 'Remove asks Azure nothing')

    def test_remove_deletes_its_three_files_by_name_and_nothing_else(self):
        self.folder.mkdir()
        for name in ('parameters.json', 'what-if.json', 'budget.json', 'keep-me.txt'):
            (self.folder / name).write_text('{}', encoding='utf-8')
        (self.folder / 'documents').mkdir()
        (self.folder / 'documents' / 'thesis.txt').write_text('years of work', encoding='utf-8')
        result = self.secrets('-Action', 'Remove')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(sorted(path.name for path in self.folder.iterdir()), ['documents', 'keep-me.txt'])
        self.assertEqual((self.folder / 'documents' / 'thesis.txt').read_text(encoding='utf-8'), 'years of work')
        self.assertIn('was left as it is', result.stderr)
        self.assertEqual(self.calls(), [])

    def test_remove_on_a_folder_that_does_not_exist_changes_nothing(self):
        result = self.secrets('-Action', 'Remove')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.folder.exists())


class UsersCase(ScriptCase):
    PASSWORDS = {'appSqlPassword': 'AppPassword0000000000000000000000000000000000000A',
                 'migratorSqlPassword': 'MigratorPassword00000000000000000000000000000000B'}
    BY_HAND = (f'az sql server firewall-rule delete --resource-group azurebank-demo '
               f'--server azurebank-test --name {RULE}')

    def setUp(self):
        super().setUp()
        self.folder.mkdir()
        self.file = self.folder / 'parameters.json'
        self.file.write_text(json.dumps(
            {'parameters': {name: {'value': value} for name, value in self.PASSWORDS.items()}}), encoding='utf-8')

    def verbs(self):
        return [' '.join(call[:4]) if call[:3] == ['sql', 'server', 'firewall-rule'] else ' '.join(call[:2])
                for call in self.calls()]

    def rules_left(self):
        return json.loads(self.rules.read_text(encoding='utf-8'))

    def assert_no_password_left_the_file(self, result):
        self.assertEqual(result.stdout, '', 'standard output must stay empty')
        for value in self.PASSWORDS.values():
            self.assertNotIn(value, result.stderr + self.log.read_text(encoding='utf-8'))


@unittest.skipUnless(PWSH, 'PowerShell 7 (pwsh) is not installed')
class UsersScriptTests(UsersCase):
    """sql-principals.ps1 up to the connection: the stand-in server name does not resolve, so no
    database is ever reached, and every run ends non-zero for that reason alone. What these prove
    is the order of the calls and what is said, never the exit code of a run that got further."""

    def users(self, **options):
        return self.run_script('sql-principals.ps1', '-ParameterFile', str(self.file), '-ConnectTimeout', '1',
                               **options)

    def test_a_rule_left_by_an_earlier_run_is_deleted_before_anything_else(self):
        self.rules.write_text(json.dumps(['AllowAzureServices', RULE]), encoding='utf-8')
        result = self.users()
        self.assertNotEqual(result.returncode, 0, 'the stand-in server cannot be reached')
        self.assertEqual(self.verbs(), ['sql server', 'sql server firewall-rule list', 'sql server firewall-rule delete',
                                        'account get-access-token', 'sql server firewall-rule list'])
        self.assertEqual(json.loads(self.rules.read_text(encoding='utf-8')), ['AllowAzureServices'])
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)
        self.assert_no_password_left_the_file(result)

    def test_without_a_leftover_nothing_is_deleted_and_the_list_is_read_at_the_end(self):
        result = self.users()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.verbs(), ['sql server', 'sql server firewall-rule list', 'account get-access-token',
                                        'sql server firewall-rule list'])
        self.assertIn('Could not connect to the database', result.stderr)
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)
        self.assert_no_password_left_the_file(result)

    def test_a_leftover_rule_that_cannot_be_deleted_stops_the_run_and_prints_the_command(self):
        self.rules.write_text(json.dumps(['AllowAzureServices', RULE]), encoding='utf-8')
        result = self.users(state='delete-fails')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('it may still be there', result.stderr)
        self.assertIn(self.BY_HAND, result.stderr)
        verbs = self.verbs()
        self.assertNotIn('account get-access-token', verbs, 'nothing else is done with the rule in place')
        self.assertEqual(verbs[-1], 'sql server firewall-rule list', 'the list is still read')
        self.assertIn(f'Firewall rules now: AllowAzureServices, {RULE}.', result.stderr)
        self.assertIn(f'The temporary rule {RULE} is still there.', self.said(result))

    def test_a_rule_list_that_cannot_be_read_back_is_not_taken_for_a_clean_one(self):
        result = self.users(state='list-fails-at-the-end')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('could not be read back', result.stderr)
        self.assertNotIn('Firewall rules now', result.stderr)

    def test_without_a_parameter_file_azure_is_asked_nothing(self):
        self.file.unlink()
        result = self.users()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls(), [])

    def test_the_sql_takes_its_passwords_as_bound_parameters_only(self):
        sql = (HERE / 'sql-principals.sql').read_text(encoding='utf-8')
        runner = (HERE / 'sql-principals.ps1').read_text(encoding='utf-8')
        for name in ('@AppPassword', '@MigratorPassword'):
            self.assertIn(name, sql)
            self.assertIn(f"Parameters.Add('{name}'", runner)
        self.assertNotIn('$(', sql, 'no client-side substitution')
        self.assertIn("DB_NAME() <> N'AzureBank'", sql)


@unittest.skipUnless(PWSH, 'PowerShell 7 (pwsh) is not installed')
class UsersRunTests(UsersCase):
    """sql-principals.ps1 from its first line to its last, with a stand-in where the database is
    (STAND_IN_DATABASE above). These are the runs that get past the connection, so here an exit
    code means something: the life of the temporary firewall rule, and what a failed delete leads
    to. What SQL Server does with the statement is checked on a real server (README.md)."""

    def setUp(self):
        super().setUp()
        self.harness = self.temp / 'with-a-stand-in-database.ps1'
        self.harness.write_text(STAND_IN_DATABASE, encoding='utf-8')
        self.statement = self.temp / 'statement.json'
        self.openings = self.temp / 'database.log'

    def users(self, database, **options):
        return self.run_script(self.harness, '-Script', str(HERE / 'sql-principals.ps1'),
                               '-ParameterFile', str(self.file), STAND_IN_DATABASE=database,
                               STAND_IN_STATEMENT=str(self.statement), STAND_IN_LOG=str(self.openings), **options)

    def opened(self):
        return self.openings.read_text(encoding='utf-8-sig').splitlines()

    def test_a_refused_address_is_allowed_for_the_run_and_the_rule_is_gone_at_the_end(self):
        result = self.users('refused')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.verbs(), ['sql server', 'sql server firewall-rule list', 'account get-access-token',
                                        'sql server firewall-rule create', 'sql server firewall-rule delete',
                                        'sql server firewall-rule list'])
        create = next(call for call in self.calls() if call[:4] == ['sql', 'server', 'firewall-rule', 'create'])
        self.assertEqual([create[create.index(flag) + 1]
                          for flag in ('--name', '--start-ip-address', '--end-ip-address')], [RULE, ADDRESS, ADDRESS])
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertEqual(self.opened(), ['open with not-a-token', 'open with not-a-token', 'dispose'])
        self.assertIn('azurebank_app: db_datareader, db_datawriter', result.stderr)
        self.assertIn('azurebank_migrator: db_datareader, db_datawriter, db_ddladmin', result.stderr)
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)
        self.assertNotIn(ADDRESS, result.stderr, 'the report holds no address')
        self.assert_no_password_left_the_file(result)

    def test_an_address_that_is_already_allowed_adds_no_rule_and_deletes_none(self):
        result = self.users('open')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.verbs(), ['sql server', 'sql server firewall-rule list', 'account get-access-token',
                                        'sql server firewall-rule list'])
        self.assertEqual(self.opened(), ['open with not-a-token', 'dispose'])

    def test_the_statement_is_the_sql_file_with_both_passwords_bound_as_parameters(self):
        result = self.users('open')
        self.assertEqual(result.returncode, 0, result.stderr)
        statement = json.loads(self.statement.read_text(encoding='utf-8-sig'))
        self.assertEqual(statement['text'].replace('\r\n', '\n'),
                         (HERE / 'sql-principals.sql').read_text(encoding='utf-8'))
        self.assertEqual(statement['parameters'], {
            '@AppPassword': {'type': 'NVarChar', 'size': 4000, 'value': self.PASSWORDS['appSqlPassword']},
            '@MigratorPassword': {'type': 'NVarChar', 'size': 4000, 'value': self.PASSWORDS['migratorSqlPassword']}})
        for value in self.PASSWORDS.values():
            self.assertNotIn(value, statement['text'])
        self.assert_no_password_left_the_file(result)

    def test_a_delete_that_fails_at_the_end_exits_non_zero_though_the_users_were_made(self):
        result = self.users('refused', state='delete-fails')
        self.assertNotEqual(result.returncode, 0, 'a rule left behind is never a success')
        self.assertIn('azurebank_migrator: db_datareader, db_datawriter, db_ddladmin', result.stderr)
        self.assertIn('it may still be there', result.stderr)
        self.assertIn(self.BY_HAND, result.stderr)
        self.assertEqual(self.verbs()[-2:], ['sql server firewall-rule delete', 'sql server firewall-rule list'])
        self.assertIn(f'Firewall rules now: AllowAzureServices, {RULE}.', result.stderr)
        self.assertIn(f'The temporary rule {RULE} is still there.', self.said(result))
        self.assertNotIn(ADDRESS, result.stderr)

    def test_a_delete_that_reports_a_failure_exits_non_zero_even_when_the_list_is_clean(self):
        result = self.users('refused', state='delete-says-it-failed')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)
        self.assertIn(self.BY_HAND, result.stderr)
        self.assertIn('run the delete by hand to be sure', self.said(result))

    def test_a_statement_that_fails_still_removes_the_rule(self):
        result = self.users('refused,statement-fails')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('The statement was refused by the stand-in.', self.said(result))
        self.assertEqual(self.verbs()[-2:], ['sql server firewall-rule delete', 'sql server firewall-rule list'])
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertEqual(self.opened()[-1], 'dispose')
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)

    def test_roles_other_than_the_expected_ones_stop_the_run_and_the_rule_is_still_removed(self):
        result = self.users('refused,wrong-roles')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('azurebank_app should hold [db_datareader, db_datawriter] and holds [db_owner].',
                      self.said(result))
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])

    def test_only_the_firewalls_own_refusal_opens_the_firewall_and_any_other_is_reported_without_an_address(self):
        result = self.users('login-failed')
        self.assertNotEqual(result.returncode, 0)
        said = self.said(result)
        self.assertIn('Could not connect to the database', said)
        self.assertIn("Client with IP address '<address>'.", said)
        self.assertNotIn(ADDRESS, result.stderr)
        self.assertNotIn('sql server firewall-rule create', self.verbs())
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])


@unittest.skipUnless(PWSH, 'PowerShell 7 (pwsh) is not installed')
class ParseTests(unittest.TestCase):
    def test_both_scripts_parse_without_an_error(self):
        for script in ('secrets.ps1', 'sql-principals.ps1'):
            with self.subTest(script=script):
                command = ('$errors = $null; $tokens = $null; '
                           '$null = [System.Management.Automation.Language.Parser]::ParseFile($args[0], '
                           '[ref]$tokens, [ref]$errors); '
                           '$errors | ForEach-Object { [Console]::Error.WriteLine($_.ToString()) }; '
                           '"{0} tokens, {1} errors" -f $tokens.Count, $errors.Count')
                result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command',
                                         f'& {{ {command} }} "{HERE / script}"'],
                                        capture_output=True, text=True, timeout=120)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertRegex(result.stdout.strip(), r'^[1-9][0-9]* tokens, 0 errors$', result.stderr)


NINE_ACTIONS = [
    'Microsoft.App/containerApps/read', 'Microsoft.App/containerApps/write',
    'Microsoft.App/containerApps/revisions/read', 'Microsoft.App/containerApps/revisions/replicas/read',
    'Microsoft.App/jobs/read', 'Microsoft.App/jobs/write', 'Microsoft.App/jobs/start/action',
    'Microsoft.App/jobs/executions/read', 'Microsoft.App/jobs/execution/read',
]
BEHIND_DEPLOY_APP = ['Microsoft.App/containerApps', 'Microsoft.App/jobs', 'Microsoft.Authorization/roleAssignments',
                     'Microsoft.Authorization/roleAssignments', 'Microsoft.Insights/actionGroups']
# One loop. Each rule needs deployApp; the one on the log workspace also needs the logs and its own switch.
ALERTS_CONDITION = ("[and(parameters('deployApp'), or(not(variables('alerts')[copyIndex()].onLogs), "
                    "and(parameters('keepLogs'), parameters('logVolumeAlert'))))]")
IDENTITIES = 'Microsoft.ManagedIdentity/userAssignedIdentities'
FOUNDATION = ['Microsoft.App/managedEnvironments', 'Microsoft.Authorization/locks',
              'Microsoft.Authorization/roleDefinitions', IDENTITIES, IDENTITIES, IDENTITIES,
              'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials',
              'Microsoft.Sql/servers', 'Microsoft.Sql/servers/databases', 'Microsoft.Sql/servers/firewallRules']
BEHIND_DENY_POLICY = ['Microsoft.Authorization/policyAssignments', 'Microsoft.Resources/deployments']
BEHIND_KEEP_LOGS = ['Microsoft.Insights/diagnosticSettings', 'Microsoft.OperationalInsights/workspaces']
SECRETS_OF_THE_APP = ['app-connection', 'audit-anchor-key', 'audit-chain-key', 'idempotency-hash-key', 'jwt-secret',
                      'pin-pepper', 'service-key', 'stepup-binding-key']
SERVER = "resourceId('Microsoft.Sql/servers', format('azurebank-{0}', uniqueString(resourceGroup().id)))"
# Everything the Deny policy refuses, as Azure receives it. A rule that is dropped or loosened
# fails the comparison below; so does a new one, until it is written here too.
APP, JOB = 'Microsoft.App/containerApps', 'Microsoft.App/jobs'
REFUSED_ON_AN_APP = [
    {'field': f'{APP}/template.scale.maxReplicas', 'greater': 1},
    {'field': f'{APP}/template.scale.minReplicas', 'greater': 0},
    {'allOf': [{'field': f'{APP}/configuration.activeRevisionsMode', 'exists': True},
               {'field': f'{APP}/configuration.activeRevisionsMode', 'notEquals': 'Single'}]},
    {'field': f'{APP}/configuration.ingress.allowInsecure', 'equals': True},
    {'count': {'field': f'{APP}/template.containers[*]'}, 'greater': 2},
    {'count': {'field': f'{APP}/template.initContainers[*]'}, 'greater': 0},
    {'count': {'field': f'{APP}/template.containers[*]',
               'where': {'field': f'{APP}/template.containers[*].resources.cpu', 'greater': 0.5}}, 'greater': 0},
]
REFUSED_ON_A_JOB = [
    {'allOf': [{'field': f'{JOB}/configuration.triggerType', 'exists': True},
               {'field': f'{JOB}/configuration.triggerType', 'notIn': "[parameters('allowedJobTriggers')]"}]},
    {'field': f'{JOB}/configuration.manualTriggerConfig.parallelism', 'greater': 1},
    {'field': f'{JOB}/configuration.scheduleTriggerConfig.parallelism', 'greater': 1},
    {'field': f'{JOB}/configuration.eventTriggerConfig.parallelism', 'greater': 1},
    {'count': {'field': f'{JOB}/template.initContainers[*]'}, 'greater': 0},
    {'count': {'field': f'{JOB}/template.containers[*]',
               'where': {'field': f'{JOB}/template.containers[*].resources.cpu', 'greater': 0.5}}, 'greater': 0},
]


def connection_of(identity):
    """A connection string as the compiled template builds it: the server, the identity to ask a
    token for, and nothing else. A credential or a limit added to it changes this text."""
    return ("[format('Server=tcp:{0},1433;Database=AzureBank;Authentication=Active Directory Managed Identity;"
            "User ID={1};Encrypt=True;TrustServerCertificate=False', "
            f"reference({SERVER}, '2023-08-01').fullyQualifiedDomainName, "
            f"reference(resourceId('{IDENTITIES}', '{identity}'), '2023-01-31').clientId)]")


def as_azure_receives_it(node, variables):
    """A compiled policy rule with the template's own expressions worked out: the two variables,
    the numbers written with json(), and the "[[" that keeps a policy expression for Azure."""
    if isinstance(node, dict):
        return {key: as_azure_receives_it(value, variables) for key, value in node.items()}
    if isinstance(node, list):
        return [as_azure_receives_it(value, variables) for value in node]
    if not isinstance(node, str):
        return node
    if node.startswith('[['):
        return node[1:]
    worked_out = ((r"\[variables\('(\w+)'\)\]", lambda m: variables[m.group(1)]),
                  (r"\[format\('\{0\}(.*)', variables\('(\w+)'\)\)\]", lambda m: variables[m.group(2)] + m.group(1)),
                  (r"\[json\('([0-9.]+)'\)\]", lambda m: float(m.group(1))))
    for pattern, value in worked_out:
        match = re.fullmatch(pattern, node)
        if match:
            return value(match)
    assert not node.startswith('['), f'an expression this test cannot work out: {node}'
    return node


@unittest.skipUnless(BICEP, 'the Bicep CLI is not installed')
class TemplateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.compiled = {}
        for name in ('main', 'guardrails'):
            for verb in (['build', '--stdout'], ['lint']):
                result = subprocess.run([BICEP, verb[0], str(HERE / f'{name}.bicep'), *verb[1:]],
                                        capture_output=True, text=True, timeout=300)
                # Bicep prints a warning on standard error and still exits 0.
                if result.returncode != 0 or result.stderr.strip():
                    raise AssertionError(f'bicep {verb[0]} {name}.bicep: exit {result.returncode}\n{result.stderr}')
                if verb[0] == 'build':
                    cls.compiled[name] = json.loads(result.stdout)
        cls.main = cls.compiled['main']
        cls.resources = cls.main['resources']
        assert isinstance(cls.resources, list), 'the tests read the compiled resources as a list'

    def of_type(self, kind):
        return [resource for resource in self.resources if resource['type'] == kind]

    def conditions(self, condition):
        return sorted(resource['type'] for resource in self.resources if resource.get('condition') == condition)

    def test_the_app_and_all_that_needs_it_are_behind_deploy_app(self):
        self.assertEqual(self.conditions("[parameters('deployApp')]"), sorted(BEHIND_DEPLOY_APP))

    def test_the_foundation_is_created_whatever_deploy_app_says(self):
        self.assertEqual(sorted(resource['type'] for resource in self.resources if 'condition' not in resource),
                         sorted(FOUNDATION))

    def test_the_policy_can_be_switched_off_and_nothing_else_hangs_on_that_switch(self):
        self.assertEqual(self.conditions("[parameters('denyPolicy')]"), sorted(BEHIND_DENY_POLICY))
        self.assertEqual(self.conditions(ALERTS_CONDITION), ['Microsoft.Insights/metricAlerts'])
        self.assertEqual(len(self.resources), len(BEHIND_DEPLOY_APP) + len(FOUNDATION) + len(BEHIND_DENY_POLICY)
                         + len(BEHIND_KEEP_LOGS) + 1)

    def test_the_logs_go_to_one_capped_workspace_that_takes_no_key(self):
        self.assertEqual(self.conditions("[parameters('keepLogs')]"), sorted(BEHIND_KEEP_LOGS))
        (workspace,) = self.of_type('Microsoft.OperationalInsights/workspaces')
        self.assertEqual(workspace['name'], 'azurebank-logs')
        self.assertEqual(workspace['properties'], {
            'sku': {'name': 'PerGB2018'}, 'retentionInDays': 30,
            'workspaceCapping': {'dailyQuotaGb': "[json(parameters('logDailyCapGb'))]"},
            'features': {'disableLocalAuth': True}})
        # Text: the property is a whole number to Bicep, and a fraction of a GB has to pass through json().
        cap = self.main['parameters']['logDailyCapGb']
        self.assertEqual((cap['type'], cap['defaultValue']), ('string', '0.05'))
        self.assertIs(self.main['parameters']['keepLogs']['defaultValue'], True)
        for key in ('sharedKey', 'listKeys', 'logAnalyticsConfiguration'):
            self.assertNotIn(key, json.dumps(self.main))

    def test_the_environment_sends_console_and_system_logs_there_and_nothing_else(self):
        (environment,) = self.of_type('Microsoft.App/managedEnvironments')
        self.assertEqual(environment['properties']['appLogsConfiguration'],
                         {'destination': "[if(parameters('keepLogs'), 'azure-monitor', 'none')]"})
        (setting,) = self.of_type('Microsoft.Insights/diagnosticSettings')
        self.assertEqual((setting['name'], setting['scope']),
                         ('to-azurebank-logs', "[resourceId('Microsoft.App/managedEnvironments', 'azurebank-env')]"))
        # No ingress (HTTP) category, no category group, no metrics: two categories, by name.
        self.assertEqual(setting['properties'], {
            'workspaceId': "[resourceId('Microsoft.OperationalInsights/workspaces', 'azurebank-logs')]",
            'logs': [{'category': 'ContainerAppConsoleLogs', 'enabled': True},
                     {'category': 'ContainerAppSystemLogs', 'enabled': True}]})

    def test_the_bff_does_not_keep_its_line_per_request(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        (bff,) = [container for container in app['properties']['template']['containers'] if container['name'] == 'bff']
        self.assertIn({'name': 'Serilog__MinimumLevel__Override__Serilog', 'value': 'Warning'}, bff['env'])

    def test_the_role_holds_exactly_the_nine_actions_and_no_data_action(self):
        (role,) = self.of_type('Microsoft.Authorization/roleDefinitions')
        (permissions,) = role['properties']['permissions']
        self.assertEqual(permissions['actions'], NINE_ACTIONS)
        self.assertEqual([permissions[key] for key in ('notActions', 'dataActions', 'notDataActions')], [[], [], []])
        self.assertEqual(role['properties']['assignableScopes'], ['[resourceGroup().id]'])
        for forbidden in ('listSecrets', 'delete', 'stop', '*'):
            self.assertNotIn(forbidden.lower(), ' '.join(permissions['actions']).lower())

    def test_github_may_sign_in_from_the_environment_demo_of_this_repository_and_from_nowhere_else(self):
        self.assertEqual(sorted(identity['name'] for identity in self.of_type(IDENTITIES)),
                         ['azurebank-app', 'azurebank-deploy', 'azurebank-migrate'])
        # One federated credential, on the deployment identity: nothing outside Azure can sign in
        # as either database identity.
        (credential,) = self.of_type('Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials')
        self.assertEqual(credential['name'], "[format('{0}/{1}', 'azurebank-deploy', 'github-demo')]")
        self.assertEqual(credential['properties'], {'issuer': 'https://token.actions.githubusercontent.com',
                                                    'subject': 'repo:Gurgant/azurebank-v2:environment:demo',
                                                    'audiences': ['api://AzureADTokenExchange']})

    def test_the_role_is_assigned_on_the_app_and_on_the_job_and_nowhere_else(self):
        assignments = self.of_type('Microsoft.Authorization/roleAssignments')
        scopes = sorted(resource['scope'] for resource in assignments)
        self.assertEqual(len(scopes), 2)
        self.assertIn("resourceId('Microsoft.App/containerApps'", scopes[0])
        self.assertIn("resourceId('Microsoft.App/jobs', 'azurebank-migrate')", scopes[1])
        # Both go to the deployment identity: the two database identities hold no role on anything in Azure.
        self.assertEqual({resource['properties']['principalId'] for resource in assignments},
                         {f"[reference(resourceId('{IDENTITIES}', 'azurebank-deploy'), '2023-01-31').principalId]"})

    def test_the_lock_is_on_the_database_not_on_the_server(self):
        (lock,) = self.of_type('Microsoft.Authorization/locks')
        self.assertIn("resourceId('Microsoft.Sql/servers/databases'", lock['scope'])
        self.assertEqual(lock['properties']['level'], 'CanNotDelete')

    def test_the_server_takes_entra_sign_ins_only_and_has_no_sql_administrator(self):
        (server,) = self.of_type('Microsoft.Sql/servers')
        self.assertEqual(server['properties'].get('administrators'), {
            'administratorType': 'ActiveDirectory', 'principalType': 'User',
            'login': "[parameters('entraAdminLogin')]", 'sid': "[parameters('entraAdminObjectId')]",
            'tenantId': '[subscription().tenantId]', 'azureADOnlyAuthentication': True})
        self.assertEqual(sorted(server['properties']),
                         ['administrators', 'minimalTlsVersion', 'publicNetworkAccess', 'version'])
        # Nothing else on the server sets its administrator or its sign-in mode a second time.
        self.assertEqual(sorted(resource['type'] for resource in self.resources
                                if resource['type'].startswith('Microsoft.Sql/servers/')),
                         ['Microsoft.Sql/servers/databases', 'Microsoft.Sql/servers/firewallRules'])

    def test_no_database_credential_is_anywhere_in_the_template(self):
        # The words themselves, descriptions included: a search of the compiled template for
        # "password" finds nothing, so a hit is always something to look at.
        text = json.dumps(self.main).lower()
        for word in ('password', 'pwd', 'administratorlogin'):
            self.assertNotIn(word, text)

    def test_the_app_and_the_job_each_carry_their_own_database_identity_and_no_other(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        (job,) = self.of_type('Microsoft.App/jobs')
        for resource, name in ((app, 'azurebank-app'), (job, 'azurebank-migrate')):
            self.assertEqual(resource.get('identity'), {'type': 'UserAssigned', 'userAssignedIdentities': {
                f"[format('{{0}}', resourceId('{IDENTITIES}', '{name}'))]": {}}}, resource['type'])

    def test_each_connection_string_names_its_own_identity_and_holds_no_credential(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        (job,) = self.of_type('Microsoft.App/jobs')
        for resource, secret, identity in ((app, 'app-connection', 'azurebank-app'),
                                           (job, 'migration-connection', 'azurebank-migrate')):
            (value,) = [entry['value'] for entry in resource['properties']['configuration']['secrets']
                        if entry['name'] == secret]
            self.assertEqual(value, connection_of(identity), secret)

    def test_seven_parameters_are_secure_and_only_two_must_be_given(self):
        parameters = self.main['parameters']
        secure = sorted(name for name, entry in parameters.items() if entry['type'].lower() == 'securestring')
        self.assertEqual(secure, sorted(SEVEN))
        required = sorted(name for name, entry in parameters.items() if 'defaultValue' not in entry)
        self.assertEqual(required, ['entraAdminLogin', 'entraAdminObjectId'])
        self.assertEqual((parameters['replicaTimeout']['minValue'], parameters['replicaTimeout']['maxValue']),
                         (60, 840))
        self.assertIs(parameters['deployApp']['defaultValue'], False)

    def test_no_output_is_a_secret(self):
        self.assertEqual({name: entry['type'] for name, entry in self.main['outputs'].items()},
                         dict.fromkeys(['sqlServerFqdn', 'sqlServerName', 'deploymentClientId',
                                        'deploymentPrincipalId', 'appIdentityClientId', 'appIdentityPrincipalId',
                                        'migrateIdentityClientId', 'migrateIdentityPrincipalId',
                                        'logWorkspaceCustomerId', 'appUrl'], 'string'))

    def test_every_secret_reaches_a_container_by_reference_only(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        (job,) = self.of_type('Microsoft.App/jobs')
        self.assertEqual(len(app['properties']['configuration']['secrets']), 8)
        self.assertEqual(len(job['properties']['configuration']['secrets']), 1)
        text = json.dumps([app['properties']['template'], job['properties']['template']])
        self.assertEqual(text.count('"secretRef"'), 10)
        for name in SEVEN:
            self.assertNotIn(f"parameters('{name}')", text, f'{name} is a plain value in a container')

    def test_only_the_api_container_and_the_job_are_handed_a_connection_string(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        (job,) = self.of_type('Microsoft.App/jobs')
        handed = {container['name']: sorted(entry['secretRef'] for entry in container['env'] if 'secretRef' in entry)
                  for container in app['properties']['template']['containers']}
        # The bff faces the internet and can use the app's identity like any container of the app:
        # it is told neither which identity nor which server.
        self.assertEqual(handed, {'bff': ['service-key'], 'api': SECRETS_OF_THE_APP})
        (migrate,) = job['properties']['template']['containers']
        self.assertEqual([entry.get('secretRef') for entry in migrate['env']], ['migration-connection'])
        # Nor does either reach a container as a plain value: nothing in a container is read from another resource.
        self.assertNotIn('reference(', json.dumps([app['properties']['template'], job['properties']['template']]))

    def test_the_app_keeps_the_shape_the_deploy_script_and_the_policy_expect(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        configuration, template = app['properties']['configuration'], app['properties']['template']
        self.assertEqual(configuration['activeRevisionsMode'], 'Single')
        self.assertEqual({key: configuration['ingress'][key] for key in ('external', 'targetPort', 'allowInsecure')},
                         {'external': True, 'targetPort': 8080, 'allowInsecure': False})
        self.assertNotIn('additionalPortMappings', configuration['ingress'])
        self.assertEqual(template['scale'], {'minReplicas': 0, 'maxReplicas': 1})
        self.assertEqual([(container['name'], container['resources']) for container in template['containers']],
                         [('bff', {'cpu': "[json('0.25')]", 'memory': '0.5Gi'}),
                          ('api', {'cpu': "[json('0.5')]", 'memory': '1Gi'})])
        self.assertNotIn('initContainers', template)
        (job,) = self.of_type('Microsoft.App/jobs')
        configuration = job['properties']['configuration']
        self.assertEqual((configuration['triggerType'], configuration['replicaRetryLimit'],
                          configuration['manualTriggerConfig']['parallelism']), ('Manual', 0, 1))
        (migrate,) = job['properties']['template']['containers']
        self.assertEqual((migrate['name'], migrate['args'], migrate['resources']),
                         ('migrate', ['migrate'], {'cpu': "[json('0.25')]", 'memory': '0.5Gi'}))
        self.assertNotIn('initContainers', job['properties']['template'])

    def test_the_policy_definition_sits_at_subscription_scope_and_denies(self):
        (module,) = self.of_type('Microsoft.Resources/deployments')
        self.assertEqual(module['subscriptionId'], '[subscription().subscriptionId]')
        nested = module['properties']['template']
        self.assertTrue(nested['$schema'].endswith('/subscriptionDeploymentTemplate.json#'), nested['$schema'])
        self.assertEqual(nested['resources'], self.compiled['guardrails']['resources'])
        (definition,) = nested['resources']
        self.assertEqual(definition['type'], 'Microsoft.Authorization/policyDefinitions')
        self.assertEqual((definition['properties']['policyType'], definition['properties']['mode']), ('Custom', 'All'))
        rule = definition['properties']['policyRule']
        self.assertEqual(rule['then'], {'effect': 'deny'})
        # A policy expression must reach Azure unevaluated: in a nested template it is written "[[".
        self.assertIn("[[parameters('allowedJobTriggers')]", json.dumps(rule))
        (assignment,) = self.of_type('Microsoft.Authorization/policyAssignments')
        self.assertEqual(assignment['name'], 'azurebank-shape')
        self.assertEqual(assignment['properties']['enforcementMode'], 'Default')

    def test_the_policy_refuses_exactly_these_shapes(self):
        definition = self.compiled['guardrails']['resources'][0]['properties']
        rule = as_azure_receives_it(definition['policyRule']['if'], self.compiled['guardrails']['variables'])
        self.assertEqual(rule, {'anyOf': [
            {'allOf': [{'field': 'type', 'equals': APP}, {'anyOf': REFUSED_ON_AN_APP}]},
            {'allOf': [{'field': 'type', 'equals': JOB}, {'anyOf': REFUSED_ON_A_JOB}]}]})

    def test_a_job_may_only_be_started_by_hand_unless_the_template_is_told_otherwise(self):
        definition = self.compiled['guardrails']['resources'][0]['properties']
        self.assertEqual(definition['parameters'],
                         {'allowedJobTriggers': {'type': 'Array', 'defaultValue': ['Manual']}})
        self.assertEqual(self.main['parameters']['allowedJobTriggers']['defaultValue'], ['Manual'])
        (assignment,) = self.of_type('Microsoft.Authorization/policyAssignments')
        self.assertEqual(assignment['properties']['parameters'],
                         {'allowedJobTriggers': {'value': "[parameters('allowedJobTriggers')]"}})

    def test_four_alerts_notify_one_action_group_and_stop_nothing(self):
        (alerts,) = self.of_type('Microsoft.Insights/metricAlerts')
        self.assertEqual(alerts['copy']['count'], "[length(variables('alerts'))]")
        rules = self.main['variables']['alerts']
        self.assertEqual([(rule['metric'], rule['aggregation'], rule['window']) for rule in rules],
                         [('Requests', 'Total', 'PT1H'), ('TxBytes', 'Total', 'P1D'), ('Replicas', 'Average', 'P1D'),
                          ('Ingestion Volume', 'Count', 'PT1H')])
        self.assertEqual(alerts['properties']['actions'],
                         [{'actionGroupId': "[resourceId('Microsoft.Insights/actionGroups', 'azurebank-owner')]"}])
        (group,) = self.of_type('Microsoft.Insights/actionGroups')
        self.assertEqual(list(group['properties']), ['groupShortName', 'enabled', 'emailReceivers'])
        # The mailbox is a parameter: no address is written in the template.
        self.assertEqual([receiver['emailAddress'] for receiver in group['properties']['emailReceivers']],
                         ["[parameters('alertEmail')]"])
        self.assertEqual(re.findall(r'[\w.+-]+@[\w-]+\.\w+', json.dumps(self.main)), [])

    def test_the_alert_on_the_log_volume_watches_the_workspace_and_has_a_switch_of_its_own(self):
        (alerts,) = self.of_type('Microsoft.Insights/metricAlerts')
        rules = self.main['variables']['alerts']
        self.assertEqual([rule['onLogs'] for rule in rules], [False, False, False, True])
        self.assertEqual({key: rules[3][key] for key in ('name', 'threshold', 'every')},
                         {'name': 'azurebank-log-volume', 'threshold': 50000, 'every': 'PT15M'})
        self.assertEqual(alerts['condition'], ALERTS_CONDITION)
        self.assertIs(self.main['parameters']['logVolumeAlert']['defaultValue'], True)
        workspace = "resourceId('Microsoft.OperationalInsights/workspaces', 'azurebank-logs')"
        app = "resourceId('Microsoft.App/containerApps', variables('appName'))"
        self.assertEqual(alerts['properties']['scopes'],
                         [f"[if(variables('alerts')[copyIndex()].onLogs, {workspace}, {app})]"])
        (criterion,) = alerts['properties']['criteria']['allOf']
        self.assertEqual(criterion['metricNamespace'],
                         "[if(variables('alerts')[copyIndex()].onLogs, 'Microsoft.OperationalInsights/workspaces', "
                         "'Microsoft.App/containerApps')]")

    @unittest.skipUnless(PWSH, 'PowerShell 7 (pwsh) is not installed')
    def test_every_name_the_secrets_script_writes_is_a_parameter_and_none_is_missing(self):
        case = SecretsScriptTests('parameters')
        case.setUp()
        try:
            required = {name for name, entry in self.main['parameters'].items() if 'defaultValue' not in entry}
            # With an environment deployed, so that keepLogs is written too.
            self.assertEqual(case.secrets('-Action', 'New', state='foundation').returncode, 0)
            foundation = set(case.parameters())
            self.assertLessEqual(foundation, set(self.main['parameters']))
            self.assertLessEqual(required | {'keepLogs'}, foundation,
                                 'the foundation file must give every required parameter')
            self.assertEqual(case.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG,
                                          state='foundation').returncode, 0)
            written = set(case.parameters())
            self.assertLessEqual(written, set(self.main['parameters']))
            # What the template's own guard asks for when deployApp is true.
            self.assertLessEqual(set(SEVEN) | {'imageTag', 'alertEmail', 'deployApp'}, written)
            # And everything secure in the template is something the script writes: no secret is typed by hand.
            secure = {name for name, entry in self.main['parameters'].items()
                      if entry['type'].lower() == 'securestring'}
            self.assertLessEqual(secure, written)
        finally:
            case.doCleanups()


if __name__ == '__main__':
    unittest.main()
