#Requires -Version 7.2
<#
.SYNOPSIS
  Creates the two database users, each bound to its managed identity, by running sql-principals.sql.

.DESCRIPTION
  The SQL is all in sql-principals.sql. It is run by Microsoft's sqlcmd (go-sqlcmd 1.10.0 or later),
  which signs in to the AzureBank database as the server's Microsoft Entra administrator through
  the Azure CLI session. This script holds no token and no password. It does what the SQL tool
  cannot: it checks the tool, reads the two identities, and lets this machine through the server's
  firewall for the length of the run.

  The tool
    * It is started by its full path, -SqlcmdPath, never through PATH: the older ODBC sqlcmd also
      answers to that name, and a terminal opened before an install finds a different one.
    * Before its first use: --version must say v1.10.0 or later, on Windows its signature must be
      valid and Microsoft's, and its -? must name the authentication method.
    * Always with -b: without it sqlcmd exits 0 when the SQL stops on an error.
  The identities
    * Their IDs are read with az identity show. Each is parsed as a GUID, and what goes to sqlcmd
      is the parsed value printed again, never the text that was read.
    * The names of the six sqlcmd variables are removed from this process's environment first:
      sqlcmd takes a variable from there too.
  The firewall
    * The server lets in Azure services only, so this machine's address is allowed by a rule named
      owner-while-creating-users. The address is the one the server itself names when it refuses
      the first connection.
    * First, before anything else is written: a rule of that name left by a run that died is deleted.
    * A new rule can take minutes to work: sqlcmd is started again only while its answer is that
      same refusal. Any other failure ends the run at once.
    * At the end, whatever happened: the rule is deleted, and the rule list is read back.
    * A delete that fails says that the rule may still be there, prints the command that removes it,
      and the script exits non-zero.
  The run counts as done only when sqlcmd exits 0 and both users were printed with their roles.

  Switches for what Azure may answer on the first deployment (README.md):
    -AuthenticationMethod   ActiveDirectoryDefault, if the tool cannot use the Azure CLI session.
    -IdKind ObjectId        binds each user to its identity's object ID instead of its client ID.
    -CreateForm ExternalProvider
                            creates each user FROM EXTERNAL PROVIDER WITH OBJECT_ID, if the server
                            refuses the form that asks the directory nothing.
    -ProveSqlSignInRefused  then tries one SQL sign-in with a name and a value made up on the spot,
                            while the firewall still lets this machine in. It passes only if the
                            server refuses it because Microsoft Entra-only authentication is on.
    -OdbcSignInName         the last resort: -SqlcmdPath is the older ODBC sqlcmd, which signs in
                            as this account through a window. It runs with -X1.

  Nothing is written to standard output; the report on standard error holds no address and no ID.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'azurebank-demo',
    [string]$SqlcmdPath = 'C:\Program Files\sqlcmd\sqlcmd.exe',
    [ValidateSet('ActiveDirectoryAzCli', 'ActiveDirectoryDefault')][string]$AuthenticationMethod = 'ActiveDirectoryAzCli',
    [ValidateSet('ClientId', 'ObjectId')][string]$IdKind = 'ClientId',
    [ValidateSet('Sid', 'ExternalProvider')][string]$CreateForm = 'Sid',
    [switch]$ProveSqlSignInRefused,
    [string]$OdbcSignInName = '',
    [string]$RuleName = 'owner-while-creating-users',
    [ValidateRange(1, 120)][int]$ConnectTimeout = 30,
    [ValidateRange(1, 900)][int]$WaitSeconds = 300,
    [ValidateRange(1, 60)][int]$RetrySeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Users = [ordered]@{
    azurebank_app      = 'db_datareader, db_datawriter'
    azurebank_migrator = 'db_datareader, db_datawriter, db_ddladmin'
}
$Variables = 'AppClientId', 'AppObjectId', 'MigratorClientId', 'MigratorObjectId', 'IdKind', 'CreateForm'
$OnlyEntra = 'Azure Active Directory only authentication is enabled'
# The server's refusal of an address it does not let in (its error 40615), known by its sentence:
# sqlcmd prints an error at sign-in as text, without the number it prints for an error in a batch.
$FirewallRefusal = "Client with IP address '(?<address>[^']*)' is not allowed to access the server"

function Say([string]$Message) { [Console]::Error.WriteLine($Message) }

function Hide-Address([string]$Text) { $Text -replace '\b\d{1,3}(\.\d{1,3}){3}\b', '<address>' }

function Invoke-Az {
    # Arguments must stay free of & | ( ) < > ^ %: on Windows az is a .cmd and cmd.exe reads them.
    $output = & az @args --only-show-errors --output json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "az $($args[0]) $($args[1]) $($args[2]) failed with exit code $LASTEXITCODE." }
    if ($output) { ($output -join "`n") | ConvertFrom-Json -AsHashtable } else { $null }
}

function Invoke-Tool([string[]]$Arguments) {
    # Both streams: go-sqlcmd writes the error that stopped the SQL to standard output.
    $lines = @(& $SqlcmdPath @Arguments 2>&1 | ForEach-Object { "$_" })
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($lines -join "`n") }
}

# The one place the signature is read. The tests put a stand-in behind this name (test_scripts.py).
function Get-ToolSignature([string]$Path) {
    if (-not $IsWindows) { return $null }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
    @{ Status = [string]$signature.Status; Subject = $subject }
}

function ConvertTo-Id([string]$Text, [string]$What) {
    # Parsed, then printed again: a pattern would let a trailing newline through.
    $parsed = [guid]::Empty
    if (-not [guid]::TryParseExact($Text, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) {
        throw "$What is not an ID. Nothing was run."
    }
    $parsed.ToString('D')
}

if ($CreateForm -eq 'ExternalProvider' -and $IdKind -ne 'ClientId') {
    throw 'A user created FROM EXTERNAL PROVIDER carries the client ID: -CreateForm ExternalProvider goes with -IdKind ClientId only.'
}
if ($OdbcSignInName -and $OdbcSignInName -notmatch '^[^\s@]+@[^\s@]+$') { throw '-OdbcSignInName is the sign-in name of the account, as in name@domain.' }

# sqlcmd reads these from the environment as well as from -v, and reads a user name, a password
# and a start-up script from the last three.
foreach ($name in $Variables + 'SQLCMDUSER', 'SQLCMDPASSWORD', 'SQLCMDINI') { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }

# --- The tool, before anything is asked of Azure ---
if (-not (Test-Path -LiteralPath $SqlcmdPath -PathType Leaf)) {
    throw "There is no sqlcmd at $SqlcmdPath. Install go-sqlcmd (winget install --id Microsoft.Sqlcmd -e) or pass -SqlcmdPath. Nothing was run."
}
$signature = Get-ToolSignature $SqlcmdPath
if ($null -eq $signature) {
    Say "The signature of $SqlcmdPath is not checked on this system."
} else {
    $signer = if ($signature['Subject'] -match '(^|, )O=Microsoft Corporation(,|$)') { 'Microsoft' } else { 'not Microsoft' }
    if ($signature['Status'] -ne 'Valid' -or $signer -ne 'Microsoft') {
        throw "The signature of $SqlcmdPath is not a valid Microsoft one (status $($signature['Status']), signer $signer). This program is handed the administrator's sign-in: nothing was run."
    }
}
$help = Invoke-Tool @('-?')
if ($OdbcSignInName) {
    if ($help.ExitCode -ne 0 -or $help.Text -notmatch '\[-G ' -or $help.Text -notmatch '\[-X\[1\]') {
        throw "$SqlcmdPath is not the ODBC sqlcmd that -OdbcSignInName is for: its -? does not list [-G and [-X[1]. Nothing was run."
    }
    Say "Using the ODBC sqlcmd at $SqlcmdPath, signing in through a window."
} else {
    $version = Invoke-Tool @('--version')
    if ($version.ExitCode -ne 0 -or $version.Text -notmatch 'Version: v(?<number>\d+\.\d+\.\d+)' -or [version]$Matches['number'] -lt [version]'1.10.0') {
        throw "$SqlcmdPath is not go-sqlcmd 1.10.0 or later (the older ODBC sqlcmd answers --version with an error). Install it (winget install --id Microsoft.Sqlcmd -e) or pass -SqlcmdPath. Nothing was run."
    }
    $found = $Matches['number']
    if ($help.ExitCode -ne 0 -or $help.Text -notmatch "\b$AuthenticationMethod\b") {
        throw "The -? of $SqlcmdPath does not name $AuthenticationMethod. Try -AuthenticationMethod ActiveDirectoryDefault. Nothing was run."
    }
    Say "Using go-sqlcmd $found at $SqlcmdPath, signing in with $AuthenticationMethod."
}

$servers = @(Invoke-Az sql server list --resource-group $ResourceGroup)
if ($servers.Count -ne 1) { throw "Expected one SQL server in $ResourceGroup, found $($servers.Count)." }
$server = $servers[0]['name']
$fqdn = $servers[0]['fullyQualifiedDomainName']
if ($fqdn -notmatch '^[a-z0-9.-]+$') { throw 'The server has an unexpected host name. Nothing was run.' }

function Get-RuleNames {
    @(Invoke-Az sql server firewall-rule list --resource-group $ResourceGroup --server $server) | ForEach-Object { $_['name'] }
}

$script:deleteFailed = $false
function Remove-TemporaryRule {
    try {
        $null = Invoke-Az sql server firewall-rule delete --resource-group $ResourceGroup --server $server --name $RuleName
    } catch {
        $script:deleteFailed = $true
        Say "The firewall rule $RuleName could not be deleted: it may still be there, with this machine's address in it. Remove it by hand:"
        Say "  az sql server firewall-rule delete --resource-group $ResourceGroup --server $server --name $RuleName"
    }
}

# How the tool connects, whatever it then does. The certificate is checked: no -C.
$connect = @('-S', "tcp:$fqdn,1433", '-d', 'AzureBank', '-l', "$ConnectTimeout", '-b')
$encrypted = if ($OdbcSignInName) { @('-N') } else { @('-N', 'true') }
$signIn = if ($OdbcSignInName) { @('-G', '-U', $OdbcSignInName, '-X1') } else { @('--authentication-method', $AuthenticationMethod) }

function Test-Refused($Attempt) { $Attempt.ExitCode -ne 0 -and $Attempt.Text -match $FirewallRefusal }

function Test-Done($Attempt) {
    # A silence is not a pass: both users must have been printed, as the file prints them after its commit.
    if ($Attempt.ExitCode -ne 0) { return $false }
    foreach ($user in $Users.Keys) {
        if (@($Attempt.Text -split "`n" | Where-Object { $_.Trim() -eq "${user}: $($Users[$user]); ID as asked: 1" }).Count -ne 1) { return $false }
    }
    $true
}

function Show([string]$Text) {
    foreach ($line in $Text -split "`n") { if ($line.Trim()) { Say ('  ' + (Hide-Address $line.TrimEnd())) } }
}

$ruleCreated = $false
$notProven = $false
try {
    # The sweep: the first write of every run. A run whose terminal was closed, or whose machine
    # went to sleep, never reached the delete below.
    if ((Get-RuleNames) -contains $RuleName) {
        Say "A firewall rule $RuleName was left by an earlier run: deleting it before anything else."
        Remove-TemporaryRule
        if ($script:deleteFailed) { throw 'The rule left by an earlier run is still there. Nothing else was done.' }
    }

    $ids = @{ IdKind = $IdKind; CreateForm = $CreateForm }
    foreach ($identity in @('App', 'azurebank-app'), @('Migrator', 'azurebank-migrate')) {
        $read = Invoke-Az identity show --resource-group $ResourceGroup --name $identity[1]
        $ids["$($identity[0])ClientId"] = ConvertTo-Id ([string]$read['clientId']) "The client ID of $($identity[1])"
        $ids["$($identity[0])ObjectId"] = ConvertTo-Id ([string]$read['principalId']) "The object ID of $($identity[1])"
    }
    $run = $connect + $encrypted + $signIn + @('-i', (Join-Path $PSScriptRoot 'sql-principals.sql'), '-v') +
        @($Variables | ForEach-Object { "$_=$($ids[$_])" })

    $attempt = Invoke-Tool $run
    if (Test-Refused $attempt) {
        # Read from the server's own words, parsed, and printed again before it goes to az.
        $address = $null
        $null = $attempt.Text -match $FirewallRefusal
        $named = $Matches['address']
        if (-not [System.Net.IPAddress]::TryParse($named, [ref]$address) -or
            $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or $address.ToString() -ne $named) {
            Say 'The server refused this machine and named no IPv4 address this script can read. Allow the address by hand,'
            Say 'under another name than the rule this script sweeps, run this script again, then delete that rule:'
            Say "  az sql server firewall-rule create --resource-group $ResourceGroup --server $server --name $RuleName-by-hand --start-ip-address <address> --end-ip-address <address>"
            Say "  az sql server firewall-rule delete --resource-group $ResourceGroup --server $server --name $RuleName-by-hand"
            throw 'The address to allow could not be read from the refusal. Nothing was run.'
        }
        Say 'The server refused this address; allowing it until the users are created.'
        $null = Invoke-Az sql server firewall-rule create --resource-group $ResourceGroup --server $server `
            --name $RuleName --start-ip-address $address.ToString() --end-ip-address $address.ToString()
        $ruleCreated = $true
        $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
        while ($true) {
            $attempt = Invoke-Tool $run
            # Only "not let in yet" is waited out. Anything else is the file's own answer.
            if (-not (Test-Refused $attempt)) { break }
            if ([DateTime]::UtcNow -ge $deadline) { throw "The server still refused this address $WaitSeconds seconds after the rule was added." }
            Start-Sleep -Seconds $RetrySeconds
        }
    }
    Show $attempt.Text
    if ($attempt.ExitCode -ne 0) {
        throw "sqlcmd exited $($attempt.ExitCode): the file keeps nothing unless it reaches its end. After Msg 50003 or Msg 50004, run nothing else as administrator in this database (README.md)."
    }
    if (-not (Test-Done $attempt)) {
        throw 'sqlcmd exited 0 without printing both users with their roles and "ID as asked: 1": that is not a pass. Read what it printed above.'
    }

    if ($ProveSqlSignInRefused) {
        # A name and a value made up here, which exist nowhere. The value goes through the tool's
        # own environment variable, never an argument, and is removed at once.
        $nobody = 'nobody_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
        $method = if ($OdbcSignInName) { @() } else { @('--authentication-method', 'SqlPassword') }
        $env:SQLCMDPASSWORD = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        try { $proof = Invoke-Tool ($connect + $encrypted + $method + @('-U', $nobody, '-Q', 'SELECT 1')) }
        finally { Remove-Item -LiteralPath Env:SQLCMDPASSWORD -ErrorAction Ignore }
        if ($proof.ExitCode -eq 0) { throw 'A SQL sign-in with a made-up name was let in. Microsoft Entra-only authentication is not what this server enforces.' }
        Show $proof.Text
        if ($proof.Text -match $OnlyEntra) {
            Say 'Proved: a SQL sign-in is refused, and the server says it is because Microsoft Entra-only authentication is on.'
        } else {
            $notProven = $true
            Say 'Not proven: the SQL sign-in was refused, but not with the reason that names Microsoft Entra-only authentication.'
        }
    }
} catch {
    # Thrown again as it is. An error that is thrown stops whoever called this script; a failed
    # statement left to itself would end this script only, and a caller could go on to the next step.
    throw
} finally {
    if ($ruleCreated) { Remove-TemporaryRule }
    # Read back, whatever happened above: an absence is believed only when the list was read.
    try { $names = @(Get-RuleNames) }
    catch { throw "The firewall rules could not be read back: the temporary rule $RuleName may still be there. Look: az sql server firewall-rule list --resource-group $ResourceGroup --server $server" }
    Say "Firewall rules now: $($names -join ', ')."
    if ($names -contains $RuleName) { throw "The temporary rule $RuleName is still there. Remove it by hand: az sql server firewall-rule delete --resource-group $ResourceGroup --server $server --name $RuleName" }
    if ($script:deleteFailed) { throw "A delete of the temporary rule $RuleName failed. The list above does not show it, but run the delete by hand to be sure." }
}
if ($notProven) { throw 'The two users are as they should be. The refusal of a SQL sign-in was asked for and not proven.' }
