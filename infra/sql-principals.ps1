#Requires -Version 7.2
<#
.SYNOPSIS
  Creates the two database users from the passwords in the parameter file secrets.ps1 wrote.

.DESCRIPTION
  Signs in to the AzureBank database as the server's Microsoft Entra administrator, with a token from
  the Azure CLI session, and runs sql-principals.sql with the two passwords as bound parameters.

  The server lets in Azure services only, so this machine's address is allowed for the length of the
  run, by a firewall rule named owner-while-creating-users. The address is the one the server itself
  reports when it refuses the first connection.
    * First, before anything else: a rule of that name left by a run that died is deleted.
    * At the end, whatever happened: the rule is deleted, and the rule list is read back.
    * A delete that fails says that the rule may still be there, prints the command that removes it,
      and the script exits non-zero.

  Nothing is written to standard output; the report on standard error holds no password and no address.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'azurebank-demo',
    [string]$ParameterFile = '',
    [string]$RuleName = 'owner-while-creating-users',
    [ValidateRange(1, 120)][int]$ConnectTimeout = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Say([string]$Message) { [Console]::Error.WriteLine($Message) }

function Invoke-Az {
    # Arguments must stay free of & | ( ) < > ^ %: on Windows az is a .cmd and cmd.exe reads them.
    $output = & az @args --only-show-errors --output json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "az $($args[0]) $($args[1]) $($args[2]) failed with exit code $LASTEXITCODE." }
    if ($output) { ($output -join "`n") | ConvertFrom-Json -AsHashtable } else { $null }
}

if (-not $ParameterFile) {
    $folder = if ($IsWindows) { Join-Path $env:LOCALAPPDATA 'AzureBank\deploy' } else { Join-Path $HOME '.azurebank-deploy' }
    $ParameterFile = Join-Path $folder 'parameters.json'
}
if (-not (Test-Path -LiteralPath $ParameterFile -PathType Leaf)) { throw 'There is no parameter file: write it with secrets.ps1 -Action New -DeployApp.' }
$values = (Get-Content -LiteralPath $ParameterFile -Raw | ConvertFrom-Json -AsHashtable)['parameters']
foreach ($name in 'appSqlPassword', 'migratorSqlPassword') {
    if (-not $values.ContainsKey($name)) { throw "The parameter file has no ${name}: write it with secrets.ps1 -Action New -DeployApp." }
}

$servers = @(Invoke-Az sql server list --resource-group $ResourceGroup)
if ($servers.Count -ne 1) { throw "Expected one SQL server in $ResourceGroup, found $($servers.Count)." }
$server = $servers[0]['name']
$fqdn = $servers[0]['fullyQualifiedDomainName']

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

# The one door to the database. The tests put a stand-in behind this name (test_scripts.py), so
# everything else in this file runs offline: keep every use of SqlClient inside it.
function Open-Database([string]$Token) {
    $connection = [System.Data.SqlClient.SqlConnection]::new(
        "Server=tcp:$fqdn,1433;Database=AzureBank;Encrypt=True;TrustServerCertificate=False;Connect Timeout=$ConnectTimeout")
    $connection.AccessToken = $Token
    $connection.Open()
    $connection
}

$ruleCreated = $false
$connection = $null
try {
    # The sweep: the first write of every run. A run whose terminal was closed, or whose machine
    # went to sleep, never reached the delete below.
    if ((Get-RuleNames) -contains $RuleName) {
        Say "A firewall rule $RuleName was left by an earlier run: deleting it before anything else."
        Remove-TemporaryRule
        if ($script:deleteFailed) { throw 'The rule left by an earlier run is still there. Nothing else was done.' }
    }

    $token = (Invoke-Az account get-access-token --resource https://database.windows.net/)['accessToken']
    try {
        $connection = Open-Database $token
    } catch {
        $refusal = $_.Exception.GetBaseException()
        # 40615: "Client with IP address '...' is not allowed to access the server." Read by its
        # number, as SqlException carries it, not by its type.
        $number = $refusal.PSObject.Properties['Number']
        if (-not $number -or $number.Value -ne 40615 -or $refusal.Message -notmatch "IP address '([0-9.]+)'") {
            # The server's own words, without any address they may carry.
            throw ('Could not connect to the database, and the server did not name an address to allow: ' +
                ($refusal.Message -replace '\b\d{1,3}(\.\d{1,3}){3}\b', '<address>' -replace "IP address '[^']*'", "IP address '<address>'"))
        }
        $address = $Matches[1]
        Say 'The server refused this address; allowing it until the users are created.'
        $null = Invoke-Az sql server firewall-rule create --resource-group $ResourceGroup --server $server `
            --name $RuleName --start-ip-address $address --end-ip-address $address
        $ruleCreated = $true
        $deadline = [DateTime]::UtcNow.AddMinutes(5)
        while (-not $connection) {
            try { $connection = Open-Database $token }
            catch { if ([DateTime]::UtcNow -gt $deadline) { throw 'The server still refused the connection five minutes after the rule was added.' }; Start-Sleep -Seconds 10 }
        }
    }

    $command = $connection.CreateCommand()
    $command.CommandText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sql-principals.sql') -Raw
    $command.CommandTimeout = 60
    $null = $command.Parameters.Add('@AppPassword', [System.Data.SqlDbType]::NVarChar, 4000)
    $null = $command.Parameters.Add('@MigratorPassword', [System.Data.SqlDbType]::NVarChar, 4000)
    $command.Parameters['@AppPassword'].Value = $values['appSqlPassword']['value']
    $command.Parameters['@MigratorPassword'].Value = $values['migratorSqlPassword']['value']
    $reader = $command.ExecuteReader()
    $found = @{}
    while ($reader.Read()) { $found[[string]$reader['user']] = [string]$reader['roles']; Say ("{0}: {1}" -f $reader['user'], $reader['roles']) }
    $reader.Close()
    $expected = @{ azurebank_app = 'db_datareader, db_datawriter'; azurebank_migrator = 'db_datareader, db_datawriter, db_ddladmin' }
    foreach ($user in $expected.Keys) {
        if ($found[$user] -ne $expected[$user]) { throw "$user should hold [$($expected[$user])] and holds [$($found[$user])]." }
    }
} catch {
    # Thrown again as it is. An error that is thrown stops whoever called this script; a failed
    # statement left to itself would end this script only, and a caller could go on to the next step.
    throw
} finally {
    if ($connection) { $connection.Dispose() }
    if ($ruleCreated) { Remove-TemporaryRule }
    # Read back, whatever happened above: an absence is believed only when the list was read.
    try { $names = @(Get-RuleNames) }
    catch { throw "The firewall rules could not be read back: the temporary rule $RuleName may still be there. Look: az sql server firewall-rule list --resource-group $ResourceGroup --server $server" }
    Say "Firewall rules now: $($names -join ', ')."
    if ($names -contains $RuleName) { throw "The temporary rule $RuleName is still there. Remove it by hand: az sql server firewall-rule delete --resource-group $ResourceGroup --server $server --name $RuleName" }
    if ($script:deleteFailed) { throw "A delete of the temporary rule $RuleName failed. The list above does not show it, but run the delete by hand to be sure." }
}
