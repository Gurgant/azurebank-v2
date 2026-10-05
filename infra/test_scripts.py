"""Offline tests of the two PowerShell scripts, of the users file and of the three templates.

The scripts run for real, in PowerShell 7, against a stand-in for the Azure CLI and a stand-in for
sqlcmd: they touch neither Azure, nor a database, nor the real parameter folder. The users file is
read as text: what a SQL Server does with it is checked on a real engine (README.md). The
templates are compiled by the Bicep CLI and the compiled JSON is read; the CLI's `snapshot` also
works them out offline with given values, as a what-if does. What Azure itself answers is
checked on the first deployment (README.md).

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

# The four IDs of the two database identities, as `az identity show` gives them.
IDS = {'azurebank-app': {'clientId': '11111111-aaaa-4bbb-8ccc-000000000001',
                         'principalId': '22222222-aaaa-4bbb-8ccc-000000000002'},
       'azurebank-migrate': {'clientId': '33333333-aaaa-4bbb-8ccc-000000000003',
                             'principalId': '44444444-aaaa-4bbb-8ccc-000000000004'}}

FAKE_AZ = textwrap.dedent('''
    import json, os, sys
    args = sys.argv[1:]
    with open(os.environ['FAKE_AZ_LOG'], 'a', encoding='utf-8') as log:
        log.write(json.dumps(args) + '\\n')
    with open(os.environ['FAKE_SEQUENCE'], 'a', encoding='utf-8') as sequence:
        verb = args[:4] if args[:3] == ['sql', 'server', 'firewall-rule'] else args[:2]
        handed = ' (with a value in SQLCMDPASSWORD)' if 'SQLCMDPASSWORD' in os.environ else ''
        sequence.write('az ' + ' '.join(verb) + handed + '\\n')
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
    if args[:2] == ['identity', 'show']:
        out(json.loads(os.environ['FAKE_AZ_IDS'])[args[args.index('--name') + 1]])
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

# What stands where sqlcmd is. FAKE_SQLCMD says how it behaves, as words separated by commas:
#   odbc, old, no-azcli         what --version and -? answer
#   refused:N                   the first N runs are refused by the server's firewall
#   numbered                    that refusal comes with its error number, as an error in a batch does
#   login-failed, tampered, silent, wrong-roles, fails-after-the-refusals
#                               what a run that reaches the database does instead of succeeding
#   entra-only, let-in          what the one SQL sign-in of -ProveSqlSignInRefused is answered
# It logs every start: its arguments, and which of the names sqlcmd would read are in its environment.
# FIREWALL has the form go-sqlcmd 1.10.0 gives an error at sign-in: "mssql: login error: " and the
# server's sentence, printed twice, with no error number (measured with a refused login on a local
# server). That the server words this refusal so is seen on the first deployment (README.md).
ADDRESS = '203.0.113.7'
FIREWALL = ("mssql: login error: Cannot open server 'azurebank-test' requested by the login. Client with IP "
            f"address '{ADDRESS}' is not allowed to access the server. To enable access, use the Azure "
            "Management Portal or run sp_set_firewall_rule on the master database to create a firewall rule "
            "for this IP address or address range. It may take up to five minutes for this change to take effect.")
VARIABLES = ['AppClientId', 'AppObjectId', 'MigratorClientId', 'MigratorObjectId', 'CreateForm']
BOTH_USERS = ['azurebank_app: db_datareader, db_datawriter; ID as asked: 1',
              'azurebank_migrator: db_datareader, db_datawriter, db_ddladmin; ID as asked: 1']
CONSTANTS = {'VARIABLES': VARIABLES, 'ADDRESS': ADDRESS, 'FIREWALL': FIREWALL, 'BOTH_USERS': BOTH_USERS}
FAKE_SQLCMD = ''.join(f'{name} = {value!r}\n' for name, value in CONSTANTS.items()) + textwrap.dedent('''
    import json, os, sys
    args = sys.argv[1:]
    modes = os.environ.get('FAKE_SQLCMD', '').split(',')
    log = os.environ['FAKE_SQLCMD_LOG']
    earlier = [json.loads(line) for line in open(log, encoding='utf-8')] if os.path.exists(log) else []
    def record(kind):
        entry = {'kind': kind, 'args': args,
                 'variables': {name: os.environ.get(name) for name in VARIABLES + ['SQLCMDUSER', 'SQLCMDINI']},
                 'a value is in SQLCMDPASSWORD': 'SQLCMDPASSWORD' in os.environ}
        with open(log, 'a', encoding='utf-8') as target:
            target.write(json.dumps(entry) + '\\n')
        with open(os.environ['FAKE_SEQUENCE'], 'a', encoding='utf-8') as sequence:
            sequence.write('sqlcmd ' + kind + '\\n')
    def end(code, *lines):
        print('\\n'.join(lines)); sys.exit(code)
    if args == ['--version']:
        record('version')
        if 'odbc' in modes:
            end(1, "Sqlcmd: Error: '-' or '/' does not have an associated argument.", "Enter '-?' for help.")
        end(0, 'sqlcmd: Install/Create/Query SQL Server, Azure SQL, and Tools', '',
            'Version: v' + ('1.8.0' if 'old' in modes else '1.10.0'), '')
    if args == ['-?']:
        record('help')
        if 'odbc' in modes:
            end(0, 'Microsoft (R) SQL Server Command Line Tool', 'usage: Sqlcmd            [-U login id]',
                '  [-X[1] disable commands, startup script, environment variables [and exit]]',
                '  [-G use Azure Active Directory for authentication]')
        methods = ['ActiveDirectoryDefault', 'ActiveDirectoryIntegrated', 'ActiveDirectoryManagedIdentity',
                   'ActiveDirectoryAzCli', 'SqlPassword']
        if 'no-azcli' in modes:
            methods.remove('ActiveDirectoryAzCli')
        end(0, '   --authentication-method', '   One of: ' + ', '.join(methods[:2]) + ', ',
            '   ' + ', '.join(methods[2:]) + ' ', '-b,--exit-on-error')
    if '-Q' in args:
        record('sign-in')
        name = args[args.index('-U') + 1]
        if 'let-in' in modes:
            end(0, '1')
        reason = ' Reason: Azure Active Directory only authentication is enabled.' if 'entra-only' in modes else ''
        end(1, f"mssql: login error: Login failed for user '{name}'.{reason} Client with IP address '{ADDRESS}'.")
    record('run')
    runs = len([entry for entry in earlier if entry['kind'] == 'run'])
    refusals = ([int(mode.split(':')[1]) for mode in modes if mode.startswith('refused:')] or [0])[0]
    if runs < refusals:
        # FAKE_SQLCMD_NAMES: what the refusal names in place of an address.
        refusal = FIREWALL.replace(ADDRESS, os.environ.get('FAKE_SQLCMD_NAMES', ADDRESS))
        if 'numbered' in modes:
            end(1, 'Msg 40615, Level 14, State 1, Server azurebank-test, Line 1', refusal[len('mssql: login error: '):])
        end(1, refusal, refusal)
    if 'login-failed' in modes:
        end(1, "mssql: login error: Login failed for user '<token-identified principal>'. "
               f"Client with IP address '{ADDRESS}'.")
    if 'tampered' in modes:
        end(1, 'Code found: [planted]', 'Msg 50003, Level 16, State 1, Server azurebank-test, Line 65',
            'Code found in the database (a trigger or a module). Nothing was run. Treat the database as tampered with.')
    if 'fails-after-the-refusals' in modes:
        end(1, 'Unknown user: [someone] SQL_USER', 'Msg 50004, Level 16, State 1, Server azurebank-test, Line 196',
            'Not committed: unknown user; ')
    if 'silent' in modes:
        end(0)
    if 'wrong-roles' in modes:
        end(0, 'azurebank_app: db_datareader, db_datawriter, db_owner; ID as asked: 1', BOTH_USERS[1])
    end(0, *BOTH_USERS)
''')

# sql-principals.ps1 reads the tool's signature through its function Get-ToolSignature and nowhere
# else. PowerShell looks a command name up among the aliases before the functions, so the alias
# this harness defines puts a stand-in behind that name, and the script itself runs unchanged.
# STAND_IN_SIGNATURE says what the stand-in answers; the script's arguments come as JSON, so that
# they reach it by name.
STAND_IN_SIGNATURE = textwrap.dedent('''
    param([string]$Script)

    function Get-StandInSignature([string]$Path) {
        Add-Content -LiteralPath $env:STAND_IN_LOG -Value $Path
        # The subject of the certificate that signs go-sqlcmd 1.10.0, as read on 2026-10-02.
        $microsoft = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        switch ($env:STAND_IN_SIGNATURE) {
            'unsigned' { @{ Status = 'NotSigned'; Subject = '' } }
            'altered' { @{ Status = 'HashMismatch'; Subject = $microsoft } }
            'someone-else' { @{ Status = 'Valid'; Subject = 'CN=Microsoft Corporation Tools, O=Someone Else, C=US' } }
            'not-checked' { $null }
            default { @{ Status = 'Valid'; Subject = $microsoft } }
        }
    }
    Set-Alias -Name Get-ToolSignature -Value Get-StandInSignature

    $named = $env:STAND_IN_ARGUMENTS | ConvertFrom-Json -AsHashtable
    & $Script @named
''')

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
        self.shim = self.stand_in('shim', 'az', FAKE_AZ).parent
        self.folder = self.temp / 'private'
        self.log = self.temp / 'az.log'
        self.sequence = self.temp / 'sequence.log'
        self.rules = self.temp / 'rules.json'
        self.rules.write_text(json.dumps(['AllowAzureServices']), encoding='utf-8')

    def stand_in(self, folder, name, source):
        """A program of that name in that folder, which runs `source` in Python. Returns its path."""
        directory = self.temp / folder
        directory.mkdir(exist_ok=True)
        (directory / f'fake_{name}.py').write_text(source, encoding='utf-8')
        if os.name == 'nt':
            program = directory / f'{name}.cmd'
            program.write_text(f'@"{sys.executable}" "%~dp0fake_{name}.py" %*\r\n', encoding='utf-8')
        else:
            program = directory / name
            program.write_text(f'#!/bin/sh\nexec "{sys.executable}" "$(dirname "$0")/fake_{name}.py" "$@"\n',
                               encoding='utf-8')
            program.chmod(0o755)
        return program

    def run_script(self, script, *args, state='empty', **environment):
        env = dict(os.environ, FAKE_AZ_STATE=state, FAKE_AZ_LIVE=json.dumps(LIVE), FAKE_AZ_LOG=str(self.log),
                   FAKE_AZ_RULES=str(self.rules), FAKE_AZ_IDS=json.dumps(IDS), FAKE_SEQUENCE=str(self.sequence),
                   PATH=str(self.shim) + os.pathsep + os.environ['PATH'])
        for name in ('AZUREBANK_ALERT_EMAIL', 'SQLCMDUSER', 'SQLCMDPASSWORD', 'SQLCMDINI', *VARIABLES):
            env.pop(name, None)
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
                for word in ('password', 'connection', 'Server='):
                    self.assertNotIn(word.lower(), written.lower())
                # Three letters can occur by chance in a random key, and did ("...ulsqlw="): "sql"
                # is looked for in every name and in every value but the seven generated ones.
                parameters = json.loads(written)['parameters']
                self.assertNotIn('sql', json.dumps({name: None if name in SEVEN else entry
                                                    for name, entry in parameters.items()}).lower())
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
                # Read with the API version the template creates the environment with.
                read = [call for call in self.calls() if 'managedEnvironments' in ' '.join(call)][-1]
                self.assertTrue(read[read.index('--url') + 1].endswith(
                    '/providers/Microsoft.App/managedEnvironments/azurebank-env?api-version=2026-07-01'), read)

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
    """sql-principals.ps1 with a stand-in for sqlcmd at a path of its own, outside PATH."""

    BY_HAND = (f'az sql server firewall-rule delete --resource-group azurebank-demo '
               f'--server azurebank-test --name {RULE}')
    SWEEP = ['sql server', 'sql server firewall-rule list']
    READS = ['identity show', 'identity show']
    LAST = ['sql server firewall-rule list']

    def setUp(self):
        super().setUp()
        self.tool = self.stand_in('tool', 'sqlcmd', FAKE_SQLCMD)
        self.tool_log = self.temp / 'sqlcmd.log'
        self.harness = self.temp / 'with-a-stand-in-signature.ps1'
        self.harness.write_text(STAND_IN_SIGNATURE, encoding='utf-8')
        self.signature_log = self.temp / 'signature.log'

    def users(self, tool='', signature='microsoft', state='empty', environment=None, **arguments):
        """Run the script through the harness. Keyword arguments are the script's own parameters."""
        named = {'SqlcmdPath': str(self.tool), 'RetrySeconds': 1, **arguments}
        return self.run_script(self.harness, '-Script', str(HERE / 'sql-principals.ps1'), state=state,
                               STAND_IN_ARGUMENTS=json.dumps(named), STAND_IN_SIGNATURE=signature,
                               STAND_IN_LOG=str(self.signature_log), FAKE_SQLCMD=tool,
                               FAKE_SQLCMD_LOG=str(self.tool_log), **(environment or {}))

    def verbs(self):
        return [' '.join(call[:4]) if call[:3] == ['sql', 'server', 'firewall-rule'] else ' '.join(call[:2])
                for call in self.calls()]

    def started(self, kind=None):
        """Every start of the stand-in sqlcmd, or those of one kind: version, help, run, sign-in."""
        if not self.tool_log.exists():
            return []
        entries = [json.loads(line) for line in self.tool_log.read_text(encoding='utf-8').splitlines()]
        return [entry for entry in entries if kind in (None, entry['kind'])]

    def in_order(self):
        return self.sequence.read_text(encoding='utf-8').splitlines() if self.sequence.exists() else []

    def rules_left(self):
        return json.loads(self.rules.read_text(encoding='utf-8'))

    def assert_no_id_or_address_was_said(self, result):
        self.assertEqual(result.stdout, '', 'standard output must stay empty')
        for value in [ADDRESS, *(value for identity in IDS.values() for value in identity.values())]:
            self.assertNotIn(value, result.stderr)


@unittest.skipUnless(PWSH, 'PowerShell 7 (pwsh) is not installed')
class UsersToolTests(UsersCase):
    """What sql-principals.ps1 asks of the tool before it asks anything of Azure."""

    def assert_refused_before_azure(self, result, *said):
        self.assertNotEqual(result.returncode, 0)
        for text in said:
            self.assertIn(text, self.said(result))
        self.assertEqual(self.calls(), [], 'Azure is asked nothing')
        self.assertEqual(self.started('run'), [])
        self.assertEqual(result.stdout, '')

    def test_the_tool_is_started_by_the_path_it_is_given_and_never_through_path(self):
        # A sqlcmd that PATH would find first. It must never be started.
        elsewhere = self.temp / 'on-path.log'
        self.stand_in('shim', 'sqlcmd', f'open({str(elsewhere)!r}, "a").write("started\\n")\n')
        result = self.users()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(elsewhere.exists(), 'the sqlcmd on PATH was started')
        self.assertEqual([entry['kind'] for entry in self.started()], ['help', 'version', 'run'])
        self.assertEqual(self.signature_log.read_text(encoding='utf-8-sig').splitlines(), [str(self.tool)])
        script = (HERE / 'sql-principals.ps1').read_text(encoding='utf-8')
        self.assertIn(r"[string]$SqlcmdPath = 'C:\Program Files\sqlcmd\sqlcmd.exe'", script)

    def test_a_path_with_no_program_stops_the_run_before_azure_is_asked_anything(self):
        result = self.users(SqlcmdPath=str(self.temp / 'nothing-here' / 'sqlcmd.exe'))
        self.assert_refused_before_azure(result, 'There is no sqlcmd at', 'winget install --id Microsoft.Sqlcmd -e')
        self.assertEqual(self.started(), [])

    def test_the_odbc_sqlcmd_is_refused_as_the_tool(self):
        result = self.users(tool='odbc')
        self.assert_refused_before_azure(result, 'is not go-sqlcmd 1.10.0 or later', 'the older ODBC sqlcmd')

    def test_a_go_sqlcmd_older_than_1_10_is_refused(self):
        result = self.users(tool='old')
        self.assert_refused_before_azure(result, 'is not go-sqlcmd 1.10.0 or later')

    def test_a_tool_whose_help_does_not_name_the_method_is_refused_and_the_other_method_is_offered(self):
        result = self.users(tool='no-azcli')
        self.assert_refused_before_azure(result, 'does not name ActiveDirectoryAzCli',
                                         '-AuthenticationMethod ActiveDirectoryDefault')
        result = self.users(tool='no-azcli', AuthenticationMethod='ActiveDirectoryDefault')
        self.assertEqual(result.returncode, 0, result.stderr)
        (run,) = self.started('run')
        self.assertEqual(run['args'][run['args'].index('--authentication-method') + 1], 'ActiveDirectoryDefault')

    def test_a_tool_that_microsoft_did_not_sign_is_refused(self):
        for signature, said in (('unsigned', 'status NotSigned, signer not Microsoft'),
                                ('altered', 'status HashMismatch, signer Microsoft'),
                                ('someone-else', 'status Valid, signer not Microsoft')):
            with self.subTest(signature=signature):
                result = self.users(signature=signature)
                self.assert_refused_before_azure(result, f'is not a valid Microsoft one ({said})')
                self.assertEqual(self.started(), [], 'a program that is not trusted is not even asked its version')

    def test_where_a_signature_cannot_be_checked_the_run_says_so_and_goes_on(self):
        result = self.users(signature='not-checked')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('is not checked on this system', result.stderr)

    @unittest.skipUnless(os.name == 'nt', 'Authenticode is the Windows half')
    def test_on_windows_the_real_check_refuses_a_program_that_is_not_signed(self):
        result = self.run_script('sql-principals.ps1', '-SqlcmdPath', str(self.tool),
                                 FAKE_SQLCMD_LOG=str(self.tool_log))
        self.assert_refused_before_azure(result)
        # Measured on Windows 11: a .cmd file reads UnknownError, a program without a signature NotSigned.
        self.assertRegex(self.said(result),
                         r'is not a valid Microsoft one \(status (UnknownError|NotSigned), signer not Microsoft\)')
        self.assertEqual(self.started(), [])

    @unittest.skipIf(os.name == 'nt', 'elsewhere there is no Authenticode to read')
    def test_off_windows_the_real_check_says_it_checked_nothing(self):
        result = self.run_script('sql-principals.ps1', '-SqlcmdPath', str(self.tool),
                                 FAKE_SQLCMD_LOG=str(self.tool_log))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('is not checked on this system', result.stderr)

    def test_there_is_no_switch_for_the_object_id_and_asking_for_one_runs_nothing(self):
        # A user carries its identity's client ID and nothing else (README.md, "Measured on Azure").
        # Started as an operator starts it: PowerShell refuses the name before the script's first line.
        result = self.run_script('sql-principals.ps1', '-SqlcmdPath', str(self.tool), '-IdKind', 'ObjectId',
                                 FAKE_SQLCMD_LOG=str(self.tool_log))
        self.assert_refused_before_azure(result, "A parameter cannot be found that matches parameter name 'IdKind'")
        self.assertEqual(self.started(), [])
        script = (HERE / 'sql-principals.ps1').read_text(encoding='utf-8')
        self.assertNotIn('IdKind', script)
        self.assertNotIn('IdKind', (HERE / 'sql-principals.sql').read_text(encoding='utf-8'))

    def test_the_odbc_road_is_taken_only_when_asked_and_runs_with_x1(self):
        result = self.users(tool='odbc', OdbcSignInName='owner@example.invalid')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([entry['kind'] for entry in self.started()], ['help', 'run'])
        (run,) = self.started('run')
        self.assertEqual(run['args'][:run['args'].index('-i')],
                         ['-S', 'tcp:azurebank-test.invalid,1433', '-d', 'AzureBank', '-l', '30', '-b', '-N',
                          '-G', '-U', 'owner@example.invalid', '-X1'])
        self.assertNotIn('--authentication-method', run['args'])
        self.assertNotIn('owner@example.invalid', result.stderr)

    def test_the_odbc_road_refuses_a_tool_that_is_not_the_odbc_one_and_a_name_that_is_not_a_sign_in_name(self):
        result = self.users(OdbcSignInName='owner@example.invalid')
        self.assert_refused_before_azure(result, 'is not the ODBC sqlcmd')
        result = self.users(tool='odbc', OdbcSignInName='owner; x')
        self.assert_refused_before_azure(result, 'the sign-in name of the account')


@unittest.skipUnless(PWSH, 'PowerShell 7 (pwsh) is not installed')
class UsersRunTests(UsersCase):
    """sql-principals.ps1 from its first line to its last. The stand-in sqlcmd answers as the
    server would, so here an exit code means something: what reaches sqlcmd, the life of the
    temporary firewall rule, and what a failure leads to. What SQL Server does with the file is
    checked on a real server (README.md)."""

    def test_the_users_file_is_run_with_b_and_the_five_values_and_both_users_are_reported(self):
        result = self.users()
        self.assertEqual(result.returncode, 0, result.stderr)
        (run,) = self.started('run')
        self.assertEqual(run['args'], [
            '-S', 'tcp:azurebank-test.invalid,1433', '-d', 'AzureBank', '-l', '30', '-b', '-N', 'true',
            '--authentication-method', 'ActiveDirectoryAzCli', '-i', str(HERE / 'sql-principals.sql'), '-v',
            'AppClientId=11111111-aaaa-4bbb-8ccc-000000000001', 'AppObjectId=22222222-aaaa-4bbb-8ccc-000000000002',
            'MigratorClientId=33333333-aaaa-4bbb-8ccc-000000000003',
            'MigratorObjectId=44444444-aaaa-4bbb-8ccc-000000000004', 'CreateForm=Sid'])
        self.assertEqual(self.verbs(), self.SWEEP + self.READS + self.LAST)
        for line in BOTH_USERS:
            self.assertIn(line, result.stderr)
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assert_no_id_or_address_was_said(result)
        # No token is asked of the CLI, and nothing here holds a password.
        self.assertNotIn('account get-access-token', self.verbs())
        self.assertFalse(run['a value is in SQLCMDPASSWORD'])

    def test_b_is_on_every_command_line_that_reaches_the_server(self):
        # Without it sqlcmd exits 0 when the file stops on an error.
        result = self.users(tool='refused:1,entra-only', ProveSqlSignInRefused=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        reaching = self.started('run') + self.started('sign-in')
        self.assertEqual(len(reaching), 3)
        for entry in reaching:
            self.assertIn('-b', entry['args'], entry['kind'])
            self.assertNotIn('-C', entry['args'], 'the certificate is checked')
            self.assertIn('-N', entry['args'])

    def test_the_switch_for_the_second_form_reaches_the_file_as_its_variable(self):
        result = self.users(CreateForm='ExternalProvider')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.started('run')[0]['args'][-2:],
                         ['MigratorObjectId=44444444-aaaa-4bbb-8ccc-000000000004', 'CreateForm=ExternalProvider'])
        self.tool_log.unlink()
        result = self.run_script('sql-principals.ps1', '-SqlcmdPath', str(self.tool), '-CreateForm', 'ByName',
                                 FAKE_SQLCMD_LOG=str(self.tool_log))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('does not belong to the set "Sid,ExternalProvider"', self.said(result))
        self.assertEqual(self.started(), [], 'a form the script does not know runs nothing')

    def test_the_variables_the_runner_passes_are_the_ones_the_file_takes(self):
        self.assertEqual(self.users().returncode, 0)
        passed = [argument.split('=')[0] for argument in self.started('run')[0]['args'][-5:]]
        sql = (HERE / 'sql-principals.sql').read_text(encoding='utf-8')
        self.assertEqual(passed, VARIABLES)
        self.assertEqual(sorted(set(re.findall(r'\$\((\w+)\)', sql))), sorted(VARIABLES))

    def test_an_id_followed_by_a_quote_and_a_statement_never_reaches_sqlcmd(self):
        injected = "11111111-aaaa-4bbb-8ccc-000000000001'; ALTER ROLE db_owner ADD MEMBER [someone]; --"
        for field in ('clientId', 'principalId'):
            for identity in IDS:
                with self.subTest(identity=identity, field=field):
                    self.tool_log.unlink(missing_ok=True)
                    ids = json.loads(json.dumps(IDS))
                    ids[identity][field] = injected
                    result = self.users(environment={'FAKE_AZ_IDS': json.dumps(ids)})
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn('is not an ID. Nothing was run.', self.said(result))
                    self.assertEqual(self.started('run'), [])
                    self.assertNotIn('ALTER ROLE', self.tool_log.read_text(encoding='utf-8'))
                    self.assertEqual(self.verbs()[-1], 'sql server firewall-rule list', 'the list is still read')

    def test_an_id_followed_by_a_newline_reaches_sqlcmd_as_the_id_alone(self):
        # A pattern with ^ and $ lets this one through as it is. Parsed and printed again, it is the ID.
        ids = json.loads(json.dumps(IDS))
        ids['azurebank-app']['clientId'] = '11111111-AAAA-4BBB-8CCC-000000000001\n'
        ids['azurebank-migrate']['principalId'] = ' 44444444-aaaa-4bbb-8ccc-000000000004\n'
        result = self.users(environment={'FAKE_AZ_IDS': json.dumps(ids)})
        self.assertEqual(result.returncode, 0, result.stderr)
        arguments = self.started('run')[0]['args']
        self.assertIn('AppClientId=11111111-aaaa-4bbb-8ccc-000000000001', arguments)
        self.assertIn('MigratorObjectId=44444444-aaaa-4bbb-8ccc-000000000004', arguments)
        self.assertEqual([argument for argument in arguments if re.search(r'\s', argument)], [])

    def test_an_id_that_is_all_zeros_or_missing_is_not_an_id(self):
        for value in ('00000000-0000-0000-0000-000000000000', '', None):
            with self.subTest(value=value):
                self.tool_log.unlink(missing_ok=True)
                ids = json.loads(json.dumps(IDS))
                ids['azurebank-migrate']['clientId'] = value
                result = self.users(environment={'FAKE_AZ_IDS': json.dumps(ids)})
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.started('run'), [])

    def test_what_sqlcmd_would_read_from_the_environment_is_cleared_first(self):
        # sqlcmd takes a scripting variable from the environment, and a user name, a password and
        # a start-up script too.
        planted = {name: "x'; DROP USER [azurebank_app]; --" for name in VARIABLES}
        planted.update(SQLCMDUSER='someone', SQLCMDPASSWORD='not-a-real-value', SQLCMDINI='C:\\\\start.sql')
        result = self.users(environment=planted)
        self.assertEqual(result.returncode, 0, result.stderr)
        for entry in self.started():
            self.assertEqual(set(entry['variables'].values()), {None}, entry['kind'])
            self.assertFalse(entry['a value is in SQLCMDPASSWORD'], entry['kind'])

    def test_a_rule_left_by_an_earlier_run_is_deleted_before_anything_else(self):
        self.rules.write_text(json.dumps(['AllowAzureServices', RULE]), encoding='utf-8')
        result = self.users()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.verbs(), self.SWEEP + ['sql server firewall-rule delete'] + self.READS + self.LAST)
        self.assertLess(self.in_order().index('az sql server firewall-rule delete'), self.in_order().index('sqlcmd run'))
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertIn('was left by an earlier run', result.stderr)

    def test_a_leftover_rule_that_cannot_be_deleted_stops_the_run_and_prints_the_command(self):
        self.rules.write_text(json.dumps(['AllowAzureServices', RULE]), encoding='utf-8')
        result = self.users(state='delete-fails')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('it may still be there', result.stderr)
        self.assertIn(self.BY_HAND, result.stderr)
        self.assertNotIn('identity show', self.verbs(), 'nothing else is done with the rule in place')
        self.assertEqual(self.started('run'), [])
        self.assertEqual(self.verbs()[-1], 'sql server firewall-rule list', 'the list is still read')
        self.assertIn(f'Firewall rules now: AllowAzureServices, {RULE}.', result.stderr)
        self.assertIn(f'The temporary rule {RULE} is still there.', self.said(result))

    def test_a_rule_list_that_cannot_be_read_back_is_not_taken_for_a_clean_one(self):
        result = self.users(state='list-fails-at-the-end')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('could not be read back', result.stderr)
        self.assertNotIn('Firewall rules now', result.stderr)

    def test_a_refused_address_is_allowed_for_the_run_and_the_rule_is_gone_at_the_end(self):
        result = self.users(tool='refused:1')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.in_order(), [
            'sqlcmd help', 'sqlcmd version', 'az sql server', 'az sql server firewall-rule list',
            'az identity show', 'az identity show', 'sqlcmd run', 'az sql server firewall-rule create',
            'sqlcmd run', 'az sql server firewall-rule delete', 'az sql server firewall-rule list'])
        create = next(call for call in self.calls() if call[:4] == ['sql', 'server', 'firewall-rule', 'create'])
        self.assertEqual([create[create.index(flag) + 1]
                          for flag in ('--name', '--start-ip-address', '--end-ip-address')], [RULE, ADDRESS, ADDRESS])
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertEqual(self.started('run')[0]['args'], self.started('run')[1]['args'])
        for line in BOTH_USERS:
            self.assertIn(line, result.stderr)
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)
        self.assert_no_id_or_address_was_said(result)

    def test_only_the_firewalls_own_refusal_is_waited_out(self):
        result = self.users(tool='refused:3')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(self.started('run')), 4, 'the rule takes a while: refused, refused, refused, let in')
        self.assertEqual(self.verbs().count('sql server firewall-rule create'), 1)
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])

    def test_the_firewalls_refusal_is_known_by_its_sentence_with_its_number_or_without(self):
        # The number is 40615. sqlcmd leaves it out of an error at sign-in; a tool that printed it
        # must still be let through the firewall and not be told its answer is some other failure.
        result = self.users(tool='refused:2,numbered')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(self.started('run')), 3)
        self.assertEqual(self.verbs().count('sql server firewall-rule create'), 1)
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assert_no_id_or_address_was_said(result)

    def test_a_failure_after_the_rule_is_in_ends_the_run_at_once(self):
        result = self.users(tool='refused:1,fails-after-the-refusals', WaitSeconds=5)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(self.started('run')), 2, 'an error of the file is not tried again')
        self.assertNotIn('still refused', result.stderr)
        self.assertIn('Msg 50004', result.stderr)
        self.assertIn('Unknown user: [someone] SQL_USER', result.stderr)
        self.assertIn('sqlcmd exited 1', self.said(result))
        self.assertEqual(self.verbs()[-2:], ['sql server firewall-rule delete', 'sql server firewall-rule list'])
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])

    def test_a_server_that_keeps_refusing_ends_the_run_at_the_deadline_and_the_rule_is_removed(self):
        # Twelve refusals are more than two seconds of waiting can see the end of.
        result = self.users(tool='refused:12', WaitSeconds=2)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('still refused this address 2 seconds after the rule was added', self.said(result))
        self.assertIn(len(self.started('run')), (3, 4, 5))
        self.assertEqual(self.verbs()[-2:], ['sql server firewall-rule delete', 'sql server firewall-rule list'])
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertNotIn(ADDRESS, result.stderr)

    def test_a_refusal_that_names_no_ipv4_address_opens_nothing_and_prints_the_command_to_run_by_hand(self):
        # What reaches az is an address parsed and printed again, or nothing: not an IPv6 address,
        # not one a parser reads as another (a leading zero is octal), not an address and more.
        for named in ('2001:db8::7', '010.0.113.7', '203.0.113.7 ; x', 'unknown'):
            with self.subTest(named=named):
                self.log.unlink(missing_ok=True)
                self.tool_log.unlink(missing_ok=True)
                result = self.users(tool='refused:1', environment={'FAKE_SQLCMD_NAMES': named})
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn('sql server firewall-rule create', self.verbs())
                self.assertEqual(len(self.started('run')), 1)
                self.assertIn('The address to allow could not be read from the refusal', self.said(result))
                # By hand, under a name of its own: the sweep of the next run would delete the rule otherwise.
                self.assertIn(f'--name {RULE}-by-hand --start-ip-address <address> --end-ip-address <address>',
                              result.stderr)
                self.assertIn(f'firewall-rule delete --resource-group azurebank-demo --server azurebank-test '
                              f'--name {RULE}-by-hand', result.stderr)
                self.assertEqual(self.verbs()[-1], 'sql server firewall-rule list')

    def test_any_other_failure_opens_no_firewall_and_is_reported_without_an_address(self):
        result = self.users(tool='login-failed')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(self.started('run')), 1)
        self.assertIn("Client with IP address '<address>'.", result.stderr)
        self.assertNotIn(ADDRESS, result.stderr)
        self.assertNotIn('sql server firewall-rule create', self.verbs())
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])

    def test_a_database_the_file_refuses_stops_the_run_and_nothing_is_tried_again(self):
        result = self.users(tool='tampered')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(self.started('run')), 1)
        self.assertIn('Msg 50003', result.stderr)
        self.assertIn('Code found: [planted]', result.stderr)
        self.assertIn('run nothing else as administrator in this database', self.said(result))

    def test_an_exit_of_zero_without_both_users_is_not_a_pass(self):
        for behaviour in ('silent', 'wrong-roles'):
            with self.subTest(behaviour=behaviour):
                result = self.users(tool=behaviour)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('that is not a pass', self.said(result))

    def test_a_delete_that_fails_at_the_end_exits_non_zero_though_the_users_were_made(self):
        result = self.users(tool='refused:1', state='delete-fails')
        self.assertNotEqual(result.returncode, 0, 'a rule left behind is never a success')
        self.assertIn(BOTH_USERS[1], result.stderr)
        self.assertIn('it may still be there', result.stderr)
        self.assertIn(self.BY_HAND, result.stderr)
        self.assertEqual(self.verbs()[-2:], ['sql server firewall-rule delete', 'sql server firewall-rule list'])
        self.assertIn(f'Firewall rules now: AllowAzureServices, {RULE}.', result.stderr)
        self.assertIn(f'The temporary rule {RULE} is still there.', self.said(result))
        self.assertNotIn(ADDRESS, result.stderr)

    def test_a_delete_that_reports_a_failure_exits_non_zero_even_when_the_list_is_clean(self):
        result = self.users(tool='refused:1', state='delete-says-it-failed')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertIn('Firewall rules now: AllowAzureServices.', result.stderr)
        self.assertIn(self.BY_HAND, result.stderr)
        self.assertIn('run the delete by hand to be sure', self.said(result))

    def test_the_sql_sign_in_is_tried_while_the_firewall_is_open_and_passes_only_on_the_entra_only_reason(self):
        result = self.users(tool='refused:1,entra-only', ProveSqlSignInRefused=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        order = self.in_order()
        self.assertEqual(order[-5:], ['az sql server firewall-rule create', 'sqlcmd run', 'sqlcmd sign-in',
                                      'az sql server firewall-rule delete', 'az sql server firewall-rule list'])
        (attempt,) = self.started('sign-in')
        self.assertEqual(attempt['args'][:9], ['-S', 'tcp:azurebank-test.invalid,1433', '-d', 'AzureBank', '-l', '30',
                                               '-b', '-N', 'true'])
        self.assertEqual(attempt['args'][9:12], ['--authentication-method', 'SqlPassword', '-U'])
        self.assertRegex(attempt['args'][12], r'^nobody_[0-9a-f]{12}$')
        self.assertEqual(attempt['args'][13:], ['-Q', 'SELECT 1'])
        # The made-up value travels in the tool's own variable, for that one start and no other:
        # the two az calls after it, in the order above, were not handed it.
        self.assertTrue(attempt['a value is in SQLCMDPASSWORD'])
        self.assertNotIn('-P', attempt['args'])
        self.assertEqual([entry['a value is in SQLCMDPASSWORD'] for entry in self.started('run')], [False, False])
        self.assertIn('Proved: a SQL sign-in is refused', result.stderr)
        self.assertIn('Reason: Azure Active Directory only authentication is enabled.', result.stderr)
        self.assert_no_id_or_address_was_said(result)

    def test_a_refusal_for_any_other_reason_proves_nothing_and_the_run_says_so(self):
        result = self.users(tool='refused:1', ProveSqlSignInRefused=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Not proven', result.stderr)
        self.assertIn('was asked for and not proven', self.said(result))
        self.assertIn(BOTH_USERS[0], result.stderr, 'the users were made all the same')
        self.assertEqual(self.rules_left(), ['AllowAzureServices'])
        self.assertNotIn('Proved', result.stderr)

    def test_a_sql_sign_in_that_is_let_in_is_a_failure(self):
        result = self.users(tool='let-in', ProveSqlSignInRefused=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('was let in', self.said(result))

    def test_without_the_switch_no_sql_sign_in_is_tried(self):
        self.assertEqual(self.users(tool='entra-only').returncode, 0)
        self.assertEqual(self.started('sign-in'), [])


class UsersFileTests(unittest.TestCase):
    """sql-principals.sql, read as text. No engine runs here: these keep each guard where it is, in
    the order that makes it a guard. What the guards do is run on a real engine (README.md)."""

    @classmethod
    def setUpClass(cls):
        cls.sql = (HERE / 'sql-principals.sql').read_text(encoding='utf-8').replace('\r\n', '\n')
        # The statements alone: a comment may name what a statement must not do.
        cls.code = '\n'.join(line for line in cls.sql.split('\n') if not line.lstrip().startswith('--'))
        cls.changing = re.compile(r'\b(DROP|CREATE|ALTER|EXEC|GRANT|DENY|REVOKE|INSERT|UPDATE|DELETE)\b')

    def at(self, text, after=0):
        position = self.code.find(text, after)
        self.assertNotEqual(position, -1, f'not in the file: {text}')
        return position

    def test_it_is_one_batch_and_sqlcmd_only_fills_in_five_values(self):
        lines = [line.strip() for line in self.code.split('\n')]
        self.assertEqual([line for line in lines if line.upper() == 'GO' or line.startswith((':', '!!'))], [])
        self.assertEqual(re.findall(r'\$\((\w+)\)', self.code),
                         ['AppClientId', 'AppObjectId', 'MigratorClientId', 'MigratorObjectId', 'CreateForm'])
        # Each lands in a typed variable, and the text sqlcmd put in is used nowhere else.
        for line in self.code.split('\n'):
            if '$(' in line:
                self.assertRegex(line, r"^DECLARE @\w+ (uniqueidentifier = '\$\(\w+\)'|nvarchar\(20\) = N'\$\(\w+\)');$")
        for word in ('password', 'pwd', 'token'):
            self.assertNotIn(word, self.code.lower())

    def test_it_refuses_any_database_but_azurebank_before_it_reads_a_value(self):
        self.assertLess(self.at("IF DB_NAME() <> N'AzureBank'\n    THROW 50000,"), self.at('$('))
        self.assertLess(self.at('SET XACT_ABORT ON;'), self.at('IF DB_NAME()'))

    def test_it_refuses_a_database_that_holds_code_before_any_statement_that_could_fire_a_trigger(self):
        begin, refusal = self.at('BEGIN TRANSACTION;'), self.at('THROW 50003,')
        self.assertLess(begin, refusal)
        before = self.code[begin + len('BEGIN TRANSACTION;'):refusal]
        # Every trigger and every other module but one kind of Microsoft's own: one SELECT, and
        # the refusal hangs on its answer alone.
        self.assertEqual(before.strip().split('\n'), [
            "SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(QUOTENAME(code.name), N'(no name)')), N', ')",
            '              FROM (SELECT name FROM sys.triggers',
            '                    UNION ALL',
            '                    SELECT OBJECT_NAME(m.object_id) FROM sys.sql_modules AS m',
            '                    WHERE m.object_id NOT IN (SELECT object_id FROM sys.triggers)',
            '                      AND NOT EXISTS (SELECT 1 FROM sys.all_objects AS o',
            '                                      WHERE o.object_id = m.object_id',
            '                                        '
            "AND o.schema_id = SCHEMA_ID(N'sys') AND o.is_ms_shipped = 1)) AS code);",
            'IF @found IS NOT NULL',
            'BEGIN',
            "    PRINT N'Code found: ' + LEFT(@found, 3500);"])
        self.assertEqual(self.changing.findall(self.code[:begin]), [], 'and nothing changes before it')
        # The same question is asked again before the commit.
        self.assertEqual(self.code.count('\n'.join(before.strip().split('\n')[:9])), 2)

    def test_only_a_module_both_in_sys_and_shipped_by_microsoft_is_left_out_and_every_trigger_counts(self):
        # A new Azure SQL database held the view sys.database_firewall_rules, in the schema sys
        # and marked is_ms_shipped; LocalDB does not list it. Nobody can create an object in sys,
        # and is_ms_shipped alone is set on objects in other schemas too (LocalDB lets sysadmin
        # mark a view in dbo), so a module is left out only when both hold. A trigger never is.
        flat = ' '.join(self.code.split())
        left_out = ("AND NOT EXISTS (SELECT 1 FROM sys.all_objects AS o WHERE o.object_id = m.object_id "
                    "AND o.schema_id = SCHEMA_ID(N'sys') AND o.is_ms_shipped = 1)) AS code)")
        modules = ('FROM (SELECT name FROM sys.triggers UNION ALL SELECT OBJECT_NAME(m.object_id) '
                   'FROM sys.sql_modules AS m WHERE m.object_id NOT IN (SELECT object_id FROM sys.triggers) ')
        # Both code lists: the triggers whole, then the modules that are not triggers, less that one kind.
        self.assertEqual(flat.count(modules + left_out), 2)
        # Nothing else is left out anywhere, and the schema is sys and no other. The third time the
        # pair is read is the permission list's, which has a test of its own.
        self.assertEqual(flat.count('sys.all_objects'), 3)
        self.assertEqual(flat.count('is_ms_shipped'), 3)
        self.assertEqual(re.findall(r'schema_id = (\S+)', flat), ["SCHEMA_ID(N'sys')"] * 3)
        # The trigger half has no condition at all: every trigger, is_ms_shipped or not.
        self.assertEqual(flat.count('SELECT name FROM sys.triggers'), 2)
        self.assertEqual(flat.count('SELECT name FROM sys.triggers UNION ALL SELECT OBJECT_NAME(m.object_id)'), 2)

    def test_everything_that_can_hold_a_right_is_listed_before_the_commit(self):
        last_change = max(match.start() for match in self.changing.finditer(self.code))
        refusal, commit = self.at('THROW 50004, @bad, 1;'), self.at('COMMIT TRANSACTION;')
        self.assertLess(last_change, refusal)
        self.assertLess(refusal, commit)
        self.assertEqual(self.code.count('COMMIT'), 1)
        lists = self.code[last_change:refusal]
        self.assertEqual(re.findall(r"SET @bad \+= N'([^']+)';", lists),
                         ['code; ', 'unknown user; ', 'unknown role; ', 'unknown role member; ',
                          'our role memberships are not five; ', 'unknown permission; ', 'schema owned by a user; '])
        for view in ('sys.triggers', 'sys.sql_modules', 'sys.database_principals', 'sys.database_role_members',
                     'sys.database_permissions', 'sys.schemas'):
            self.assertIn(f'FROM {view}', lists)
        self.assertIn("IF @bad <> N''\nBEGIN\n    SET @bad = N'Not committed: ' + @bad;\n    THROW 50004, @bad, 1;\nEND",
                      self.code)

    def test_every_user_is_compared_by_name_and_by_stored_id_together(self):
        self.assertIn(
            "WHERE principal_id > 4 AND type <> 'R'\n"
            "                AND NOT (type = 'E' AND ((name = N'azurebank_app' AND sid = @AppSid)\n"
            "                                      OR (name = N'azurebank_migrator' AND sid = @MigratorSid))));",
            self.code)
        # The stored ID is the client ID, built from a typed value, in the byte order the server
        # stores. The object ID never becomes a stored ID: it only names the identity in the second form.
        for user in ('App', 'Migrator'):
            self.assertIn(f'DECLARE @{user}Sid varbinary(16) = CONVERT(varbinary(16), @{user}ClientId);', self.code)
            self.assertEqual(len(re.findall(rf'@{user}ObjectId\b', self.code)), 3,
                             'declared, compared with the other identity\'s, and named in the second form')
        self.assertEqual(re.findall(r'CONVERT\(varbinary\(16\), ([^)]+)\)', self.code),
                         ['@AppClientId', '@MigratorClientId'])

    def test_every_role_membership_is_compared_db_owner_included(self):
        self.assertIn(
            "WHERE NOT ((m.member_principal_id = 1 AND USER_NAME(m.role_principal_id) = N'db_owner')\n"
            "                      OR (USER_NAME(m.member_principal_id) = N'azurebank_app'\n"
            "                          AND USER_NAME(m.role_principal_id) IN (N'db_datareader', N'db_datawriter'))\n"
            "                      OR (USER_NAME(m.member_principal_id) = N'azurebank_migrator'\n"
            "                          AND USER_NAME(m.role_principal_id) IN (N'db_datareader', N'db_datawriter', "
            "N'db_ddladmin'))));", self.code)
        self.assertIn('IF (SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id > 4) <> 5',
                      self.code)

    def test_every_condition_in_the_file_is_this_one_and_no_looser(self):
        # What follows each WHERE, to the end of its statement, in the file's order. A condition
        # loosened by one word still runs, and its list is then silent about what it was written
        # to name: CONTROL taken for an expected permission, guest allowed to connect.
        def given(role, user):
            return (f"role_principal_id = DATABASE_PRINCIPAL_ID(N'{role}') AND member_principal_id = "
                    f"DATABASE_PRINCIPAL_ID(N'{user}')) ALTER ROLE [{role}] ADD MEMBER [{user}]")
        code = ('m.object_id NOT IN (SELECT object_id FROM sys.triggers) AND NOT EXISTS (SELECT 1 FROM sys.all_objects '
                'AS o WHERE o.object_id = m.object_id '
                "AND o.schema_id = SCHEMA_ID(N'sys') AND o.is_ms_shipped = 1)) AS code)")
        self.assertEqual([' '.join(found.split()) for found in re.findall(r'\bWHERE (.+?);\n', self.code, re.S)], [
            code,
            # A user is replaced when its stored ID is another one, or when it is another kind of user.
            "name = N'azurebank_app' AND (sid <> @AppSid OR type <> 'E')) DROP USER [azurebank_app]",
            "name = N'azurebank_migrator' AND (sid <> @MigratorSid OR type <> 'E')) DROP USER [azurebank_migrator]",
            given('db_datareader', 'azurebank_app'), given('db_datawriter', 'azurebank_app'),
            given('db_datareader', 'azurebank_migrator'), given('db_datawriter', 'azurebank_migrator'),
            given('db_ddladmin', 'azurebank_migrator'),
            # The lists before the commit: code, users, roles, role members and their count,
            # permissions, schema owners.
            code,
            "principal_id > 4 AND type <> 'R' AND NOT (type = 'E' AND ((name = N'azurebank_app' AND sid = @AppSid) "
            "OR (name = N'azurebank_migrator' AND sid = @MigratorSid))))",
            "type = 'R' AND is_fixed_role = 0 AND name <> N'public')",
            "NOT ((m.member_principal_id = 1 AND USER_NAME(m.role_principal_id) = N'db_owner') "
            "OR (USER_NAME(m.member_principal_id) = N'azurebank_app' "
            "AND USER_NAME(m.role_principal_id) IN (N'db_datareader', N'db_datawriter')) "
            "OR (USER_NAME(m.member_principal_id) = N'azurebank_migrator' "
            "AND USER_NAME(m.role_principal_id) IN (N'db_datareader', N'db_datawriter', N'db_ddladmin'))))",
            "member_principal_id > 4) <> 5 SET @bad += N'our role memberships are not five; '",
            # CONNECT for dbo and the two users, granted; and to public, granted, only what the
            # engine gives it: system objects (their IDs are negative) and two database permissions;
            # and SELECT on an object that is in sys and marked is_ms_shipped, as on Azure SQL.
            "NOT (d.class = 0 AND d.type = 'CO' AND d.state = 'G' "
            "AND USER_NAME(d.grantee_principal_id) IN (N'dbo', N'azurebank_app', N'azurebank_migrator')) "
            "AND NOT (d.grantee_principal_id = 0 AND d.state = 'G' "
            "AND ((d.class = 1 AND d.major_id < 0) OR (d.class = 0 AND d.type IN ('VWCK', 'VWCM')))) "
            "AND NOT (d.grantee_principal_id = 0 AND d.state = 'G' AND d.class = 1 AND d.type = 'SL' "
            "AND EXISTS (SELECT 1 FROM sys.all_objects AS o WHERE o.object_id = d.major_id "
            "AND o.schema_id = SCHEMA_ID(N'sys') AND o.is_ms_shipped = 1)))",
            "p.type <> 'R' AND p.principal_id NOT IN (1, 2, 3, 4))",
            # After the commit: the roles of each user, and whether its stored ID is the one asked for.
            "member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_app'))",
            "name = N'azurebank_app' AND type = 'E' AND sid = @AppSid), N'1', N'0')",
            "member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_migrator'))",
            "name = N'azurebank_migrator' AND type = 'E' AND sid = @MigratorSid), N'1', N'0')"])

    def test_every_if_in_the_file_is_this_one(self):
        found, absent = '@found IS NOT NULL', 'NOT EXISTS (SELECT 1 FROM sys.database_role_members'
        self.assertEqual(re.findall(r'^IF (.+)$', self.code, re.M), [
            "DB_NAME() <> N'AzureBank'",
            "@CreateForm NOT IN (N'Sid', N'ExternalProvider')",
            '@AppClientId = @MigratorClientId OR @AppObjectId = @MigratorObjectId',
            found,
            'EXISTS (SELECT 1 FROM sys.database_principals', 'EXISTS (SELECT 1 FROM sys.database_principals',
            "DATABASE_PRINCIPAL_ID(N'azurebank_app') IS NULL", "DATABASE_PRINCIPAL_ID(N'azurebank_migrator') IS NULL",
            absent, absent, absent, absent, absent,
            found, found, found, found,
            '(SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id > 4) <> 5',
            found, found,
            "@bad <> N''"])
        self.assertIn("IF @CreateForm NOT IN (N'Sid', N'ExternalProvider')\n    THROW 50002,", self.code)

    def test_each_list_that_finds_something_says_so_in_the_refusal_and_prints_the_names(self):
        for word, heading in (('code', 'Code found'), ('unknown user', 'Unknown user'),
                              ('unknown role', 'Unknown role'), ('unknown role member', 'Unknown role member'),
                              ('unknown permission', 'Unknown permission'),
                              ('schema owned by a user', 'Schema owned by a user')):
            self.assertIn(f"IF @found IS NOT NULL\nBEGIN\n    SET @bad += N'{word}; ';\n"
                          f"    PRINT N'{heading}: ' + LEFT(@found, 3500);\nEND", self.code)
        # Each list fills @found itself: none reads what the list before it left there.
        lists = self.code[self.at('ALTER ROLE [db_ddladmin] ADD MEMBER [azurebank_migrator];'):self.at("IF @bad <> N''")]
        self.assertEqual(lists.count('SET @found = (SELECT STRING_AGG('), 6)
        self.assertEqual(lists.count('IF @found IS NOT NULL'), 6)

    def test_public_may_keep_select_on_an_object_of_microsofts_in_sys_and_nothing_wider(self):
        # A new Azure SQL database granted public SELECT on the view sys.database_firewall_rules,
        # whose ID is not negative, and a read-only query with the file's own conditions found that
        # the permission list would refuse it. The list now keeps that grant and nothing wider:
        # granted, SELECT, on an object, to public, and the object both in sys and marked
        # is_ms_shipped, read as the code lists read it. One condition fewer and it would keep a
        # DENY or a grant that may be passed on, another right on the object, a grant to one of
        # the two users, a grant on a schema or on the database, or a grant on anyone's object.
        flat = ' '.join(self.code.split())
        start = flat.index('FROM sys.database_permissions AS d WHERE ')
        conditions = flat[start:flat.index(';', start)]
        self.assertEqual(conditions.count('AND NOT ('), 2, "the engine's grants to public, then this one")
        kept = conditions[conditions.rindex('AND NOT ('):]
        self.assertEqual(re.findall(r'd\.state (\S+ \S+)', kept), ["= 'G'"], 'granted: not denied, not passed on')
        self.assertEqual(re.findall(r'd\.type (\S+ \S+)', kept), ["= 'SL'"], 'SELECT and no other permission')
        self.assertEqual(re.findall(r'd\.class (\S+ \S+)', kept), ['= 1'], 'on an object, not a schema or the database')
        self.assertEqual(re.findall(r'd\.grantee_principal_id (\S+ \S+)', kept), ['= 0'], 'to public, to nobody else')
        self.assertTrue(kept.endswith("AND EXISTS (SELECT 1 FROM sys.all_objects AS o WHERE o.object_id = d.major_id "
                                      "AND o.schema_id = SCHEMA_ID(N'sys') AND o.is_ms_shipped = 1)))"), kept)
        self.assertEqual(re.findall(r'\bo\.(\w+)', kept), ['object_id', 'schema_id', 'is_ms_shipped'])
        # The pair of conditions is the code lists', word for word.
        self.assertEqual(flat.count("AND o.schema_id = SCHEMA_ID(N'sys') AND o.is_ms_shipped = 1)"), 3)

    def test_a_refused_permission_on_an_object_names_the_object_and_never_its_id(self):
        # A grant to public on a table was printed "GRANT SELECT (OBJECT_OR_COLUMN) to [public]"
        # (LocalDB), which does not say on what. A permission on an object (class 1) now ends with
        # " on [schema].[name]", both read from the object's ID by the two functions that name it,
        # and the ID itself is printed nowhere. A name that cannot be read gives "(no name)" there
        # and leaves the rest of the line.
        flat = ' '.join(self.code.split())
        start = flat.index('ISNULL(d.state_desc')
        printed = flat[start:flat.index(' FROM sys.database_permissions AS d', start)]
        self.assertEqual(printed,
                         "ISNULL(d.state_desc COLLATE DATABASE_DEFAULT + N' ' + d.permission_name COLLATE DATABASE_DEFAULT "
                         "+ N' (' + d.class_desc COLLATE DATABASE_DEFAULT + N') to ' "
                         "+ QUOTENAME(USER_NAME(d.grantee_principal_id)), N'(no name)') "
                         "+ IIF(d.class = 1, N' on ' + ISNULL(QUOTENAME(OBJECT_SCHEMA_NAME(d.major_id)) + N'.' "
                         "+ QUOTENAME(OBJECT_NAME(d.major_id)), N'(no name)'), N'')), N', ')")
        self.assertEqual(re.findall(r'(\w+)\(d\.major_id\)', printed), ['OBJECT_SCHEMA_NAME', 'OBJECT_NAME'])
        self.assertEqual(printed.count('major_id'), 2)

    def test_the_roles_it_gives_are_the_five_and_no_other(self):
        self.assertEqual(sorted(re.findall(r'ALTER ROLE \[(\w+)\] ADD MEMBER \[(\w+)\];', self.code)),
                         [('db_datareader', 'azurebank_app'), ('db_datareader', 'azurebank_migrator'),
                          ('db_datawriter', 'azurebank_app'), ('db_datawriter', 'azurebank_migrator'),
                          ('db_ddladmin', 'azurebank_migrator')])
        self.assertEqual(self.code.count('ALTER ROLE'), 5)
        self.assertEqual(self.code.count('GRANT'), 0)

    def test_both_forms_of_create_user_are_built_from_typed_values_only(self):
        for user, variable in (('azurebank_app', 'App'), ('azurebank_migrator', 'Migrator')):
            self.assertIn(f"N'CREATE USER [{user}] WITH SID = ' + CONVERT(nvarchar(34), @{variable}Sid, 1) "
                          "+ N', TYPE = E;',", self.code)
            self.assertIn(f"N'CREATE USER [{user}] FROM EXTERNAL PROVIDER WITH OBJECT_ID = ''' + "
                          f"CONVERT(nvarchar(36), @{variable}ObjectId) + N''';');", self.code)
        self.assertEqual(self.code.count('CREATE USER'), 4)
        self.assertEqual(self.code.count('EXEC (@statement);'), 2)

    def test_what_the_runner_waits_for_is_printed_after_the_commit_and_holds_no_id(self):
        after = self.code[self.at('COMMIT TRANSACTION;'):]
        self.assertEqual(self.changing.findall(after), [], 'nothing changes after the commit')
        self.assertEqual(after.count("; ID as asked: ' + @asked;"), 2)
        self.assertNotIn('ID as asked', self.code[:self.at('COMMIT TRANSACTION;')])
        for line in self.code.split('\n'):
            if 'PRINT' in line:
                self.assertNotRegex(line, r'@\w*(Sid|ClientId|ObjectId)\b')


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


TEMPLATES = ('main', 'guardrails', 'app-inputs')
NINE_ACTIONS = [
    'Microsoft.App/containerApps/read', 'Microsoft.App/containerApps/write',
    'Microsoft.App/containerApps/revisions/read', 'Microsoft.App/containerApps/revisions/replicas/read',
    'Microsoft.App/jobs/read', 'Microsoft.App/jobs/write', 'Microsoft.App/jobs/start/action',
    'Microsoft.App/jobs/executions/read', 'Microsoft.App/jobs/execution/read',
]
BEHIND_DEPLOY_APP = ['Microsoft.App/containerApps', 'Microsoft.App/jobs', 'Microsoft.Authorization/roleAssignments',
                     'Microsoft.Authorization/roleAssignments', 'Microsoft.Insights/actionGroups',
                     'Microsoft.Resources/deployments']
# The module that refuses deployApp=true without these values (app-inputs.bicep), and what it asks
# of each: a length of exactly 40 for the image tag, at least 1 for the others.
GUARD = 'azurebank-app-inputs'
GUARDED = {'imageTag': (40, 40), 'alertEmail': (1, None), **dict.fromkeys(SEVEN, (1, None))}
# What every snapshot below is evaluated against: none of it is real.
SNAPSHOT_CONTEXT = ['--subscription-id', '00000000-0000-4000-8000-00000000000a', '--resource-group', 'azurebank-demo',
                    '--location', 'italynorth', '--tenant-id', '00000000-0000-4000-8000-00000000000b']
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
    # A trigger outside the list that holds for every job, with one exception: a job whose name is
    # in the second list may run on a schedule.
    {'allOf': [{'field': f'{JOB}/configuration.triggerType', 'exists': True},
               {'field': f'{JOB}/configuration.triggerType', 'notIn': "[parameters('allowedJobTriggers')]"},
               {'not': {'allOf': [{'field': f'{JOB}/configuration.triggerType', 'equals': 'Schedule'},
                                  {'field': 'name', 'in': "[parameters('scheduledJobs')]"}]}}]},
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


def copy_templates(folder, **texts):
    """The three templates written into a folder, each as it is or as the text given for it."""
    for name in TEMPLATES:
        text = texts.pop(name, None)
        if text is None:
            text = (HERE / f'{name}.bicep').read_text(encoding='utf-8')
        (pathlib.Path(folder) / f'{name}.bicep').write_text(text, encoding='utf-8')
    assert not texts, f'no such template: {sorted(texts)}'


def bicep_literal(value):
    if isinstance(value, bool):
        return 'true' if value else 'false'
    return "'" + value.replace('\\', '\\\\').replace("'", "\\'") + "'"


# The values of a run with the app, none of them real, and of one without it.
FOUNDATION_INPUTS = {'entraAdminObjectId': '00000000-0000-4000-8000-000000000001', 'entraAdminLogin': 'owner'}
APP_INPUTS = {**FOUNDATION_INPUTS, 'deployApp': True, 'imageTag': TAG, 'alertEmail': 'owner',
              **dict.fromkeys(SEVEN, 'x')}


def snapshot(folder, values):
    """`bicep snapshot` of the main.bicep in a folder, given these values: the template worked out
    offline, the way a what-if works it out. On the template of the first session's step 9 it left
    the app's name an expression, as that what-if did (README.md, "Measured on Azure"). Returns its
    exit code, what it printed and the resources it predicts."""
    parameters = pathlib.Path(folder) / 'run.bicepparam'
    parameters.write_text("using 'main.bicep'\n" + ''.join(f'param {name} = {bicep_literal(value)}\n'
                                                           for name, value in values.items()), encoding='utf-8')
    written = pathlib.Path(folder) / 'run.snapshot.json'
    written.unlink(missing_ok=True)
    result = subprocess.run([BICEP, 'snapshot', str(parameters), '--mode', 'overwrite', *SNAPSHOT_CONTEXT],
                            capture_output=True, text=True, timeout=300)
    said = result.stdout + result.stderr
    predicted = json.loads(written.read_text(encoding='utf-8'))['predictedResources'] if written.exists() else None
    return result.returncode, said, predicted


@unittest.skipUnless(BICEP, 'the Bicep CLI is not installed')
class TemplateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.compiled = {}
        for name in TEMPLATES:
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

    def test_the_app_is_named_by_a_plain_value_and_no_resource_id_reads_a_secret(self):
        # A what-if works out no expression that reads a secure parameter. On 2026-10-03 the app's
        # name went through one, and the what-if listed the app and the role assignment on it as
        # Unsupported (README.md, "Measured on Azure"). What makes a resource's ID: its name,
        # scope, condition, copy, subscription, group and API version.
        (app,) = self.of_type(APP)
        self.assertEqual(app['name'], 'azurebank')
        # The name the two scripts look the app up by.
        self.assertRegex((HERE / 'deploy.py').read_text(encoding='utf-8'), r"(?m)^APP = 'azurebank'$")
        self.assertRegex((HERE / 'secrets.ps1').read_text(encoding='utf-8'), r"(?m)^\$AppName = 'azurebank'$")
        # Each secure parameter, and each variable that reads one, directly or through another.
        reads_a_secret = {f"parameters('{name}')" for name in SEVEN}
        variables = {f"variables('{name}')": json.dumps(value)
                     for name, value in self.main.get('variables', {}).items()}
        while True:
            more = {name for name, value in variables.items()
                    if name not in reads_a_secret and any(token in value for token in reads_a_secret)}
            if not more:
                break
            reads_a_secret |= more
        for resource in self.resources:
            for key in ('name', 'scope', 'condition', 'copy', 'subscriptionId', 'resourceGroup', 'apiVersion'):
                text = json.dumps(resource.get(key, ''))
                self.assertEqual([token for token in reads_a_secret if token in text], [], (resource['type'], key))

    def test_deploy_app_true_waits_for_a_check_of_each_value_the_app_needs(self):
        (guard,) = [module for module in self.of_type('Microsoft.Resources/deployments') if module['name'] == GUARD]
        self.assertEqual(guard['condition'], "[parameters('deployApp')]")
        self.assertEqual(guard['properties']['expressionEvaluationOptions'], {'scope': 'inner'})
        # Each value goes in under its own name, as main.bicep was given it.
        self.assertEqual(guard['properties']['parameters'],
                         {name: {'value': f"[parameters('{name}')]"} for name in GUARDED})
        declared = guard['properties']['template']['parameters']
        self.assertEqual(declared, self.compiled['app-inputs']['parameters'])
        self.assertEqual({name: (entry.get('minLength'), entry.get('maxLength')) for name, entry in declared.items()},
                         GUARDED)
        # Secure inside it as well: a plain parameter of a nested deployment is kept in its history.
        self.assertEqual(sorted(name for name, entry in declared.items() if entry['type'].lower() == 'securestring'),
                         sorted(SEVEN))
        self.assertEqual(sorted(name for name, entry in declared.items() if 'defaultValue' in entry), [])
        # It creates nothing and gives nothing back.
        self.assertEqual(guard['properties']['template']['resources'], [])
        self.assertNotIn('outputs', guard['properties']['template'])
        # The app, the job and the action group wait for it; the role assignments and the alerts
        # wait for them.
        waits = f"[resourceId('Microsoft.Resources/deployments', '{GUARD}')]"
        self.assertEqual(sorted(resource['type'] for resource in self.resources
                                if waits in resource.get('dependsOn', [])),
                         sorted([APP, JOB, 'Microsoft.Insights/actionGroups']))

    def test_a_what_if_can_name_everything_the_app_run_creates(self):
        # Offline, with values of the shapes secrets.ps1 writes: fourteen things and eight more, and
        # every ID worked out. The check creates nothing, and nothing of it is listed. Until
        # 2026-10-03 it was nine more: the alert on the log workspace is now built only when it is
        # asked for, and then the run is the 23 it was, with the nine step 9's what-if would create.
        with tempfile.TemporaryDirectory() as folder:
            copy_templates(folder)
            code, said, predicted = snapshot(folder, {**APP_INPUTS, 'logVolumeAlert': True})
            self.assertEqual(code, 0, said)
            self.assertEqual([resource['id'] for resource in predicted if resource['id'].startswith('[')], [])
            self.assertEqual(len(predicted), 23)
            code, said, predicted = snapshot(folder, APP_INPUTS)
            self.assertEqual(code, 0, said)
            self.assertEqual([resource['id'] for resource in predicted if resource['id'].startswith('[')], [])
            self.assertEqual(len(predicted), 22)
            (app,) = [resource['id'] for resource in predicted if resource['type'] == APP]
            self.assertTrue(app.endswith('/resourceGroups/azurebank-demo/providers/' + APP + '/azurebank'), app)
            on_the_app = [resource for resource in predicted
                          if resource['id'].startswith(app + '/providers/Microsoft.Authorization/roleAssignments/')]
            self.assertEqual(len(on_the_app), 1)
            # And without the app: the fourteen, whatever the app's values are.
            code, said, predicted = snapshot(folder, {**FOUNDATION_INPUTS, 'deployApp': False, 'imageTag': '',
                                                      'alertEmail': ''})
            self.assertEqual(code, 0, said)
            self.assertEqual(len(predicted), 14)
            self.assertNotIn(APP, [resource['type'] for resource in predicted])

    def test_deploy_app_true_is_refused_with_a_tag_that_is_not_40_characters_or_no_address(self):
        # Offline, as above. The seven secrets cannot be tried this way: like a what-if, this
        # evaluation works out no secure value. Their checks are read from the compiled template
        # above, and a local deployment saw each one refuse (README.md, "Checking these files").
        with tempfile.TemporaryDirectory() as folder:
            copy_templates(folder)
            for name, value, why in (('imageTag', TAG[:39], "greater than or equal to '40'"),
                                     ('imageTag', TAG + 'a', "less than or equal to '40'"),
                                     ('alertEmail', '', "greater than or equal to '1'")):
                with self.subTest(name=name, length=len(value)):
                    code, said, predicted = snapshot(folder, {**APP_INPUTS, name: value})
                    self.assertNotEqual(code, 0, said)
                    self.assertIsNone(predicted)
                    self.assertIn(f"The provided value for the template parameter '{name}' is not valid. "
                                  f"Length of the value should be {why}", said)

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

    def test_the_environment_names_its_mode_on_an_api_version_that_has_the_property(self):
        # A request that names no mode was taken as Express on the demo's subscription and refused
        # (README.md, "Measured on Azure"). The property exists from this API version on.
        (environment,) = self.of_type('Microsoft.App/managedEnvironments')
        self.assertEqual(environment['apiVersion'], '2026-07-01')
        self.assertEqual(environment['properties'].get('environmentMode'), 'WorkloadProfiles')
        self.assertEqual(environment['properties']['workloadProfiles'],
                         [{'name': 'Consumption', 'workloadProfileType': 'Consumption'}])
        # The Bicep CLI has no types for that version: nothing but this test checks the names above.
        self.assertEqual(sorted(environment['properties']),
                         ['appLogsConfiguration', 'environmentMode', 'workloadProfiles'])
        # And the script that reads the environment back asks with the same version.
        self.assertIn(f"$EnvironmentApi = '{environment['apiVersion']}'",
                      (HERE / 'secrets.ps1').read_text(encoding='utf-8'))

    def test_one_warning_is_silenced_on_one_line_and_any_other_still_reaches_standard_error(self):
        # setUpClass fails on anything Bicep writes to standard error. The template silences one
        # code on one line (no types for the environment's API version). Both halves are seen
        # firing here, on copies: without that line the warning is back, and with it an unused
        # parameter is still reported, by build and by lint.
        source = (HERE / 'main.bicep').read_text(encoding='utf-8')
        self.assertEqual([line for line in source.splitlines() if line.lstrip().startswith('#')],
                         ['#disable-next-line BCP081'])
        mutants = {'BCP081': source.replace('#disable-next-line BCP081\n', ''),
                   'no-unused-params': source + "\nparam nobodyReadsThis string = ''\n"}
        self.assertNotIn('#disable', mutants['BCP081'])
        for code, text in mutants.items():
            for verb in (['build', '--stdout'], ['lint']):
                with self.subTest(code=code, verb=verb[0]), tempfile.TemporaryDirectory() as folder:
                    copy_templates(folder, main=text)
                    result = subprocess.run([BICEP, verb[0], str(pathlib.Path(folder) / 'main.bicep'), *verb[1:]],
                                            capture_output=True, text=True, timeout=300)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn(f'Warning {code}', result.stderr)

    def test_the_check_of_the_app_inputs_silences_one_code_and_any_other_still_reaches_standard_error(self):
        # Its parameters exist to be checked by Azure and nothing in the file reads them, so the
        # file silences that one code, from that line on. Seen firing on copies: without the line
        # the warning is back, through main.bicep too; with it an unused variable is reported.
        source = (HERE / 'app-inputs.bicep').read_text(encoding='utf-8')
        self.assertEqual([line for line in source.splitlines() if line.lstrip().startswith('#')],
                         ['#disable-diagnostics no-unused-params'])
        mutants = {'no-unused-params': source.replace('#disable-diagnostics no-unused-params\n', ''),
                   'no-unused-vars': source + "\nvar nobodyReadsThis = 1\n"}
        self.assertNotIn('#disable', mutants['no-unused-params'])
        for code, text in mutants.items():
            for verb, name in ((['build', '--stdout'], 'app-inputs'), (['lint'], 'app-inputs'), (['lint'], 'main')):
                with self.subTest(code=code, verb=verb[0], file=name), tempfile.TemporaryDirectory() as folder:
                    copy_templates(folder, **{'app-inputs': text})
                    result = subprocess.run([BICEP, verb[0], str(pathlib.Path(folder) / f'{name}.bicep'), *verb[1:]],
                                            capture_output=True, text=True, timeout=300)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn(f'Warning {code}', result.stderr)

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
                                        'deploymentPrincipalId', 'appIdentityClientId', 'migrateIdentityClientId',
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
        self.assertEqual(sorted(module['name'] for module in self.of_type('Microsoft.Resources/deployments')),
                         [GUARD, 'azurebank-shape-definition'])
        (module,) = [module for module in self.of_type('Microsoft.Resources/deployments')
                     if module['name'] == 'azurebank-shape-definition']
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

    def test_a_job_is_started_by_hand_unless_it_is_named_for_a_schedule(self):
        # Two lists. The first holds for every job and stays as it was. The second names the jobs
        # that may run on a schedule: the definition by itself names none, main.bicep names one,
        # and the assignment hands over both.
        definition = self.compiled['guardrails']['resources'][0]['properties']
        self.assertEqual(definition['parameters'],
                         {'allowedJobTriggers': {'type': 'Array', 'defaultValue': ['Manual']},
                          'scheduledJobs': {'type': 'Array', 'defaultValue': []}})
        self.assertEqual({name: self.main['parameters'].get(name, {}).get('defaultValue')
                          for name in ('allowedJobTriggers', 'scheduledJobs')},
                         {'allowedJobTriggers': ['Manual'], 'scheduledJobs': ['azurebank-pool']})
        (assignment,) = self.of_type('Microsoft.Authorization/policyAssignments')
        self.assertEqual(assignment['properties']['parameters'],
                         {'allowedJobTriggers': {'value': "[parameters('allowedJobTriggers')]"},
                          'scheduledJobs': {'value': "[parameters('scheduledJobs')]"}})

    def test_the_policy_is_named_for_what_it_refuses_and_the_removal_finds_it(self):
        # The name is what a refusal is expected to show (not yet read on Azure), so the definition
        # and its assignment carry the same one. The runbook's removal looks for the words the name
        # opens with and not for the whole of it: the name it had before this one opens with them too.
        opens_with = 'AzureBank: one small replica'
        name = opens_with + ', manual jobs, the pool job scheduled'
        definition = self.compiled['guardrails']['resources'][0]['properties']
        (assignment,) = self.of_type('Microsoft.Authorization/policyAssignments')
        self.assertEqual((definition['displayName'], assignment['properties']['displayName']), (name, name))
        # The description says what is refused, and so it says the exception too.
        self.assertIn('a job with another trigger, unless it is named for a schedule', definition['description'])
        runbook = (HERE / 'README.md').read_text(encoding='utf-8')
        self.assertNotIn(f"-eq '{opens_with}, manual jobs'", runbook)
        self.assertIn(f"Where-Object {{ $_.policyType -eq 'Custom' -and $_.displayName -like '{opens_with}*' }} |",
                      runbook)

    def test_every_alert_rule_notifies_one_action_group_and_stops_nothing(self):
        # Four rules are written. Three are built, and the fourth when it is asked for (below).
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

    def test_the_alert_on_the_log_volume_watches_the_workspace_and_is_left_out_unless_asked_for(self):
        (alerts,) = self.of_type('Microsoft.Insights/metricAlerts')
        rules = self.main['variables']['alerts']
        self.assertEqual([rule['onLogs'] for rule in rules], [False, False, False, True])
        self.assertEqual({key: rules[3][key] for key in ('name', 'threshold', 'every')},
                         {'name': 'azurebank-log-volume', 'threshold': 50000, 'every': 'PT15M'})
        self.assertEqual(alerts['condition'], ALERTS_CONDITION)
        # True until 2026-10-03, when the runbook's step 20 found that the rule's metric reported
        # nothing for an hour in which the workspace ingested rows (README.md, "Measured on Azure").
        self.assertIs(self.main['parameters']['logVolumeAlert']['defaultValue'], False)
        workspace = "resourceId('Microsoft.OperationalInsights/workspaces', 'azurebank-logs')"
        app = "resourceId('Microsoft.App/containerApps', 'azurebank')"
        self.assertEqual(alerts['properties']['scopes'],
                         [f"[if(variables('alerts')[copyIndex()].onLogs, {workspace}, {app})]"])
        (criterion,) = alerts['properties']['criteria']['allOf']
        self.assertEqual(criterion['metricNamespace'],
                         "[if(variables('alerts')[copyIndex()].onLogs, 'Microsoft.OperationalInsights/workspaces', "
                         "'Microsoft.App/containerApps')]")

    def test_three_alerts_are_built_and_the_fourth_as_it_was_when_it_is_asked_for(self):
        # Offline, as a what-if works the template out. By default: the three rules on the app.
        # With logVolumeAlert=true: the rule on the workspace as well, with the properties it was
        # built with while the default was true. Without the logs there is no workspace to watch,
        # whatever is asked.
        on_the_app = ['azurebank-bytes-out', 'azurebank-replica-time', 'azurebank-requests']
        group = f'/subscriptions/{SNAPSHOT_CONTEXT[1]}/resourceGroups/azurebank-demo/providers'
        with tempfile.TemporaryDirectory() as folder:
            copy_templates(folder)
            built = {}
            for case, values in (('by default', {}), ('asked for', {'logVolumeAlert': True}),
                                 ('asked for, with no logs', {'logVolumeAlert': True, 'keepLogs': False})):
                code, said, predicted = snapshot(folder, {**APP_INPUTS, **values})
                self.assertEqual(code, 0, said)
                built[case] = {resource['name']: resource for resource in predicted
                               if resource['type'] == 'Microsoft.Insights/metricAlerts'}
        self.assertEqual({case: sorted(rules) for case, rules in built.items()},
                         {'by default': on_the_app, 'asked for': sorted([*on_the_app, 'azurebank-log-volume']),
                          'asked for, with no logs': on_the_app})
        for name in on_the_app:
            self.assertEqual(built['by default'][name], built['asked for'][name], name)
            self.assertEqual(built['by default'][name]['properties']['scopes'], [f'{group}/{APP}/azurebank'])
        self.assertEqual(built['asked for']['azurebank-log-volume']['properties'], {
            'description': 'More than 50,000 log lines in one hour.', 'severity': 2, 'enabled': True,
            'scopes': [f'{group}/Microsoft.OperationalInsights/workspaces/azurebank-logs'],
            'evaluationFrequency': 'PT15M', 'windowSize': 'PT1H', 'autoMitigate': True,
            'criteria': {'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria', 'allOf': [{
                'name': 'threshold', 'criterionType': 'StaticThresholdCriterion',
                'metricNamespace': 'Microsoft.OperationalInsights/workspaces', 'metricName': 'Ingestion Volume',
                'operator': 'GreaterThan', 'threshold': 50000, 'timeAggregation': 'Count'}]},
            'actions': [{'actionGroupId': f'{group}/Microsoft.Insights/actionGroups/azurebank-owner'}]})

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
            # What the template's own check asks for when deployApp is true.
            self.assertLessEqual(set(self.compiled['app-inputs']['parameters']) | {'deployApp'}, written)
            # And everything secure in the template is something the script writes: no secret is typed by hand.
            secure = {name for name, entry in self.main['parameters'].items()
                      if entry['type'].lower() == 'securestring'}
            self.assertLessEqual(secure, written)
        finally:
            case.doCleanups()


if __name__ == '__main__':
    unittest.main()
