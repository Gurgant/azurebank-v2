"""Offline tests of the two PowerShell scripts, of the users file and of the three templates.

The scripts run for real, in PowerShell 7, against a stand-in for the Azure CLI and a stand-in for
sqlcmd: they touch neither Azure, nor a database, nor the real parameter folder. The users file is
read as text: what a SQL Server does with it is checked on a real engine (README.md). The
templates are compiled by the Bicep CLI and the compiled JSON is read; the CLI's `snapshot` also
works them out offline with given values, as a what-if does. What Azure itself answers is
checked on the first deployment (README.md).

The deploy script is imported, and nothing of it is run against Azure: its names for the app
and the jobs, and the shape checks it makes before it moves anything, are asked of what the
templates work out, so that a name typed in both cannot drift on one side. Its own decisions
are tested in test_deploy.py. One source of the backend is read as text, for the names of the
settings the template writes.

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

import deploy

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
        # What the deployed group says of the phone, as FAKE_AZ_PUSH has it. By default nothing:
        # the answer does not hold the property. "none" is an empty list and "null" a null; what
        # Azure answers for a group with no such receiver has not been read. Anything else is the
        # accounts of its receivers of the Azure mobile app, separated by commas. The mailboxes it
        # writes to are the ones FAKE_AZ_MAIL has, separated by commas: by default one, as the
        # template writes one.
        mailboxes = os.environ.get('FAKE_AZ_MAIL', 'deployed.alerts@example.invalid').split(',')
        group = {'emailReceivers': [{'name': 'owner' + str(number or ''), 'emailAddress': mailbox}
                                    for number, mailbox in enumerate(mailboxes)]}
        push = os.environ.get('FAKE_AZ_PUSH', 'absent')
        if push != 'absent':
            accounts = [] if push in ('none', 'null') else push.split(',')
            group['azureAppPushReceivers'] = None if push == 'null' else [
                {'name': 'phone-' + str(number), 'emailAddress': account} for number, account in enumerate(accounts)]
        out({'properties': group})
    if 'containerApps/azurebank/listSecrets' in url:
        names = ['app-connection', 'jwt-secret', 'idempotency-hash-key', 'stepup-binding-key',
                 'service-key', 'audit-chain-key', 'audit-anchor-key', 'pin-pepper']
        if state == 'key-missing':
            names.remove('pin-pepper')
        # The demo's client key is held by an app that was deployed with it. 'deployed' is the app
        # as it was before the key existed, and 'demo-key-missing' a demo that lost it.
        if state in ('deployed-demo', 'deployed-demo-off', 'demo-disagrees'):
            names.append('demo-client-key')
        out({'value': [{'name': n, 'value': live[n]} for n in names]})
    if 'containerApps/azurebank?' in url:
        # What the bff and the api container say about the demo, by state, or as FAKE_AZ_DEMO has
        # it: a value for Demo__Enabled, "-" for a container that does not carry the setting,
        # "nothing" for one with no settings at all, "secret" for the setting as a reference,
        # "twice" for the setting carried twice.
        by_state = {'deployed-demo': 'true,true', 'demo-key-missing': 'true,true',
                    'deployed-demo-off': 'false,false', 'demo-disagrees': 'true,false'}
        says = os.environ.get('FAKE_AZ_DEMO', by_state.get(state, '-,-')).split(',')
        # What the bff container says of the proxies it believes, as FAKE_AZ_NETWORKS has it:
        # settings separated by "|", each NAME=VALUE for a plain value, or NAME alone for a
        # reference to a secret. By default none, as the template writes none.
        proxies = [{'name': pair.partition('=')[0], 'value': pair.partition('=')[2]} if '=' in pair
                   else {'name': pair, 'secretRef': 'a-secret'}
                   for pair in os.environ.get('FAKE_AZ_NETWORKS', '').split('|') if pair]
        def container(name, own, flag, more=()):
            settings = [{'name': 'ASPNETCORE_ENVIRONMENT', 'value': 'Production'}, own, *more]
            if flag == 'secret':
                settings.append({'name': 'Demo__Enabled', 'secretRef': 'a-secret'})
            elif flag == 'twice':
                # Off, then on: which of the two a container reads is not this script's to guess.
                settings += [{'name': 'Demo__Enabled', 'value': value} for value in ('false', 'true')]
            elif flag not in ('-', 'nothing'):
                settings.append({'name': 'Demo__Enabled', 'value': flag})
            described = {'name': name, 'image': 'ghcr.io/gurgant/azurebank-' + name + ':' + live['tag']}
            return described if flag == 'nothing' else dict(described, env=settings)
        out({'properties': {'template': {'containers': [
            container('bff', {'name': 'ServiceCredential__BffKey', 'secretRef': 'service-key'}, says[0], proxies),
            container('api', {'name': 'Security__PinPepper', 'secretRef': 'pin-pepper'}, says[1])]}}})
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
    'pin-pepper': 'live-pepper-0000000', 'demo-client-key': 'live-client-key-0000000000000000',
}
# The application secrets the template takes and the script writes. No database credential is among them.
SEVEN = ['jwtSecret', 'idempotencyHashKey', 'stepUpBindingKey', 'serviceCredentialBffKey', 'auditChainKey',
         'auditAnchorKey', 'securityPinPepper']
# The key the demo hashes a visitor's address with before it stores it: an eighth secret, beside the seven.
CLIENT_KEY = 'demoClientKeySecret'
SECURE = [*SEVEN, CLIENT_KEY]
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
        for name in ('AZUREBANK_ALERT_EMAIL', 'AZUREBANK_ALERT_PUSH_ACCOUNT', 'AZUREBANK_PROXY_NETWORKS',
                     'SQLCMDUSER', 'SQLCMDPASSWORD', 'SQLCMDINI', *VARIABLES):
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
        # CONTROL: green before this change. The eighth generated value joins the exception below.
        for state in ('empty', 'deployed'):
            with self.subTest(state=state):
                result = self.secrets('-Action', 'New', '-DeployApp', *(['-ImageTag', TAG] if state == 'empty' else []),
                                      state=state)
                self.assertEqual(result.returncode, 0, result.stderr)
                written = (self.folder / 'parameters.json').read_text(encoding='utf-8')
                for word in ('password', 'connection', 'Server='):
                    self.assertNotIn(word.lower(), written.lower())
                # Three letters can occur by chance in a random key, and did ("...ulsqlw="): "sql"
                # is looked for in every name and in every value but the eight generated ones.
                parameters = json.loads(written)['parameters']
                self.assertNotIn('sql', json.dumps({name: None if name in SECURE else entry
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

    def test_first_app_file_generates_eight_distinct_values_and_prints_none(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG)
        self.assertEqual(result.returncode, 0, result.stderr)
        values = self.parameters()
        # No app is deployed, so nothing remembers whether the demo is on: the file does not say.
        self.assertEqual(sorted(values), sorted(['entraAdminObjectId', 'entraAdminLogin', 'deployApp', 'imageTag',
                                                 'alertEmail', *SECURE]))
        self.assertEqual(values['imageTag'], TAG)
        self.assertIs(values['deployApp'], True)
        self.assertEqual(len({values[name] for name in SECURE}), 8)
        for name in SECURE:
            self.assertGreaterEqual(len(values[name]), 44, f'{name}: at least 32 random bytes')
            self.assertIn(f'{name}: generated', result.stderr)
        self.assertEqual(result.stderr.count(': generated'), 8)
        self.assertIn("demo: not written, the template's default applies", result.stderr)
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
        # Until 2026-10-06 this test held that the deployed group is then not asked at all. It is
        # asked once now, for the phone's account, which this run does not name: the mailbox
        # written is the argument's all the same (above), and with both named nothing is asked
        # (test_the_argument_names_the_phones_account_before_anything_else_does).
        self.assertEqual(len([call for call in self.calls() if 'actionGroups' in ' '.join(call)]), 1)
        self.assertNotIn('alertPushAccount', self.parameters())
        # "Not asked at all" also held that nothing the group says of its mailboxes can stop a run
        # that names one, and one read does not hold it. A group that writes to two addresses
        # stops a run that names none, and tells it to pass -AlertEmail: passed, the run goes on
        # with the argument's mailbox, and keeps the phone that group notifies.
        # CONTROL: green as written. Seen red twice: with the group's mailboxes counted before the
        # argument is looked at, and with the first of two kept where the run names none.
        two = 'first.alerts@example.invalid,second.alerts@example.invalid'
        (self.folder / 'parameters.json').unlink()
        result = self.secrets('-Action', 'New', '-DeployApp', state='deployed', FAKE_AZ_MAIL=two,
                              FAKE_AZ_PUSH='deployed.phone@example.invalid')
        self.assertIn('The deployed alerts write to 2 addresses, not one. Pass -AlertEmail. Nothing was written.',
                      self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        result = self.secrets('-Action', 'New', '-DeployApp', '-AlertEmail', 'given@example.invalid',
                              state='deployed', FAKE_AZ_MAIL=two, FAKE_AZ_PUSH='deployed.phone@example.invalid')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.parameters()['alertEmail'], self.parameters().get('alertPushAccount')),
                         ('given@example.invalid', 'deployed.phone@example.invalid'))
        self.assertNotIn('alerts@', result.stderr)

    def test_a_mailbox_that_is_not_an_address_is_refused(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, '-AlertEmail', 'not an address')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertEqual(self.calls(), [])

    # The account of the Azure mobile app that the alerts also notify. It is found as the mailbox
    # is, but for the last place: an account nobody names is no account, and never the signed-in one.
    PHONE = 'alertPushAccount'
    NO_PHONE = "alertPushAccount: not written, the template's default applies"

    def test_an_account_for_the_phone_that_nobody_names_is_not_an_error_and_is_not_written(self):
        # Before any app, and against a deployed group that says nothing of a phone, in the three
        # ways an answer can say nothing: no such property, an empty list, a null.
        for state, push in (('empty', None), ('deployed', None), ('deployed', 'none'), ('deployed', 'null')):
            with self.subTest(state=state, push=push):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                self.log.unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *(['-ImageTag', TAG] if state == 'empty' else []),
                                      state=state, **({'FAKE_AZ_PUSH': push} if push else {}))
                self.assertEqual(result.returncode, 0, result.stderr)
                values = self.parameters()
                self.assertNotIn(self.PHONE, values)
                self.assertIn(self.NO_PHONE, result.stderr)
                self.assertEqual(result.stderr.count(f'{self.PHONE}: '), 1)
                # The mailbox is still found, where it was found before: the account's own, or the group's.
                self.assertEqual(values['alertEmail'], 'owner.mailbox@example.invalid' if state == 'empty'
                                 else 'deployed.alerts@example.invalid')
                # One read of the group serves both, and with no group deployed there is none.
                self.assertEqual(len([call for call in self.calls() if 'actionGroups' in ' '.join(call)]),
                                 0 if state == 'empty' else 1)
                self.assert_nothing_leaked(result, values)
        # CONTROL: green as written (the script already took an empty argument for none). The
        # argument given as empty is no account either, with -DeployApp and without it: the
        # runbook's step 25 passes a variable that is empty when the owner goes without the phone.
        # Seen red with the argument refused when it is empty.
        for more in (['-DeployApp'], []):
            with self.subTest(empty_argument_with=more):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', *more, '-AlertPushAccount', '', state='deployed')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertNotIn(self.PHONE, self.parameters())
                self.assertEqual(result.stderr.count(self.NO_PHONE), 1 if more else 0)

    def test_the_argument_names_the_phones_account_before_anything_else_does(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-AlertPushAccount', 'given.phone@example.invalid',
                              state='deployed', AZUREBANK_ALERT_PUSH_ACCOUNT='phone.elsewhere@example.invalid',
                              FAKE_AZ_PUSH='deployed.phone@example.invalid')
        self.assertEqual(result.returncode, 0, result.stderr)
        values = self.parameters()
        self.assertEqual(values.get(self.PHONE), 'given.phone@example.invalid')
        self.assertIn(f'{self.PHONE}: from -AlertPushAccount', result.stderr)
        self.assertEqual(result.stderr.count(f'{self.PHONE}: '), 1)
        self.assertNotIn('phone@', result.stderr + self.log.read_text(encoding='utf-8'))
        self.assertNotIn('phone.elsewhere', result.stderr + self.log.read_text(encoding='utf-8'))
        # The mailbox was not named, so the group was asked for it, once; with both named it is not asked.
        self.assertEqual(values['alertEmail'], 'deployed.alerts@example.invalid')
        self.assertEqual(len([call for call in self.calls() if 'actionGroups' in ' '.join(call)]), 1)
        self.log.unlink()
        result = self.secrets('-Action', 'New', '-DeployApp', '-AlertPushAccount', 'given.phone@example.invalid',
                              '-AlertEmail', 'given@example.invalid', state='deployed',
                              FAKE_AZ_PUSH='deployed.phone@example.invalid')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.parameters()['alertEmail'], self.parameters().get(self.PHONE)),
                         ('given@example.invalid', 'given.phone@example.invalid'))
        self.assertEqual([call for call in self.calls() if 'actionGroups' in ' '.join(call)], [])
        self.assert_nothing_leaked(result, self.parameters())

    def test_the_variable_names_the_phones_account_and_the_report_does_not_show_it(self):
        # Before any app, and against a deployed group that notifies another account: the variable wins.
        for state, push in (('empty', None), ('deployed', 'deployed.phone@example.invalid')):
            with self.subTest(state=state):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *(['-ImageTag', TAG] if state == 'empty' else []),
                                      state=state, AZUREBANK_ALERT_PUSH_ACCOUNT='phone.elsewhere@example.invalid',
                                      **({'FAKE_AZ_PUSH': push} if push else {}))
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.parameters().get(self.PHONE), 'phone.elsewhere@example.invalid')
                self.assertIn(f'{self.PHONE}: from AZUREBANK_ALERT_PUSH_ACCOUNT', result.stderr)
                self.assertEqual(result.stderr.count(f'{self.PHONE}: '), 1)
                self.assertNotIn('phone.elsewhere', result.stderr + self.log.read_text(encoding='utf-8'))
                self.assertNotIn('deployed.phone', result.stderr)

    def test_the_phones_account_the_deployed_alerts_notify_is_kept_when_none_is_named(self):
        # With nothing named, and with the mailbox named and the phone not: a run that names one
        # of the two must not forget the other.
        for more, mailbox in (([], 'deployed.alerts@example.invalid'),
                              (['-AlertEmail', 'given@example.invalid'], 'given@example.invalid')):
            with self.subTest(more=more):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                self.log.unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *more, state='deployed',
                                      FAKE_AZ_PUSH='deployed.phone@example.invalid')
                self.assertEqual(result.returncode, 0, result.stderr)
                values = self.parameters()
                self.assertEqual((values['alertEmail'], values.get(self.PHONE)),
                                 (mailbox, 'deployed.phone@example.invalid'))
                self.assertIn(f'{self.PHONE}: kept from the deployed resource', result.stderr)
                self.assertEqual(result.stderr.count(f'{self.PHONE}: '), 1)
                self.assertNotIn('deployed.phone', result.stderr)
                self.assertEqual(len([call for call in self.calls() if 'actionGroups' in ' '.join(call)]), 1)
                self.assert_nothing_leaked(result, values)

    def test_deployed_alerts_that_notify_two_phones_stop_the_run_unless_one_is_named(self):
        # The template writes one such receiver. Two were put there by hand, and no side is chosen.
        two = 'first.phone@example.invalid,second.phone@example.invalid'
        result = self.secrets('-Action', 'New', '-DeployApp', state='deployed', FAKE_AZ_PUSH=two)
        self.assertIn('The deployed alerts notify 2 accounts of the Azure mobile app, not one. '
                      'Pass -AlertPushAccount. Nothing was written.', self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertNotIn('phone@', result.stderr)
        self.assertEqual(result.stdout, '')
        result = self.secrets('-Action', 'New', '-DeployApp', '-AlertPushAccount', 'given.phone@example.invalid',
                              state='deployed', FAKE_AZ_PUSH=two)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters().get(self.PHONE), 'given.phone@example.invalid')

    def test_an_account_for_the_phone_that_is_not_an_address_is_refused(self):
        # As an argument, before Azure is asked anything; from the variable, before anything is written.
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, '-AlertPushAccount', 'not an address')
        self.assertIn('-AlertPushAccount is not an e-mail address. Nothing was written.', self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertEqual(self.calls(), [])
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG,
                              AZUREBANK_ALERT_PUSH_ACCOUNT='planted value')
        self.assertIn('The account for the Azure mobile app is not an e-mail address. Pass -AlertPushAccount or '
                      'set AZUREBANK_ALERT_PUSH_ACCOUNT. Nothing was written.', self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertNotIn('planted', result.stderr)
        self.assertEqual(result.stdout, '')

    def test_an_account_for_the_phone_without_deploy_app_is_refused_before_azure_is_asked_anything(self):
        # The group that carries it is built with the app: a file for the foundation alone cannot
        # carry it, and an argument that is dropped in silence would be taken for one that was kept.
        result = self.secrets('-Action', 'New', '-AlertPushAccount', 'given.phone@example.invalid', state='deployed')
        self.assertIn('-AlertPushAccount needs -DeployApp. Nothing was written.', self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls(), [])
        self.assertFalse(self.folder.exists())
        self.assertEqual(result.stdout, '')
        # The variable is the terminal's and not this run's: the foundation's file leaves it out.
        result = self.secrets('-Action', 'New', state='deployed',
                              AZUREBANK_ALERT_PUSH_ACCOUNT='phone.elsewhere@example.invalid')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn(self.PHONE, self.parameters())
        self.assertNotIn(self.PHONE, result.stderr)

    # The networks of proxies the app believes: the bff container's settings
    # ForwardedHeaders__KnownIPNetworks__0 and on. Found as the phone's account is: the argument,
    # then the variable, then what the deployed app holds; none of the three is no value, and
    # not an error. No line of the report, and no call, holds a network. The ranges below are
    # the ones kept for documentation: none is a network of any deployment.
    NETWORKS = 'proxyNetworks'
    NO_NETWORKS = "proxyNetworks: not written, the template's default applies"
    SETTING = 'ForwardedHeaders__KnownIPNetworks__'
    NOT_TAKEN = ('is not a network the app takes (address/prefix-length, written as it is printed, no wider '
                 'than /8). Nothing was written.')
    BY_HAND = ('The bff container of the deployed app carries a forwarded-headers setting this template never '
               'writes. Pass -ProxyNetworks. Nothing was written.')

    def held(self, *networks):
        """What FAKE_AZ_NETWORKS takes for a bff container that holds these networks, as the template writes them."""
        return '|'.join(f'{self.SETTING}{number}={network}' for number, network in enumerate(networks))

    def assert_no_network_is_shown(self, result, *more):
        said = result.stderr + result.stdout + (self.log.read_text(encoding='utf-8') if self.log.exists() else '')
        for part in ('192.0.2', '198.51.100', '203.0.113', '2001:db8', *more):
            self.assertNotIn(part, said)

    def test_networks_that_nobody_names_are_not_an_error_and_are_not_written(self):
        # Before any app, and against a deployed app whose bff container holds none.
        for state in ('empty', 'deployed'):
            with self.subTest(state=state):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *(['-ImageTag', TAG] if state == 'empty' else []),
                                      state=state)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertNotIn(self.NETWORKS, self.parameters())
                self.assertIn(self.NO_NETWORKS, result.stderr)
                self.assertEqual(result.stderr.count(f'{self.NETWORKS}: '), 1)
        # The argument given as empty names none, with -DeployApp and without it: a step of the
        # runbook passes a variable that is empty until the operator has measured a range. With
        # it empty the deployed app is still what is read.
        for more, deployed, expected in ((['-DeployApp'], '', None), ([], '', None),
                                         (['-DeployApp'], self.held('192.0.2.0/24'), ['192.0.2.0/24'])):
            with self.subTest(empty_argument_with=more, deployed=bool(deployed)):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', *more, '-ProxyNetworks', '', state='deployed',
                                      FAKE_AZ_NETWORKS=deployed)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.parameters().get(self.NETWORKS), expected)
                self.assertEqual(result.stderr.count(self.NO_NETWORKS), 1 if more and not deployed else 0)

    def test_the_argument_names_the_networks_before_anything_else_does(self):
        result = self.secrets('-Action', 'New', '-DeployApp', '-ProxyNetworks', '192.0.2.0/24, 2001:db8:7::/48',
                              state='deployed', AZUREBANK_PROXY_NETWORKS='198.51.100.0/24',
                              FAKE_AZ_NETWORKS=self.held('203.0.113.0/24'))
        self.assertEqual(result.returncode, 0, result.stderr)
        values = self.parameters()
        # A list, in the order given, the blanks around a network dropped.
        self.assertEqual(values.get(self.NETWORKS), ['192.0.2.0/24', '2001:db8:7::/48'])
        self.assertIn(f'{self.NETWORKS}: from -ProxyNetworks', result.stderr)
        self.assertEqual(result.stderr.count(f'{self.NETWORKS}: '), 1)
        self.assert_no_network_is_shown(result)
        self.assert_nothing_leaked(result, values)
        # One network is a list of one, not a text: the template takes an array.
        (self.folder / 'parameters.json').unlink()
        result = self.secrets('-Action', 'New', '-DeployApp', '-ProxyNetworks', '192.0.2.0/24', state='deployed')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters().get(self.NETWORKS), ['192.0.2.0/24'])

    def test_the_variable_names_the_networks_and_the_report_does_not_show_them(self):
        # Before any app, and against a deployed app that holds another network: the variable wins.
        for state, deployed in (('empty', ''), ('deployed', self.held('203.0.113.0/24'))):
            with self.subTest(state=state):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *(['-ImageTag', TAG] if state == 'empty' else []),
                                      state=state, AZUREBANK_PROXY_NETWORKS='198.51.100.0/24,2001:db8:7::/48',
                                      FAKE_AZ_NETWORKS=deployed)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.parameters().get(self.NETWORKS), ['198.51.100.0/24', '2001:db8:7::/48'])
                self.assertIn(f'{self.NETWORKS}: from AZUREBANK_PROXY_NETWORKS', result.stderr)
                self.assertEqual(result.stderr.count(f'{self.NETWORKS}: '), 1)
                self.assert_no_network_is_shown(result)

    def test_the_networks_the_deployed_app_believes_are_kept_when_none_is_named(self):
        # In the order of their numbers, whatever order the answer lists the settings in, and
        # whatever the case of a name: .NET reads a setting's name whatever its case.
        in_order = self.held('192.0.2.0/24', '2001:db8:7::/48', '198.51.100.0/24')
        for case, deployed in (('as the template writes them', in_order),
                               ('listed last first', '|'.join(reversed(in_order.split('|')))),
                               ('a name in lower case',
                                in_order.replace(f'{self.SETTING}1', f'{self.SETTING}1'.lower()))):
            with self.subTest(case=case):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', state='deployed', FAKE_AZ_NETWORKS=deployed)
                self.assertEqual(result.returncode, 0, result.stderr)
                values = self.parameters()
                self.assertEqual(values.get(self.NETWORKS), ['192.0.2.0/24', '2001:db8:7::/48', '198.51.100.0/24'])
                self.assertIn(f'{self.NETWORKS}: kept from the deployed resource', result.stderr)
                self.assertEqual(result.stderr.count(f'{self.NETWORKS}: '), 1)
                self.assert_no_network_is_shown(result)
                self.assert_nothing_leaked(result, values)

    def test_the_word_none_writes_an_empty_list_whatever_is_deployed(self):
        # The way back: an empty list, written, so that the run takes the settings out. Without
        # it the script would keep what the deployed app holds, and nothing could unset them.
        deployed = self.held('203.0.113.0/24')
        for case, arguments, variable, said in (
                ('the argument', ['-ProxyNetworks', 'none'], '198.51.100.0/24', 'none, asked for with -ProxyNetworks'),
                ('the variable', [], 'none', 'none, asked for with AZUREBANK_PROXY_NETWORKS')):
            with self.subTest(case=case):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *arguments, state='deployed',
                                      AZUREBANK_PROXY_NETWORKS=variable, FAKE_AZ_NETWORKS=deployed)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.parameters().get(self.NETWORKS, 'not written'), [])
                self.assertIn(f'{self.NETWORKS}: {said}', result.stderr)
                self.assertEqual(result.stderr.count(f'{self.NETWORKS}: '), 1)
                self.assert_no_network_is_shown(result)
        # Only the word itself, in lower case: anything else is read as a list of networks.
        (self.folder / 'parameters.json').unlink()
        result = self.secrets('-Action', 'New', '-DeployApp', '-ProxyNetworks', 'None', state='deployed')
        self.assertIn(f'-ProxyNetworks: entry 1 of 1 {self.NOT_TAKEN}', self.said(result))
        self.assertFalse((self.folder / 'parameters.json').exists())

    def test_an_entry_the_app_would_not_take_for_a_network_is_refused_and_not_shown(self):
        # As an argument, before Azure is asked anything; from the variable and from the deployed
        # app, before anything is written. Named by its place in the list, never by what it holds.
        not_taken = (('0.0.0.0/0', '1 of 1'), ('192.0.2.0/24,planted-value', '2 of 2'), ('192.0.2.1/24', '1 of 1'),
                     ('192.0.2.0/24,', '2 of 2'), ('192.0.2.0/24,,198.51.100.0/24', '2 of 3'))
        for given, place in not_taken:
            with self.subTest(given=given, by='the argument'):
                result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, '-ProxyNetworks', given)
                self.assertIn(f'-ProxyNetworks: entry {place} {self.NOT_TAKEN}', self.said(result))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.calls(), [])
                self.assertFalse(self.folder.exists())
                self.assert_no_network_is_shown(result, 'planted', '0.0.0.0')
        for given, place in not_taken:
            with self.subTest(given=given, by='the variable'):
                result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, AZUREBANK_PROXY_NETWORKS=given)
                self.assertIn(f'AZUREBANK_PROXY_NETWORKS: entry {place} {self.NOT_TAKEN}', self.said(result))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((self.folder / 'parameters.json').exists())
                self.assert_no_network_is_shown(result, 'planted', '0.0.0.0')
                self.assertEqual(result.stdout, '')
        # The deployed app: a network somebody wrote there by hand, well numbered and not one
        # the app takes. (An app that held it would not have started: this is a read of settings.)
        result = self.secrets('-Action', 'New', '-DeployApp', state='deployed',
                              FAKE_AZ_NETWORKS=self.held('192.0.2.0/24', '0.0.0.0/0'))
        self.assertIn('The bff container of the deployed app: entry 2 of 2 is not a network the app takes. '
                      'Pass -ProxyNetworks. Nothing was written.', self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assert_no_network_is_shown(result, '0.0.0.0')

    def test_networks_without_deploy_app_are_refused_before_azure_is_asked_anything(self):
        # They are a setting of the app's bff container: a file for the foundation alone cannot
        # carry them, and an argument dropped in silence would be taken for one that was kept.
        result = self.secrets('-Action', 'New', '-ProxyNetworks', '192.0.2.0/24', state='deployed')
        self.assertIn('-ProxyNetworks needs -DeployApp. Nothing was written.', self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls(), [])
        self.assertFalse(self.folder.exists())
        self.assertEqual(result.stdout, '')
        # The variable is the terminal's and not this run's: the foundation's file leaves it out,
        # and so it does what the deployed app holds.
        result = self.secrets('-Action', 'New', state='deployed', AZUREBANK_PROXY_NETWORKS='198.51.100.0/24',
                              FAKE_AZ_NETWORKS=self.held('203.0.113.0/24'))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn(self.NETWORKS, self.parameters())
        self.assertNotIn(self.NETWORKS, result.stderr)

    def test_a_forwarded_headers_setting_the_template_never_writes_stops_the_run_and_is_not_shown(self):
        # The template writes one setting a network, each once, as a plain value, numbered from
        # 0 with no gap, and nothing else under ForwardedHeaders__. Anything else on the bff
        # container was set by hand: it is not taken for a list, a run would take it out in
        # silence, and what it holds is not repeated.
        one = f'{self.SETTING}0=192.0.2.0/24'
        by_hand = {'a reference to a secret': f'{self.SETTING}0',
                   'a gap': f'{one}|{self.SETTING}2=198.51.100.0/24',
                   'numbered from 1': f'{self.SETTING}1=192.0.2.0/24',
                   'a number twice': f'{one}|{self.SETTING}0=198.51.100.0/24',
                   'a number twice, in two cases': f'{one}|{self.SETTING.lower()}0=198.51.100.0/24',
                   'a number with a leading zero': f'{self.SETTING}00=192.0.2.0/24',
                   'no number': f'{self.SETTING}first=192.0.2.0/24',
                   'an exact address beside a network': f'{one}|ForwardedHeaders__KnownProxies__0=203.0.113.9',
                   'an exact address alone': 'ForwardedHeaders__KnownProxies__0=203.0.113.9',
                   'a limit of hops': 'ForwardedHeaders__ForwardLimit=2'}
        for case, deployed in by_hand.items():
            with self.subTest(case=case):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', state='deployed', FAKE_AZ_NETWORKS=deployed)
                self.assertIn(self.BY_HAND, self.said(result))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((self.folder / 'parameters.json').exists())
                self.assert_no_network_is_shown(result)
                self.assertEqual(result.stdout, '')
        # A run that names the networks itself is not stopped by them: what it names is written,
        # and the run of the template writes the bff's settings whole.
        result = self.secrets('-Action', 'New', '-DeployApp', '-ProxyNetworks', '198.51.100.0/24', state='deployed',
                              FAKE_AZ_NETWORKS=by_hand['an exact address beside a network'])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.parameters().get(self.NETWORKS), ['198.51.100.0/24'])

    # What takes the script's own rule for a network out of its source and asks it a list of
    # texts, in one process: the function by its name, defined here as the script defines it.
    ASK_THE_RULE = textwrap.dedent('''
        param([string]$Script)
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
        $rule = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq 'Test-ProxyNetwork' }, $true)
        if (-not $rule) { throw 'The script has no function Test-ProxyNetwork.' }
        Invoke-Expression $rule.Extent.Text
        $answers = @($env:ASKED | ConvertFrom-Json | ForEach-Object { [bool](Test-ProxyNetwork $_) })
        ConvertTo-Json -InputObject $answers -Compress
    ''')
    # The rows of the app's own tests of its rule (ProxyOptionsValidatorTests), by the test that
    # holds them: the first text of each row is the entry.
    APP_TAKES = ('Validate_ANetworkInCidrForm_Succeeds',)
    APP_REFUSES = ('Validate_AnEntryThatIsNotANetwork_Fails', 'Validate_APrefixLengthOutOfRange_Fails',
                   'Validate_ANetworkThatTrustsEverybody_Fails', 'Validate_ANetworkWiderThanSlash8_Fails',
                   'Validate_AnEntryTheFrameworkReadsAsAnotherText_Fails_AndSaysWhatItReads',
                   'Validate_AnIPv4MappedNetwork_Fails_AndAsksForTheIPv4One')

    def test_the_script_takes_for_a_network_what_the_app_takes_and_refuses_what_it_refuses(self):
        # Two rules, one in the app and one here, and the app's is the authority: a text this
        # script let through and the app refused would stop a new revision from starting. So the
        # script's rule is asked every entry the app's own tests hold, taken or refused. The
        # source of those tests is read as text; nothing of the backend is built or run.
        source = (HERE.parent / 'backend' / 'tests' / 'AzureBank.Bff.Tests' / 'OptionsValidatorTests.cs').read_text(
            encoding='utf-8')

        def rows(test):
            (block,) = re.findall(r'((?:[ \t]*(?://[^\n]*|\[(?:Theory|InlineData\([^\n]*\))\])\r?\n)+)'
                                  r'[ \t]*public void ' + test + r'\(', source)
            found = re.findall(r'\[InlineData\("([^"\\]*)"', block)
            self.assertEqual(len(found), block.count('[InlineData('), f'{test}: a row this test cannot read')
            return found
        taken = [entry for test in self.APP_TAKES for entry in rows(test)]
        refused = [entry for test in self.APP_REFUSES for entry in rows(test)]
        # That the search finds anything, and what is asked: one of each kind the app refuses.
        self.assertGreaterEqual(len(taken), 8)
        self.assertGreaterEqual(len(refused), 30)
        self.assertLessEqual({'0.0.0.0/0', '::/0', '10.0.0.1/8', '010.0.0.0/8', '10.0.0.0', '::ffff:10.0.0.0/104',
                              '10.0.0.0/33', 'fc00::/7', ''}, set(refused))
        harness = self.temp / 'ask-the-rule.ps1'
        harness.write_text(self.ASK_THE_RULE, encoding='utf-8')
        asked = [*taken, *refused]
        result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-File', str(harness),
                                 str(HERE / 'secrets.ps1')], capture_output=True, text=True, timeout=180,
                                env=dict(os.environ, ASKED=json.dumps(asked)))
        self.assertEqual(result.returncode, 0, result.stderr)
        answers = json.loads(result.stdout)
        self.assertEqual(len(answers), len(asked))
        self.assertEqual({entry: answer for entry, answer in zip(asked, answers)},
                         {**dict.fromkeys(taken, True), **dict.fromkeys(refused, False)})

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
        self.assertEqual(result.stderr.count(': kept from the earlier file'), 8)
        self.assertIn('jwtSecret: kept from the earlier file', result.stderr)
        self.assertIn(f'{CLIENT_KEY}: kept from the earlier file', result.stderr)
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
        # This app was deployed before the demo existed: its containers say nothing of the demo and
        # it holds no client key. That key is the one value generated against a deployed app, and
        # only because the app's demo is off; the demo stays as the app has it.
        self.assertIn(f'{CLIENT_KEY}: generated', result.stderr)
        self.assertEqual(result.stderr.count('generated'), 1)
        self.assertGreaterEqual(len(values[CLIENT_KEY]), 44)
        self.assertIs(values.get('demo'), False)
        self.assertIn('demo: kept from the deployed resource', result.stderr)
        self.assert_nothing_leaked(result, values)

    def test_the_client_key_is_kept_once_the_deployed_app_holds_it(self):
        # With the demo on, and with it off: an app that holds the key keeps it, and keeps its demo.
        for state, demo in (('deployed-demo', True), ('deployed-demo-off', False)):
            with self.subTest(state=state):
                result = self.secrets('-Action', 'New', '-DeployApp', state=state)
                self.assertEqual(result.returncode, 0, result.stderr)
                values = self.parameters()
                self.assertEqual(values.get(CLIENT_KEY), LIVE['demo-client-key'])
                self.assertIn(f'{CLIENT_KEY}: kept from the deployed resource', result.stderr)
                self.assertNotIn('generated', result.stderr)
                self.assertIs(values.get('demo'), demo)
                self.assertIn('demo: kept from the deployed resource', result.stderr)
                self.assert_nothing_leaked(result, values)

    def test_a_deployed_demo_without_its_client_key_stops_the_run(self):
        # The exception is for an app whose demo is off. A demo that is on has hashed addresses
        # with its key: a new one would be another key, so this is the seven's refusal, and
        # -DemoOn does not get past it.
        # CONTROL, the case with -DemoOn: green as written (the script already refused it). Seen
        # red with -DemoOn let through the exception: the run ended well, with a new key.
        for more in ([], ['-DemoOn']):
            with self.subTest(more=more):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *more, state='demo-key-missing')
                self.assertIn(f'{CLIENT_KEY} could not be read from the deployed resource. Nothing was written.',
                              self.said(result))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((self.folder / 'parameters.json').exists())
                self.assertNotIn('generated', result.stderr)

    def test_demo_on_is_written_when_asked_for(self):
        # Against the app as it is deployed today, and against one whose demo is already on.
        for state, key in (('deployed', 'generated'), ('deployed-demo', 'kept from the deployed resource')):
            with self.subTest(state=state):
                result = self.secrets('-Action', 'New', '-DeployApp', '-DemoOn', state=state)
                self.assertEqual(result.returncode, 0, result.stderr)
                values = self.parameters()
                self.assertIs(values.get('demo'), True)
                self.assertIn('demo: true, asked for with -DemoOn', result.stderr)
                self.assertEqual(result.stderr.count('demo: '), 1)
                self.assertIn(f'{CLIENT_KEY}: {key}', result.stderr)
                self.assert_nothing_leaked(result, values)

    def test_demo_on_without_deploy_app_is_refused_before_azure_is_asked_anything(self):
        result = self.secrets('-Action', 'New', '-DemoOn', state='deployed')
        self.assertIn('-DemoOn needs -DeployApp. Nothing was written.', self.said(result))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls(), [])
        self.assertFalse(self.folder.exists())
        self.assertEqual(result.stdout, '')

    def test_with_no_app_deployed_nothing_remembers_the_demo(self):
        # The app is the one thing that remembers the switch. Before the first app, or after the
        # app was deleted, the script has nothing to read and does not guess: without -DemoOn the
        # file says nothing and the template's default, off, applies.
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, state='foundation')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn('demo', self.parameters())
        self.assertIn("demo: not written, the template's default applies", result.stderr)
        self.assertEqual(result.stderr.count(': generated'), 8)
        (self.folder / 'parameters.json').unlink()
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, '-DemoOn', state='foundation')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIs(self.parameters().get('demo'), True)
        self.assertIn('demo: true, asked for with -DemoOn', result.stderr)
        self.assertNotIn('demo: not written', result.stderr)
        self.assertEqual(result.stderr.count(': generated'), 8)
        # CONTROL: green as written (the script already forgot it). Nor does the file an earlier
        # run left remember the switch: its eight secrets are kept, its demo is not. Seen red with
        # the demo taken from that file: the third run wrote it as on.
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, state='foundation')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn('demo', self.parameters())
        self.assertIn("demo: not written, the template's default applies", result.stderr)
        self.assertEqual(result.stderr.count(': kept from the earlier file'), 8)

    def test_containers_that_disagree_about_the_demo_stop_the_run(self):
        # On in one container and off in the other is an app nobody deployed from this folder:
        # the script does not choose a side, with -DemoOn or without it. A container that does not
        # carry the setting is off, as the app reads it.
        cases = (('demo-disagrees', None, []), ('deployed-demo', 'false,true', []), ('deployed-demo', 'true,-', []),
                 ('deployed-demo', '-,true', []), ('deployed-demo', 'nothing,true', []),
                 ('demo-disagrees', None, ['-DemoOn']))
        for state, says, more in cases:
            with self.subTest(state=state, says=says, more=more):
                # Each case answers for itself: a file a case before it left is not this one's.
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', *more, state=state,
                                      **({'FAKE_AZ_DEMO': says} if says else {}))
                self.assertIn('The two containers of the deployed app disagree about Demo__Enabled. '
                              'Nothing was written.', self.said(result))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((self.folder / 'parameters.json').exists())
                self.assertEqual(result.stdout, '')

    def test_a_container_without_the_setting_is_off_like_one_that_says_false(self):
        for says in ('false,-', '-,false', 'nothing,nothing'):
            with self.subTest(says=says):
                result = self.secrets('-Action', 'New', '-DeployApp', state='deployed-demo-off', FAKE_AZ_DEMO=says)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIs(self.parameters().get('demo'), False)
                self.assertIn('demo: kept from the deployed resource', result.stderr)

    def test_a_demo_setting_the_template_never_writes_stops_the_run_and_is_not_shown(self):
        # The template writes the text true or false, as a plain value, once. Anything else was
        # set by hand: it is not read as on or as off, and what it holds is not repeated.
        # CONTROL, the two cases with the setting twice: green as written (the script already
        # refused them). Seen red with the count of one taken out of the script: both runs ended
        # well, the demo "kept from the deployed resource".
        for says in ('planted-value,planted-value', 'True,True', 'true,secret', 'secret,secret', ',false',
                     'twice,twice', 'false,twice'):
            with self.subTest(says=says):
                (self.folder / 'parameters.json').unlink(missing_ok=True)
                result = self.secrets('-Action', 'New', '-DeployApp', state='deployed-demo', FAKE_AZ_DEMO=says)
                self.assertIn('carries Demo__Enabled with something this template never writes. Nothing was written.',
                              self.said(result))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((self.folder / 'parameters.json').exists())
                self.assertNotIn('planted-value', result.stderr)
                self.assertEqual(result.stdout, '')

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
        # Read as "no app yet", it would generate every secret anew for an app that holds its own.
        result = self.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, state='resources-unreadable')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('az resource list failed', result.stderr)
        self.assertFalse((self.folder / 'parameters.json').exists())
        self.assertNotIn('generated', result.stderr)

    def test_a_deployed_app_missing_a_key_stops_the_run(self):
        # CONTROL: green before this change, which lets one secret, the eighth, be new for a deployed
        # app. This is one of the seven: it is refused whether the app's demo is on or off.
        result = self.secrets('-Action', 'New', '-DeployApp', state='key-missing')
        self.assertIn('securityPinPepper could not be read from the deployed resource. Nothing was written.',
                      self.said(result))
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


class SecretsScriptQuotesTests(unittest.TestCase):
    """What the runbook quotes of infra/secrets.ps1 about the demo's switch and, since 2026-10-06,
    about the account of the owner's phone, held to the script.
    Both files are read as text and nothing is run: the tests above hold what the script prints,
    and these hold that the page quotes the words the script holds. A step of the runbook gives
    such a line as what is good to read, and a read that differs is a stop there."""

    # The three lines of the report that say what was done with the switch.
    REPORT = ('demo: true, asked for with -DemoOn', "demo: not written, the template's default applies",
              'demo: kept from the deployed resource')
    # The two refusals about the switch, as the script's source writes them: the second names the
    # setting through the script's own variable. The page quotes the second and not the first.
    NEEDS_THE_APP = '-DemoOn needs -DeployApp. Nothing was written.'
    DISAGREE = 'The two containers of the deployed app disagree about $DemoFlag. Nothing was written.'

    @classmethod
    def setUpClass(cls):
        cls.script = (HERE / 'secrets.ps1').read_text(encoding='utf-8')
        # Every run of blanks and line ends as one blank: a quote that is wrapped is still found.
        cls.page = ' '.join((HERE / 'README.md').read_text(encoding='utf-8').split())

    def test_the_lines_about_the_demo_that_the_runbook_quotes_are_the_ones_the_script_writes(self):
        # CONTROL: green as written: each quote was the script's when this was written. Seen red
        # with a line of the report reworded in the script, with the refusal reworded there, and
        # with a quote taken out of the page.
        for words in (*self.REPORT, self.NEEDS_THE_APP, self.DISAGREE):
            with self.subTest(words=words, held_by='the script'):
                self.assertEqual(self.script.count(words), 1)
        (flag,) = re.findall(r"(?m)^\$DemoFlag = '([^']*)'$", self.script)
        for words in (*self.REPORT, self.DISAGREE.replace('$DemoFlag', flag)):
            with self.subTest(words=words, held_by='the runbook'):
                self.assertIn(words, self.page)

    # The four lines of the report that say where the account of the owner's phone came from, or
    # that nobody named one. The script writes each of them whole, once. The page quotes three:
    # no step of it has the variable name the account.
    PHONE_REPORT = ('alertPushAccount: from -AlertPushAccount',
                    'alertPushAccount: from AZUREBANK_ALERT_PUSH_ACCOUNT',
                    'alertPushAccount: kept from the deployed resource',
                    "alertPushAccount: not written, the template's default applies")
    PHONE_QUOTED = (PHONE_REPORT[0], *PHONE_REPORT[2:])

    def test_the_lines_about_the_phones_account_that_the_runbook_quotes_are_the_ones_the_script_writes(self):
        # Seen red with each line of the report reworded in the script, and with each quote
        # taken out of the page.
        for words in self.PHONE_REPORT:
            with self.subTest(words=words, held_by='the script'):
                self.assertEqual(self.script.count(words), 1)
        for words in self.PHONE_QUOTED:
            with self.subTest(words=words, held_by='the runbook'):
                self.assertIn(words, self.page)


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
# What needs the demo's switch as well as the app's: the pool job, and the deployment role's
# assignment on it. BEHIND_DEPLOY_APP above is what needs the app's switch alone.
BEHIND_THE_DEMO = ['Microsoft.App/jobs', 'Microsoft.Authorization/roleAssignments']
# The module that refuses deployApp=true without these values (app-inputs.bicep), and what it asks
# of each: a length of exactly 40 for the image tag, at least 32 for the demo's client key (with
# the demo on the API does not start on a shorter one), at least 1 for the others.
GUARD = 'azurebank-app-inputs'
GUARDED = {'imageTag': (40, 40), 'alertEmail': (1, None), **dict.fromkeys(SEVEN, (1, None)),
           CLIENT_KEY: (32, None)}
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
SECRETS_OF_THE_APP = ['app-connection', 'audit-anchor-key', 'audit-chain-key', 'demo-client-key',
                      'idempotency-hash-key', 'jwt-secret', 'pin-pepper', 'service-key', 'stepup-binding-key']
# What each container of the app is told, by name and in the template's order. The bff's list has
# nothing about forwarded headers: these six are what every run writes. Until 2026-10-06 this
# comment went on "which address it takes for a visitor's is not set in this folder". Since that
# day a run that names networks of proxies writes one setting more for each, after the six, and a
# run that names none writes the six alone (NETWORKS_SETTING, and the test of it below).
SETTINGS_OF_THE_BFF = ['ASPNETCORE_ENVIRONMENT', 'BackendApi__BaseUrl',
                       'ReverseProxy__Clusters__backend-api__Destinations__primary__Address',
                       'ServiceCredential__BffKey', 'Serilog__MinimumLevel__Override__Serilog', 'Demo__Enabled']
# The six with what each holds, as a run with the demo off works them out: what the bff container
# was told, whole, before a run could name a network.
THE_BFF_AS_IT_WAS = [
    {'name': 'ASPNETCORE_ENVIRONMENT', 'value': 'Production'},
    {'name': 'BackendApi__BaseUrl', 'value': 'http://localhost:5068'},
    {'name': 'ReverseProxy__Clusters__backend-api__Destinations__primary__Address', 'value': 'http://localhost:5068'},
    {'name': 'ServiceCredential__BffKey', 'secretRef': 'service-key'},
    {'name': 'Serilog__MinimumLevel__Override__Serilog', 'value': 'Warning'},
    {'name': 'Demo__Enabled', 'value': 'false'}]
# How the bff container's settings are compiled since 2026-10-06: the six, a variable, and after
# them one setting for each network of proxies a run names. And what each of those is named.
BFF_SETTINGS = "[flatten(createArray(variables('bffSettings'), variables('proxyNetworkSettings')))]"
NETWORKS_SETTING = 'ForwardedHeaders__KnownIPNetworks__'
SETTINGS_OF_THE_API = ['ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'ConnectionStrings__DefaultConnection',
                       'Jwt__Secret', 'Idempotency__HashKey', 'StepUp__BindingKey', 'ServiceCredential__BffKey',
                       'Audit__ChainKey', 'Audit__AnchorKey', 'Security__PinPepper', 'Demo__Enabled',
                       'Demo__ClientKeySecret', 'Demo__Claim__MaxPerClientPerDay']
# One switch, written as text to every container of the app.
DEMO_FLAG = {'name': 'Demo__Enabled', 'value': "[if(parameters('demo'), 'true', 'false')]"}
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
    if isinstance(value, list):
        return '[' + ', '.join(bicep_literal(entry) for entry in value) + ']'
    return "'" + value.replace('\\', '\\\\').replace("'", "\\'") + "'"


# The values of a run with the app, none of them real, and of one without it.
FOUNDATION_INPUTS = {'entraAdminObjectId': '00000000-0000-4000-8000-000000000001', 'entraAdminLogin': 'owner'}
APP_INPUTS = {**FOUNDATION_INPUTS, 'deployApp': True, 'imageTag': TAG, 'alertEmail': 'owner',
              **dict.fromkeys(SECURE, 'x')}


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

    def job(self, name):
        """The one job of that name."""
        (job,) = [resource for resource in self.of_type(JOB) if resource['name'] == name]
        return job

    def settings(self, container):
        """A container's settings as the compiled template holds them: a list. The bff's are one
        expression since 2026-10-06, the six that every run writes and after them the networks a
        run names, so what is returned for it is those six, read from their variable. That the
        expression is that one, and what a run works it out to, with networks and without:
        test_the_bff_is_told_the_networks_of_proxies_only_when_a_run_names_them."""
        settings = container.get('env', [])
        if settings == BFF_SETTINGS:
            settings = self.main['variables']['bffSettings']
        self.assertIsInstance(settings, list, f"the settings of {container['name']} are not a list this test can read")
        return settings

    def told(self, resource):
        """A resource's template with each container's settings as `settings` reads them and, for
        the bff, after its six the one setting a network adds, as it is compiled: everything a
        container of the resource can be told. Since 2026-10-06 a search of the template alone no
        longer sees what the bff is told, which stands in two variables."""
        template = json.loads(json.dumps(resource['properties']['template']))
        for container in template.get('containers', []):
            a_network = []
            if container.get('env') == BFF_SETTINGS:
                (networks,) = self.main['variables']['copy']
                self.assertEqual(networks['name'], 'proxyNetworkSettings')
                a_network = [networks['input']]
            container['env'] = self.settings(container) + a_network
        return template

    def container(self, name):
        """The app's container of that name, with its settings as `settings` reads them."""
        (app,) = self.of_type(APP)
        (container,) = [entry for entry in app['properties']['template']['containers'] if entry['name'] == name]
        return dict(container, env=self.settings(container))

    def every_setting(self):
        """Each (resource, container, name of a setting): every container and init container of
        every resource the template compiles. A search that walked nothing would find nothing, so
        the app's two containers and each job's one must be among what this saw. Properties that
        are compiled as one expression hold no container to read: the action group's are (below)."""
        found = []
        for resource in self.resources:
            properties = resource.get('properties', {})
            template = properties.get('template', {}) if isinstance(properties, dict) else {}
            for container in [*template.get('containers', []), *template.get('initContainers', [])]:
                found += [(resource['name'], container['name'], entry['name']) for entry in self.settings(container)]
        self.assertLessEqual({('azurebank', 'bff'), ('azurebank', 'api'), ('azurebank-migrate', 'migrate'),
                              ('azurebank-pool', 'pool')},
                             {(resource, container) for resource, container, _ in found})
        return found

    def conditions(self, condition):
        return sorted(resource['type'] for resource in self.resources if resource.get('condition') == condition)

    def test_the_app_and_all_that_needs_it_are_behind_deploy_app(self):
        self.assertEqual(self.conditions("[parameters('deployApp')]"), sorted(BEHIND_DEPLOY_APP))

    def test_the_pool_job_and_its_role_are_behind_the_demo_switch(self):
        # Two switches, and the job needs both. A job that builds copies while the app's flags are
        # off is the state ADR-0063 warns of ("With the flag on the job and on neither container"),
        # so with the demo off the template writes no such job; and without the app nobody could
        # be handed a copy.
        both = "[and(parameters('deployApp'), parameters('demo'))]"
        self.assertEqual(self.conditions(both), sorted(BEHIND_THE_DEMO))
        # Which two: the job by its name, and the assignment by the job it is on.
        self.assertEqual(sorted((resource['type'], resource.get('scope', resource['name']))
                                for resource in self.resources if resource.get('condition') == both),
                         [(JOB, 'azurebank-pool'),
                          ('Microsoft.Authorization/roleAssignments', f"[resourceId('{JOB}', 'azurebank-pool')]")])
        # What needed the app's switch alone before there was a pool job still needs that one alone,
        # and no other condition reads the demo's switch.
        self.assertEqual(self.conditions("[parameters('deployApp')]"), sorted(BEHIND_DEPLOY_APP))
        self.assertEqual(sorted({resource['condition'] for resource in self.resources
                                 if "parameters('demo')" in resource.get('condition', '')}), [both])

    def test_the_pool_job_waits_for_the_app(self):
        # Written after the app, never beside it. Expected of Azure and not provoked: a run in
        # which the app's own update fails then creates no job, where a job created beside an app
        # whose flags are still off would be the state the test above names.
        (app,) = self.of_type(APP)
        after_the_app = f"[resourceId('{APP}', '{app['name']}')]"
        self.assertIn(after_the_app, self.job('azurebank-pool').get('dependsOn', []))
        # The text is the one Bicep writes for such a wait: the role assignment on the app has it.
        (on_the_app,) = [resource for resource in self.of_type('Microsoft.Authorization/roleAssignments')
                         if resource['scope'] == after_the_app]
        self.assertIn(after_the_app, on_the_app['dependsOn'])

    def test_the_app_is_named_by_a_plain_value_and_no_resource_id_reads_a_secret(self):
        # CONTROL: green before this change. The walk below covers the eighth secure parameter too.
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
        reads_a_secret = {f"parameters('{name}')" for name in SECURE}
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
                         sorted(SECURE))
        self.assertEqual(sorted(name for name, entry in declared.items() if 'defaultValue' in entry), [])
        # It creates nothing and gives nothing back.
        self.assertEqual(guard['properties']['template']['resources'], [])
        self.assertNotIn('outputs', guard['properties']['template'])
        # The app, each of the two jobs and the action group wait for it; the role assignments and
        # the alerts wait for them.
        waits = f"[resourceId('Microsoft.Resources/deployments', '{GUARD}')]"
        self.assertEqual(sorted((resource['type'], resource['name']) for resource in self.resources
                                if waits in resource.get('dependsOn', [])),
                         [(APP, 'azurebank'), (JOB, 'azurebank-migrate'), (JOB, 'azurebank-pool'),
                          ('Microsoft.Insights/actionGroups', 'azurebank-owner')])

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

    def test_with_the_demo_on_the_run_also_creates_the_pool_job_and_its_role(self):
        # Offline, as above. By default the run creates the 22 it created before there was a pool
        # job. With the demo on it creates those and two more: the job, and the deployment role's
        # assignment on it. Asking for the demo without the app adds nothing to the fourteen.
        group = f'/subscriptions/{SNAPSHOT_CONTEXT[1]}/resourceGroups/azurebank-demo/providers'
        with tempfile.TemporaryDirectory() as folder:
            copy_templates(folder)
            runs = {}
            for case, values, count in (
                    ('by default', {}, 22),
                    ('the demo on', {'demo': True}, 24),
                    ('the demo on, and the alert on the workspace', {'demo': True, 'logVolumeAlert': True}, 25),
                    ('the demo asked for without the app', {'demo': True, 'deployApp': False}, 14)):
                code, said, predicted = snapshot(folder, {**APP_INPUTS, **values})
                self.assertEqual(code, 0, said)
                self.assertEqual([resource['id'] for resource in predicted if resource['id'].startswith('[')], [], case)
                self.assertEqual(len(predicted), count, case)
                runs[case] = {resource['id']: resource for resource in predicted}
        default, on = runs['by default'], runs['the demo on']
        job = f'{group}/{JOB}/azurebank-pool'
        self.assertEqual(sorted(set(default) - set(on)), [])
        added = sorted(set(on) - set(default))
        self.assertEqual(added[:1], [job])
        self.assertEqual(len(added), 2, added)
        self.assertTrue(added[1].startswith(job + '/providers/Microsoft.Authorization/roleAssignments/'), added[1])
        # The policy is handed the one name it lets run on a schedule, and it is this job's.
        (assignment,) = [resource for resource in on.values()
                         if resource['type'] == 'Microsoft.Authorization/policyAssignments']
        self.assertEqual(assignment['properties']['parameters'],
                         {'allowedJobTriggers': {'value': ['Manual']}, 'scheduledJobs': {'value': [on[job]['name']]}})
        # The job as the run would send it: every four hours, one run at a time, ten minutes at most.
        configuration = on[job]['properties']['configuration']
        self.assertEqual((configuration['triggerType'], configuration['replicaTimeout'],
                          configuration['scheduleTriggerConfig']),
                         ('Schedule', 600, {'cronExpression': '0 */4 * * *', 'parallelism': 1,
                                            'replicaCompletionCount': 1}))

        # And a run sends the job exactly when it tells both containers of the app that the demo is on.
        def told(run):
            return {(resource['name'], container['name']): entry['value']
                    for resource in run.values() if resource['type'] in (APP, JOB)
                    for container in resource['properties']['template']['containers']
                    for entry in container['env'] if entry['name'] == 'Demo__Enabled'}
        self.assertEqual(told(default), {('azurebank', 'bff'): 'false', ('azurebank', 'api'): 'false'})
        self.assertEqual(told(on), {('azurebank', 'bff'): 'true', ('azurebank', 'api'): 'true',
                                    ('azurebank-pool', 'pool'): 'true'})

        # CONTROL: green as written, from here to the end. The deploy script is asked itself. It
        # types the names of the app, of the two jobs and of their containers, the identity each
        # carries, the pool job's arguments and the demo's setting; this file types them again
        # for the template. A name changed in the template and in the lists above left every
        # test green and the script refusing that job at each deployment with the demo on. So
        # what a run would send is handed to the script's own checks: it finds each resource by
        # its name, in shape, with the container it moves, and reads the demo as the run set it.
        # Seen red with the pool job's container, its name, its arguments and the demo's setting
        # each renamed in the template and in the lists above.
        app = f'{group}/{APP}/{deploy.APP}'
        self.assertEqual(sorted(f'{group}/{JOB}/{name}' for name in deploy.JOBS),
                         sorted(found for found, resource in on.items() if resource['type'] == JOB))
        self.assertEqual(job, f'{group}/{JOB}/{deploy.POOL_JOB}')
        self.assertEqual(deploy.pool_drift(on[job]), [])
        self.assertEqual(deploy.job_drift(on[f'{group}/{JOB}/{deploy.MIGRATE_JOB}']), [])
        self.assertEqual(deploy.app_drift(on[app]), [])
        for name, container in deploy.JOBS.items():
            self.assertEqual(list(deploy.images(on[f'{group}/{JOB}/{name}'])), [container], name)
        self.assertIs(deploy.demo_of(on[app]), True)
        self.assertIs(deploy.demo_of(default[app]), False)
        self.assertEqual(deploy.app_drift(default[app]), [])

    def test_deploy_app_true_is_refused_with_a_tag_that_is_not_40_characters_or_no_address(self):
        # Offline, as above. The secrets cannot be tried this way: like a what-if, this evaluation
        # works out no secure value. Their checks are read from the compiled template above. A
        # local deployment saw each of the seven refuse (README.md, "Checking these files"); the
        # demo's client key, which must be 32 characters, has not been seen refused by any engine.
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
        self.assertEqual(len(self.resources), len(BEHIND_DEPLOY_APP) + len(BEHIND_THE_DEMO) + len(FOUNDATION)
                         + len(BEHIND_DENY_POLICY) + len(BEHIND_KEEP_LOGS) + 1)

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
        self.assertIn({'name': 'Serilog__MinimumLevel__Override__Serilog', 'value': 'Warning'},
                      self.container('bff')['env'])

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

    def test_the_role_is_assigned_on_the_app_and_on_each_job_and_nowhere_else(self):
        assignments = self.of_type('Microsoft.Authorization/roleAssignments')
        scopes = sorted(resource['scope'] for resource in assignments)
        self.assertEqual(scopes, [f"[resourceId('{APP}', 'azurebank')]", f"[resourceId('{JOB}', 'azurebank-migrate')]",
                                  f"[resourceId('{JOB}', 'azurebank-pool')]"])
        # All three go to the deployment identity: the two database identities hold no role on anything in Azure.
        self.assertEqual({resource['properties']['principalId'] for resource in assignments},
                         {f"[reference(resourceId('{IDENTITIES}', 'azurebank-deploy'), '2023-01-31').principalId]"})
        # One role, the nine actions above, and each assignment is named for the resource it is on.
        self.assertEqual({resource['properties']['roleDefinitionId'] for resource in assignments},
                         {"[variables('deployRoleId')]"})
        # CONTROL, the last two lines of the loop: green as written (the three assignments had
        # both). The role's ID in an assignment is a variable, and Bicep works out no wait from
        # one: the wait for the role definition is written by hand, once for each. Expected of
        # Azure and not provoked: in a run that creates the definition and an assignment
        # together, an assignment that does not wait can be sent first. And each says its
        # principal is a service principal. Seen red with the pool job's assignment without its
        # wait, which Bicep compiles and lints with no warning, and with its principal made a user.
        (role,) = self.of_type('Microsoft.Authorization/roleDefinitions')
        after_the_role = f"[resourceId('{role['type']}', {role['name'][1:-1]})]"
        for resource in assignments:
            self.assertEqual(resource['name'], f"[guid({resource['scope'][1:-1]}, "
                             f"resourceId('{IDENTITIES}', 'azurebank-deploy'), variables('deployRoleId'))]")
            self.assertIn(after_the_role, resource['dependsOn'], resource['scope'])
            self.assertEqual(resource['properties']['principalType'], 'ServicePrincipal', resource['scope'])

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

    def test_the_app_and_each_job_carry_their_own_database_identity_and_no_other(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        # The pool job signs in to the database as the app does, to read and write rows, and
        # never as the identity that changes the schema (ADR-0062).
        for resource, name in ((app, 'azurebank-app'), (self.job('azurebank-migrate'), 'azurebank-migrate'),
                               (self.job('azurebank-pool'), 'azurebank-app')):
            self.assertEqual(resource.get('identity'), {'type': 'UserAssigned', 'userAssignedIdentities': {
                f"[format('{{0}}', resourceId('{IDENTITIES}', '{name}'))]": {}}}, resource['name'])

    def test_each_connection_string_names_its_own_identity_and_holds_no_credential(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        for resource, secret, identity in ((app, 'app-connection', 'azurebank-app'),
                                           (self.job('azurebank-migrate'), 'migration-connection', 'azurebank-migrate'),
                                           (self.job('azurebank-pool'), 'app-connection', 'azurebank-app')):
            self.assertEqual([entry['value'] for entry in resource['properties']['configuration'].get('secrets', [])
                              if entry['name'] == secret], [connection_of(identity)], (resource['name'], secret))

    def test_eight_parameters_are_secure_and_only_two_must_be_given(self):
        parameters = self.main['parameters']
        secure = sorted(name for name, entry in parameters.items() if entry['type'].lower() == 'securestring')
        self.assertEqual(secure, sorted(SECURE))
        required = sorted(name for name, entry in parameters.items() if 'defaultValue' not in entry)
        self.assertEqual(required, ['entraAdminLogin', 'entraAdminObjectId'])
        self.assertEqual((parameters['replicaTimeout']['minValue'], parameters['replicaTimeout']['maxValue']),
                         (60, 840))
        self.assertIs(parameters['deployApp']['defaultValue'], False)
        # The demo is a switch of its own, and off unless it is asked for.
        demo = parameters.get('demo', {})
        self.assertEqual((demo.get('type'), demo.get('defaultValue')), ('bool', False))

    def test_no_output_is_a_secret(self):
        self.assertEqual({name: entry['type'] for name, entry in self.main['outputs'].items()},
                         dict.fromkeys(['sqlServerFqdn', 'sqlServerName', 'deploymentClientId',
                                        'deploymentPrincipalId', 'appIdentityClientId', 'migrateIdentityClientId',
                                        'logWorkspaceCustomerId', 'appUrl'], 'string'))

    def test_every_secret_reaches_a_container_by_reference_only(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        job, pool = self.job('azurebank-migrate'), self.job('azurebank-pool')
        self.assertEqual(len(app['properties']['configuration']['secrets']), 9)
        self.assertEqual(len(job['properties']['configuration']['secrets']), 1)
        self.assertEqual(len(pool['properties']['configuration'].get('secrets', [])), 2)
        # What each container can be told (`told`): the bff's settings stand in two variables
        # since 2026-10-06, and until then this read the three templates as they are compiled.
        text = json.dumps([self.told(resource) for resource in (app, job, pool)])
        self.assertEqual(text.count('"secretRef"'), 13)
        for name in SECURE:
            self.assertNotIn(f"parameters('{name}')", text, f'{name} is a plain value in a container')

    def test_only_the_api_container_and_the_two_jobs_are_handed_a_connection_string(self):
        (app,) = self.of_type('Microsoft.App/containerApps')
        job, pool = self.job('azurebank-migrate'), self.job('azurebank-pool')
        handed = {container['name']: sorted(entry['secretRef'] for entry in container['env'] if 'secretRef' in entry)
                  for container in self.told(app)['containers']}
        # The bff faces the internet and can use the app's identity like any container of the app:
        # it is told neither which identity nor which server.
        self.assertEqual(handed, {'bff': ['service-key'], 'api': SECRETS_OF_THE_APP})
        (migrate,) = job['properties']['template']['containers']
        self.assertEqual([entry.get('secretRef') for entry in migrate['env']], ['migration-connection'])
        # The pool job is handed the app's string and the pepper, each a secret of its own (below).
        (recycle,) = pool['properties']['template']['containers']
        self.assertEqual([entry['secretRef'] for entry in recycle.get('env', []) if 'secretRef' in entry],
                         ['app-connection', 'pin-pepper'])
        # Nor does either reach a container as a plain value: nothing in a container is read from another resource.
        self.assertNotIn('reference(', json.dumps([self.told(resource) for resource in (app, job, pool)]))

    def test_the_pool_job_holds_two_secrets_and_the_pepper_the_app_holds(self):
        # A job has a secret list of its own. The pool's commands need the connection string and
        # the PIN pepper, "which must be the API's" (backend/tools/AzureBank.Seeder/README.md), so
        # the template writes the job's two from what it writes the app's from: one run of it
        # cannot give the job and the app two peppers.
        (app,) = self.of_type(APP)
        pool = self.job('azurebank-pool')
        of_the_app = {entry['name']: entry['value'] for entry in app['properties']['configuration']['secrets']}
        secrets = pool['properties']['configuration'].get('secrets', [])
        self.assertEqual([entry['name'] for entry in secrets], ['app-connection', 'pin-pepper'])
        self.assertEqual({entry['name']: entry['value'] for entry in secrets},
                         {name: of_the_app[name] for name in ('app-connection', 'pin-pepper')})
        self.assertEqual(of_the_app['pin-pepper'], "[parameters('securityPinPepper')]")
        # Its container is handed the two by reference, under the names the api container reads
        # them by, and nothing else of the job is a secret.
        (container,) = pool['properties']['template']['containers']
        by_reference = [entry for entry in container.get('env', []) if 'secretRef' in entry]
        self.assertEqual(by_reference, [{'name': 'ConnectionStrings__DefaultConnection', 'secretRef': 'app-connection'},
                                        {'name': 'Security__PinPepper', 'secretRef': 'pin-pepper'}])
        for entry in by_reference:
            self.assertIn(entry, self.container('api')['env'])
        # No other secure parameter is read anywhere in the job: not the client key, which only
        # the api hashes an address with, and none of the six other keys.
        self.assertEqual([name for name in SECURE if f"parameters('{name}')" in json.dumps(pool)],
                         ['securityPinPepper'])

    def test_the_demo_is_off_unless_asked_and_one_switch_says_it_to_both_containers(self):
        # Both hosts read the flag: the bff closes registration and marks the page by it, the api
        # hands out the copies by it. One parameter writes it to both as the same text, so the
        # template cannot set the two apart.
        self.assertEqual({name: [entry for entry in self.container(name)['env'] if entry['name'] == 'Demo__Enabled']
                          for name in ('bff', 'api')}, {'bff': [DEMO_FLAG], 'api': [DEMO_FLAG]})
        self.assertIs(self.main['parameters']['demo']['defaultValue'], False)

    def test_both_scripts_read_the_demo_by_the_setting_and_the_containers_the_template_writes(self):
        # CONTROL: green as written. infra/secrets.ps1 and infra/deploy.py each read from the
        # deployed app whether the demo is on, by a name typed in each, and a setting that is not
        # found reads as off. A name that differed from the template's would make the secrets
        # script write demo=false for a demo that is on, and report it as "kept from the deployed
        # resource". Seen red with the name changed in the script, and with the two containers
        # it asks changed there.
        (app,) = self.of_type(APP)
        containers = [container['name'] for container in app['properties']['template']['containers']]
        script = (HERE / 'secrets.ps1').read_text(encoding='utf-8')
        self.assertEqual(re.findall(r"(?m)^\$DemoFlag = '([^']*)'$", script), [DEMO_FLAG['name']])
        asks = r"(?m)^ *\$says = @\('(\w+)', '(\w+)' \| ForEach-Object \{ Get-DemoFlag \$app \$_ \}\)$"
        self.assertEqual(re.findall(asks, script), [tuple(containers)])
        self.assertEqual(deploy.DEMO_FLAG, DEMO_FLAG['name'])

    def test_every_setting_of_the_demo_is_one_the_backend_binds(self):
        # CONTROL: green as written. A setting whose name the backend does not bind is not an
        # error anywhere: the host starts and the default applies. For the cap on one address's
        # claims that default is 10 where the template means 1,000 (ADR-0063, decision 14). So
        # each name the template writes under the demo's section is walked through the classes
        # of backend/src/AzureBank.Shared/Options/DemoOptions.cs, read as text: the section,
        # then a property of each class on the way. Seen red with the cap's name changed in the
        # template and in the lists above.
        source = (HERE.parent / 'backend' / 'src' / 'AzureBank.Shared' / 'Options' / 'DemoOptions.cs').read_text(
            encoding='utf-8')
        classes = {name: dict((prop, kind) for kind, prop in re.findall(r'public (\S+) (\w+) \{ get; set; \}', body))
                   for name, body in re.findall(r'(?ms)^public class (\w+)\n\{\n(.*?)^\}', source)}
        (section,) = re.findall(r'public const string SectionName = "(\w+)";', source)
        written = sorted({name for _, _, name in self.every_setting() if name.startswith(f'{section}__')})
        self.assertEqual(written, ['Demo__Claim__MaxPerClientPerDay', 'Demo__ClientKeySecret', 'Demo__Enabled'])
        for name in written:
            with self.subTest(name=name):
                kind = 'DemoOptions'
                for part in name.split('__')[1:]:
                    self.assertIn(part, classes.get(kind, {}), f'{kind} has no such property')
                    kind = classes[kind][part]
                self.assertIn(kind, ('bool', 'int', 'string?'), 'the name stops at a section, not at a value')

    def test_the_bff_is_handed_the_flag_and_nothing_about_forwarded_headers(self):
        self.assertEqual([entry['name'] for entry in self.container('bff')['env']], SETTINGS_OF_THE_BFF)
        # Whether the BFF sees a visitor's own address behind the ingress is not measured yet
        # (ADR-0063, decision 14): until it is, no container of any resource is told whose
        # forwarded headers to believe. .NET reads a setting's name whatever its case.
        # Since 2026-10-06 that holds for what every run writes, which is what `every_setting`
        # walks: a run that names networks of proxies tells the bff, and the bff alone, to believe
        # them (the test below), and the switch that would make a host believe every caller is
        # still written nowhere.
        about_forwarded_headers = re.compile(r'ForwardedHeaders__|ASPNETCORE_FORWARDEDHEADERS_ENABLED$', re.IGNORECASE)
        for name in ('ForwardedHeaders__KnownProxies__0', 'ASPNETCORE_FORWARDEDHEADERS_ENABLED',
                     f'{NETWORKS_SETTING}0'):
            self.assertRegex(name, about_forwarded_headers)
        self.assertEqual([found for found in self.every_setting() if about_forwarded_headers.match(found[2])], [])
        # The tool reads the deployed app by the same pattern.
        self.assertEqual(deploy.ABOUT_FORWARDED.pattern, about_forwarded_headers.pattern)
        self.assertEqual(deploy.ABOUT_FORWARDED.flags, about_forwarded_headers.flags)
        # And nothing compiled anywhere names such a setting but the one expression that numbers
        # the networks: not a second list, and not the switch.
        compiled = json.dumps([self.main['variables'], self.main['resources']])
        self.assertEqual(len(re.findall('forwardedheaders', compiled, re.IGNORECASE)), 1)
        self.assertEqual(compiled.count(f"[format('{NETWORKS_SETTING}{{0}}', copyIndex('proxyNetworkSettings'))]"), 1)

    def test_the_bff_is_told_the_networks_of_proxies_only_when_a_run_names_them(self):
        # The ranges are the ones kept for documentation: none is a network of any deployment.
        one, two = '192.0.2.0/24', '2001:db8:7::/48'
        with self.subTest(read='the parameter'):
            # A plain list, empty unless a run gives it, and not among what the app needs: the
            # check of the app's values knows nothing of it.
            networks = self.main['parameters'].get('proxyNetworks', {})
            self.assertEqual((networks.get('type'), networks.get('defaultValue')), ('array', []))
            self.assertNotIn('proxyNetworks', GUARDED)
            self.assertEqual(re.findall(r'[0-9]{1,3}(?:\.[0-9]{1,3}){3}/[0-9]+', json.dumps(self.main)), [])
        with self.subTest(read='the compiled template'):
            # The bff's settings are the six and, after them, one for each network; the api's and
            # each job's are the plain lists they were.
            (app,) = self.of_type(APP)
            bff, api = app['properties']['template']['containers']
            self.assertEqual((bff['name'], bff['env']), ('bff', BFF_SETTINGS))
            self.assertIsInstance(api['env'], list)
            self.assertEqual(self.main['variables'].get('copy'), [{
                'name': 'proxyNetworkSettings', 'count': "[length(parameters('proxyNetworks'))]",
                'input': {'name': f"[format('{NETWORKS_SETTING}{{0}}', copyIndex('proxyNetworkSettings'))]",
                          'value': "[parameters('proxyNetworks')[copyIndex('proxyNetworkSettings')]]"}}])
            for job in self.of_type(JOB):
                for container in job['properties']['template']['containers']:
                    self.assertIsInstance(container['env'], list, job['name'])
        # Worked out offline, as a what-if works a run out.
        runs = {}
        with tempfile.TemporaryDirectory() as folder:
            copy_templates(folder)
            for case, values, count in (('no network named', {}, 22),
                                        ('an empty list', {'proxyNetworks': []}, 22),
                                        ('one network', {'proxyNetworks': [one]}, 22),
                                        ('two networks', {'proxyNetworks': [one, two]}, 22),
                                        ('the demo on', {'demo': True}, 24),
                                        ('the demo on, two networks', {'demo': True, 'proxyNetworks': [one, two]}, 24),
                                        ('two networks without the app',
                                         {'proxyNetworks': [one, two], 'deployApp': False}, 14)):
                with self.subTest(case=case, read='the count'):
                    code, said, predicted = snapshot(folder, {**APP_INPUTS, **values})
                    self.assertEqual(code, 0, said)
                    self.assertEqual(len(predicted), count)
                    runs[case] = {resource['id']: resource for resource in predicted}

        def told(case):
            """What a run tells the bff container, what it tells the api container, and every
            other resource of the run as it is."""
            (found,) = [key for key, resource in runs.get(case, {}).items() if resource['type'] == APP]
            bff, api = runs[case][found]['properties']['template']['containers']
            self.assertEqual((bff['name'], api['name']), ('bff', 'api'))
            others = {key: resource for key, resource in runs[case].items() if key != found}
            return bff['env'], api['env'], others, found
        # CONTROL: green before a run could name a network. With none named the bff is told what
        # it was told, whole: six settings, each with what it held.
        with self.subTest(case='no network named'):
            self.assertEqual(told('no network named')[0], THE_BFF_AS_IT_WAS)
        # A list named as empty is no network: the same run, resource for resource.
        with self.subTest(case='an empty list'):
            self.assertEqual(runs.get('an empty list'), runs['no network named'])
        # Each network adds one setting to the bff, after the six, numbered from 0 in the order
        # given. Nothing else of the run changes: not the api container, not another resource,
        # not the rest of the app. And each network stands in that one place.
        for case, networks, without in (('one network', [one], 'no network named'),
                                        ('two networks', [one, two], 'no network named'),
                                        ('the demo on, two networks', [one, two], 'the demo on')):
            with self.subTest(case=case):
                bff, api, others, app = told(case)
                as_it_was, api_as_it_was, others_as_they_were, _ = told(without)
                self.assertEqual(bff, as_it_was + [{'name': f'{NETWORKS_SETTING}{number}', 'value': network}
                                                    for number, network in enumerate(networks)])
                self.assertEqual((api, others), (api_as_it_was, others_as_they_were))
                rest_of_the_app = json.loads(json.dumps(runs[case][app]))
                rest_of_the_app['properties']['template']['containers'][0]['env'] = as_it_was
                self.assertEqual(rest_of_the_app, runs[without][app])
                for network in networks:
                    self.assertEqual(json.dumps(runs[case]).count(network), 1)
                # The tool counts them on what the template works out, and the secrets script's
                # name for the setting is the template's: a name typed in all three cannot drift
                # on one side.
                self.assertEqual(deploy.proxy_networks(runs[case][app]), len(networks))
                self.assertEqual(deploy.proxy_networks(runs[without][app]), 0)
        with self.subTest(read='the names'):
            self.assertEqual((deploy.NETWORKS_SETTING, deploy.NETWORKS_CONTAINER), (NETWORKS_SETTING, 'bff'))
            script = (HERE / 'secrets.ps1').read_text(encoding='utf-8')
            self.assertEqual(re.findall(r"(?m)^\$NetworksSetting = '([^']*)'$", script), [NETWORKS_SETTING])
            # And it is a name the BFF binds: the section, then the list, then a number
            # (backend/src/AzureBank.Bff/Options/ProxyOptions.cs, read as text).
            source = (HERE.parent / 'backend' / 'src' / 'AzureBank.Bff' / 'Options' / 'ProxyOptions.cs').read_text(
                encoding='utf-8')
            (section,) = re.findall(r'public const string SectionName = "(\w+)";', source)
            lists = re.findall(r'public string\[\] (\w+) \{ get; set; \}', source)
            self.assertIn(NETWORKS_SETTING, [f'{section}__{name}__' for name in lists])
        # Without the app there is no container to tell.
        with self.subTest(case='two networks without the app'):
            self.assertEqual(len(runs.get('two networks without the app', {})), 14)
            self.assertEqual([key for key, resource in runs.get('two networks without the app', {}).items()
                              if resource['type'] == APP or one in json.dumps(resource)], [])

    def test_the_api_is_handed_the_client_key_by_reference_and_the_cap_as_a_plain_value(self):
        api = self.container('api')
        self.assertEqual([entry['name'] for entry in api['env']], SETTINGS_OF_THE_API)
        # The key a visitor's address is hashed with: a secret of the app, handed to the api alone.
        (app,) = self.of_type(APP)
        self.assertIn({'name': 'demo-client-key', 'value': f"[parameters('{CLIENT_KEY}')]"},
                      app['properties']['configuration']['secrets'])
        self.assertIn({'name': 'Demo__ClientKeySecret', 'secretRef': 'demo-client-key'}, api['env'])
        # How many copies one address may claim in a day: one variable, the range's maximum
        # (ADR-0063, decision 14), written as text.
        self.assertIn({'name': 'Demo__Claim__MaxPerClientPerDay', 'value': "[variables('demoClaimsPerClient')]"},
                      api['env'])
        self.assertEqual(self.main['variables'].get('demoClaimsPerClient'), '1000')
        # No key id and no earlier pepper on any container of any resource: with neither set, the
        # one pepper is key 1 wherever it is read (ADR-0062).
        about_pepper_keys = re.compile(r'Security__PinPepperKeyId$|Security__PreviousPinPeppers__', re.IGNORECASE)
        for name in ('Security__PinPepperKeyId', 'Security__PreviousPinPeppers__1'):
            self.assertRegex(name, about_pepper_keys)
        self.assertEqual([found for found in self.every_setting() if about_pepper_keys.match(found[2])], [])

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
        job = self.job('azurebank-migrate')
        configuration = job['properties']['configuration']
        self.assertEqual((configuration['triggerType'], configuration['replicaRetryLimit'],
                          configuration['manualTriggerConfig']['parallelism']), ('Manual', 0, 1))
        (migrate,) = job['properties']['template']['containers']
        self.assertEqual((migrate['name'], migrate['args'], migrate['resources']),
                         ('migrate', ['migrate'], {'cpu': "[json('0.25')]", 'memory': '0.5Gi'}))
        self.assertNotIn('initContainers', job['properties']['template'])

    def test_the_pool_job_keeps_the_shape_the_deploy_script_and_the_policy_expect(self):
        pool, migrate = self.job('azurebank-pool'), self.job('azurebank-migrate')
        configuration, template = pool['properties']['configuration'], pool['properties']['template']
        # On a schedule, one run at a time, and a run is never tried again: `recycle` ends a run
        # that found something to say with an exit code from 10 to 15 (docs/runbooks/demo-pool.md),
        # and with a retry Azure is expected to take such a run for a failed one and to try it again.
        self.assertEqual((configuration.get('triggerType'), configuration.get('replicaRetryLimit')), ('Schedule', 0))
        self.assertEqual(configuration.get('scheduleTriggerConfig'),
                         {'cronExpression': "[variables('poolSchedule')]", 'parallelism': 1,
                          'replicaCompletionCount': 1})
        self.assertEqual(sorted(key for key in configuration if key.endswith('TriggerConfig')),
                         ['scheduleTriggerConfig'])
        # Every four hours, on the hour. One variable: no parameter of a run can change it.
        self.assertEqual(self.main['variables'].get('poolSchedule'), '0 */4 * * *')
        self.assertNotIn('poolSchedule', self.main['parameters'])
        # A timeout of its own, within the bounds the migrate job's has.
        self.assertEqual(configuration.get('replicaTimeout'), "[parameters('poolTimeout')]")
        timeout = self.main['parameters'].get('poolTimeout', {})
        self.assertEqual({key: timeout.get(key) for key in ('type', 'minValue', 'maxValue', 'defaultValue')},
                         {'type': 'int', 'minValue': 60, 'maxValue': 840, 'defaultValue': 600})
        # In the environment and on the profile of the migrate job, and nothing else is set on it.
        self.assertEqual(sorted(pool['properties']),
                         ['configuration', 'environmentId', 'template', 'workloadProfileName'])
        for key in ('environmentId', 'workloadProfileName'):
            self.assertEqual(pool['properties'][key], migrate['properties'][key], key)
        # One container: the tools image at the commit a run names (`imageTag`), which is the
        # migrate job's image; `recycle`; a quarter of a vCPU. The policy refuses an init
        # container and anything above half a vCPU.
        (container,) = template['containers']
        (tool,) = migrate['properties']['template']['containers']
        # CONTROL: green as written. The text itself, once: compared with each other only, the
        # two jobs' images could both be another image, or the tools image at no commit. Seen
        # red with both made the api's image, and with both made the tools image at `latest`.
        self.assertEqual(tool['image'], "[format('ghcr.io/gurgant/azurebank-tools:{0}', parameters('imageTag'))]")
        self.assertEqual((container['name'], container.get('image'), container.get('args'), container.get('resources')),
                         ('pool', tool['image'], ['recycle'], {'cpu': "[json('0.25')]", 'memory': '0.5Gi'}))
        self.assertEqual(sorted(template), ['containers'])
        # CONTROL: green as written. Nothing else is set on the container or in the
        # configuration: the job carries the app's database identity and the pepper, so what it
        # runs is held whole. A `command` is expected to replace the image's entry point
        # (backend/tools/AzureBank.Seeder/Dockerfile) and to leave `recycle` an argument of
        # something else. Seen red with a `command` on the container, and with an empty
        # `registries` in the configuration.
        self.assertEqual(sorted(container), ['args', 'env', 'image', 'name', 'resources'])
        self.assertEqual(sorted(configuration),
                         ['replicaRetryLimit', 'replicaTimeout', 'scheduleTriggerConfig', 'secrets', 'triggerType'])
        # What it is told: where the database is and the pepper, by reference; that the demo is
        # on, as a plain word, because the template writes the job only with the demo on; and the
        # cap on one address's claims, the one expression the api container has.
        (cap,) = [entry for entry in self.container('api')['env'] if entry['name'] == 'Demo__Claim__MaxPerClientPerDay']
        self.assertEqual(container.get('env'), [
            {'name': 'ConnectionStrings__DefaultConnection', 'secretRef': 'app-connection'},
            {'name': 'Security__PinPepper', 'secretRef': 'pin-pepper'},
            {'name': 'Demo__Enabled', 'value': 'true'},
            cap])

    def test_two_scheduled_runs_cannot_overlap(self):
        # Two pool runs at once can build up to twice the pool's target
        # (backend/tools/AzureBank.Seeder/README.md), so the template leaves no room for it: a run
        # every H hours, where H divides the day (the gap across midnight is then H hours like
        # every other), and H hours are longer than the longest timeout a run may be given. One
        # try a run: the test above holds the retry at 0. It holds for what the template writes.
        # It says nothing of a job that is deployed, whose schedule and timeout whoever may write
        # the job can change.
        schedule = self.main['variables'].get('poolSchedule', '')
        form = re.fullmatch(r'([0-5]?[0-9]) \*/([1-9][0-9]?) \* \* \*', schedule)
        self.assertIsNotNone(form, f'not of the form "M */H * * *": {schedule!r}')
        hours = int(form.group(2))
        self.assertEqual(24 % hours, 0, schedule)
        longest = self.main['parameters'].get('poolTimeout', {}).get('maxValue')
        self.assertIsInstance(longest, int)
        self.assertGreater(hours * 3600, longest)
        # And the timeout the job is given is that parameter, so its bound is the job's.
        configuration = self.job('azurebank-pool')['properties']['configuration']
        self.assertEqual(configuration.get('replicaTimeout'), "[parameters('poolTimeout')]")

    def test_the_schedule_the_tool_expects_is_the_one_the_template_writes(self):
        # infra/deploy.py reads the deployed job's expression against a constant of its own, at a
        # deployment: the test above holds the template, and this one holds the tool to it.
        tool = (HERE / 'deploy.py').read_text(encoding='utf-8')
        self.assertEqual(re.findall(r"(?m)^POOL_SCHEDULE = '([^']*)'$", tool),
                         [self.main['variables']['poolSchedule']])

    def test_the_timeouts_the_tool_takes_on_the_pool_job_are_the_ones_the_template_allows(self):
        tool = (HERE / 'deploy.py').read_text(encoding='utf-8')
        timeout = self.main['parameters']['poolTimeout']
        self.assertEqual(re.findall(r'(?m)^POOL_TIMEOUTS = \((\d+), (\d+)\)$', tool),
                         [(str(timeout['minValue']), str(timeout['maxValue']))])

    def test_the_secrets_the_tool_compares_are_the_ones_the_template_writes(self):
        # CONTROL: green as written. `python infra/deploy.py --check` lists the pool job's secrets
        # and the app's, and compares the two it names in a constant of its own. The test of the
        # two secrets holds the template, and this one holds the tool to it: a name changed in one
        # of the two only would leave that check unable to compare, at every run. Seen red with a
        # name changed in the tool, and with one changed in the template.
        tool = (HERE / 'deploy.py').read_text(encoding='utf-8')
        compared = re.findall(r"(?m)^POOL_SECRETS = \{'([a-z-]+)': '[^']*', '([a-z-]+)': '[^']*'\}$", tool)
        secrets = self.job('azurebank-pool')['properties']['configuration'].get('secrets', [])
        self.assertEqual([sorted(names) for names in compared], [sorted(entry['name'] for entry in secrets)])
        # Each is a secret of the app under the same name: the one it is compared with.
        (app,) = self.of_type(APP)
        of_the_app = [entry['name'] for entry in app['properties']['configuration']['secrets']]
        self.assertEqual([name for names in compared for name in names if name not in of_the_app], [])

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
        # The mailbox is a parameter, and so is the account of the phone: no address is written in
        # the template. The account is empty unless it is given, and is not among what the app needs.
        self.assertEqual(re.findall(r'[\w.+-]+@[\w-]+\.\w+', json.dumps(self.main)), [])
        with self.subTest(read='the parameter'):
            account = self.main['parameters'].get('alertPushAccount', {})
            self.assertEqual((account.get('type'), account.get('defaultValue')), ('string', ''))
            self.assertNotIn('alertPushAccount', GUARDED)
        # The group's receivers, worked out offline as a what-if works them out. Until 2026-10-06
        # this test read three keys in the compiled template: the receivers were one mailbox,
        # whatever the run was given. Now the phone's receiver is written only when its account is
        # given, so the properties are compiled as one expression and are read worked out.
        mailbox, phone = 'the.mailbox@example.invalid', 'the.phone@example.invalid'
        as_it_was = {'groupShortName': 'azurebank', 'enabled': True, 'emailReceivers': [
            {'name': 'owner', 'emailAddress': mailbox, 'useCommonAlertSchema': True}]}
        runs = {}
        with tempfile.TemporaryDirectory() as folder:
            copy_templates(folder)
            for case, values, count in (('no account named', {}, 22),
                                        ('an empty account', {'alertPushAccount': ''}, 22),
                                        ('an account given', {'alertPushAccount': phone}, 22),
                                        ('an account given without the app',
                                         {'alertPushAccount': phone, 'deployApp': False}, 14)):
                with self.subTest(case=case, read='the count'):
                    code, said, predicted = snapshot(folder, {**APP_INPUTS, 'alertEmail': mailbox, **values})
                    self.assertEqual(code, 0, said)
                    self.assertEqual(len(predicted), count)
                    runs[case] = {resource['id']: resource for resource in predicted}

        def receivers(case):
            """The properties of the one action group of a run, and every other resource of it."""
            (found,) = [key for key, resource in runs.get(case, {}).items() if resource['type'] == group['type']]
            return runs[case][found]['properties'], {key: resource for key, resource in runs[case].items()
                                                     if key != found}
        # CONTROL: green before the phone's receiver existed. With no account named the group is
        # what it was: the same three properties and one mailbox, taken from the parameter.
        with self.subTest(case='no account named'):
            self.assertEqual(receivers('no account named')[0], as_it_was)
        # An account named as empty is no account: the same run, resource for resource.
        with self.subTest(case='an empty account'):
            self.assertEqual(runs.get('an empty account'), runs['no account named'])
        # An account given adds one receiver of the Azure mobile app, with that account, under a
        # name of its own: a receiver's name must be unique in its group. Nothing else of the run
        # changes, and the account stands in that one place.
        with self.subTest(case='an account given'):
            properties, others = receivers('an account given')
            self.assertEqual(properties, {**as_it_was, 'azureAppPushReceivers': [
                {'name': 'owner-phone', 'emailAddress': phone}]})
            self.assertEqual(others, receivers('no account named')[1])
            self.assertEqual(json.dumps(runs['an account given']).count(phone), 1)
        # Without the app there is no group to carry it.
        with self.subTest(case='an account given without the app'):
            self.assertEqual([resource['type'] for resource in runs.get('an account given without the app', {}).values()
                              if resource['type'] == group['type'] or phone in json.dumps(resource)], [])
            self.assertEqual(len(runs.get('an account given without the app', {})), 14)

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
            # With -DemoOn, so that the demo's switch is written too, with an account for the
            # phone, so that its parameter is, and with a network of proxies, so that theirs is:
            # the name the script writes is the template's.
            self.assertEqual(case.secrets('-Action', 'New', '-DeployApp', '-ImageTag', TAG, '-DemoOn',
                                          '-AlertPushAccount', 'the.phone@example.invalid',
                                          '-ProxyNetworks', '192.0.2.0/24',
                                          state='foundation').returncode, 0)
            written = set(case.parameters())
            self.assertIn('demo', written)
            self.assertIn('alertPushAccount', written)
            self.assertIn('proxyNetworks', written)
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
