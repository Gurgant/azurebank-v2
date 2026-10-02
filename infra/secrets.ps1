#Requires -Version 7.2
<#
.SYNOPSIS
  Writes, or removes, the one parameter file a run of main.bicep needs. PowerShell 7, Azure CLI signed in.

.DESCRIPTION
  -Action New writes parameters.json into a folder only the current user can open, outside the
  repository: %LOCALAPPDATA%\AzureBank\deploy on Windows, ~/.azurebank-deploy elsewhere.
    * Without -DeployApp: what the foundation needs (the Microsoft Entra administrator, a new SQL
      administrator password, deployApp=false).
    * With -DeployApp: also the image tag, the address the alerts write to, and the nine
      application secrets. Each secret comes from the first place that has it: the deployed app
      and job, then a file left by a run that stopped, then the system's random generator.
    * The SQL administrator password is new on every run and is kept nowhere: nothing signs in with it.
    * The alerts write to AZUREBANK_ALERT_EMAIL if that variable is set, else to the signed-in
      account's own mailbox.
    * "Could not read" is never taken for "absent": a failed az call, or a deployed app without
      one of its secrets, stops the script and no file is written.
    * Once the app exists its images move through the deploy workflow only: another -ImageTag is
      refused, and without one the running tag is written back.

  -Action Remove deletes parameters.json, what-if.json and budget.json by name, then the folder if
  nothing else is in it. It never deletes anything else, whatever -Directory says.

  Nothing is written to standard output, and no value is ever printed or put on a command line:
  the report on standard error names each value and says where it came from.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('New', 'Remove')][string]$Action,
    [string]$ResourceGroup = 'azurebank-demo',
    [switch]$DeployApp,
    [string]$ImageTag = '',
    [string]$Directory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppName = 'azurebank'
$JobName = 'azurebank-migrate'
$Api = '2025-01-01'
# Everything a session may leave in the folder. Remove deletes these and nothing else.
$SessionFiles = 'parameters.json', 'what-if.json', 'budget.json'

function Say([string]$Message) { [Console]::Error.WriteLine($Message) }

function New-Password {
    # Letters and digits only: no quoting anywhere, and all three classes SQL asks for.
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    do {
        $chars = for ($i = 0; $i -lt 48; $i++) {
            $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)]
        }
        $value = -join $chars
    } until ($value -cmatch '[A-Z]' -and $value -cmatch '[a-z]' -and $value -match '[0-9]')
    $value
}

function New-Key([int]$Bytes) {
    [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes))
}

function Invoke-Az {
    # Captures the answer; a failure stops the script. "Could not read" is never taken for "absent".
    # Arguments must stay free of & | ( ) < > ^ %: on Windows az is a .cmd and cmd.exe reads them.
    $output = & az @args --only-show-errors --output json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "az $($args[0]) $($args[1]) failed with exit code $LASTEXITCODE. Nothing was written." }
    if ($output) { ($output -join "`n") | ConvertFrom-Json -AsHashtable } else { $null }
}

function Protect-Directory([string]$Path) {
    $null = New-Item -ItemType Directory -Path $Path -Force
    if ($IsWindows) {
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $null = & icacls $Path /inheritance:r /grant:r "${me}:(OI)(CI)F" 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the folder.' }
        $others = @((Get-Acl -LiteralPath $Path).Access | Where-Object { $_.IdentityReference.Value -ne $me })
        if ($others.Count -ne 0) { throw 'The folder is still readable by someone else.' }
    } else {
        & chmod 700 $Path
        if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the folder.' }
    }
}

if (-not $Directory) {
    $Directory = if ($IsWindows) { Join-Path $env:LOCALAPPDATA 'AzureBank\deploy' } else { Join-Path $HOME '.azurebank-deploy' }
}
$File = Join-Path $Directory 'parameters.json'

if ($Action -eq 'Remove') {
    # By name, never recursively: a wrong -Directory must not be able to take a folder with it.
    foreach ($name in $SessionFiles) {
        $path = Join-Path $Directory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        if (Test-Path -LiteralPath $path) { throw "Still there: $path" }
    }
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        Say "Nothing to remove: $Directory does not exist."
        return
    }
    $left = @(Get-ChildItem -LiteralPath $Directory -Force)
    if ($left.Count -ne 0) {
        Say "Removed the session's files. $Directory holds $($left.Count) other item(s) and was left as it is."
        return
    }
    Remove-Item -LiteralPath $Directory -Force
    if (Test-Path -LiteralPath $Directory) { throw "Still there: $Directory" }
    Say "Removed $Directory."
    return
}

if ($DeployApp -and $ImageTag -and $ImageTag -cnotmatch '^[0-9a-f]{40}$') { throw 'ImageTag must be a full lowercase commit SHA.' }

Protect-Directory $Directory

$subscription = (Invoke-Az account show)['id']
$arm = 'https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.App/{2}?api-version={3}'
function Url([string]$Path) { $arm -f $subscription, $ResourceGroup, $Path, $Api }
$resources = @(Invoke-Az resource list --resource-group $ResourceGroup)
$appExists = @($resources | Where-Object { $_['type'] -eq 'Microsoft.App/containerApps' -and $_['name'] -eq $AppName }).Count -eq 1
$jobExists = @($resources | Where-Object { $_['type'] -eq 'Microsoft.App/jobs' -and $_['name'] -eq $JobName }).Count -eq 1

# What already exists, in order of authority: the deployed app and job, then a file left by a run that stopped.
$live = @{}
if ($DeployApp -and $appExists) {
    foreach ($s in (Invoke-Az rest --method POST --url (Url "containerApps/$AppName/listSecrets"))['value']) { $live[$s['name']] = $s['value'] }
    # Once the app exists its images move only through the deploy workflow, which migrates first.
    $app = Invoke-Az rest --method GET --url (Url "containerApps/$AppName")
    $bff = @($app['properties']['template']['containers'] | Where-Object { $_['name'] -eq 'bff' })[0]['image']
    $liveTag = ($bff -split ':')[-1]
    if ($ImageTag -and $ImageTag -ne $liveTag) { throw 'The app is deployed: its images move through the deploy workflow, not through this file. Nothing was written.' }
    $ImageTag = $liveTag
}
if ($DeployApp -and $jobExists) {
    foreach ($s in (Invoke-Az rest --method POST --url (Url "jobs/$JobName/listSecrets"))['value']) { $live[$s['name']] = $s['value'] }
}
$previous = @{}
if (Test-Path -LiteralPath $File) {
    $old = Get-Content -LiteralPath $File -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in $old['parameters'].Keys) { $previous[$name] = $old['parameters'][$name]['value'] }
}

function PasswordIn([string]$ConnectionString) {
    if ($ConnectionString -cmatch 'Password="([A-Za-z0-9]+)"') { $Matches[1] } else { $null }
}

# parameter -> the secret that holds it once deployed, on which resource, and how a new one is made
$plan = [ordered]@{
    appSqlPassword          = @{ Secret = 'app-connection';       On = $appExists; Password = $true; New = { New-Password } }
    migratorSqlPassword     = @{ Secret = 'migration-connection'; On = $jobExists; Password = $true; New = { New-Password } }
    jwtSecret               = @{ Secret = 'jwt-secret';           On = $appExists; New = { New-Key 64 } }
    idempotencyHashKey      = @{ Secret = 'idempotency-hash-key'; On = $appExists; New = { New-Key 32 } }
    stepUpBindingKey        = @{ Secret = 'stepup-binding-key';   On = $appExists; New = { New-Key 32 } }
    serviceCredentialBffKey = @{ Secret = 'service-key';          On = $appExists; New = { New-Key 48 } }
    auditChainKey           = @{ Secret = 'audit-chain-key';      On = $appExists; New = { New-Key 32 } }
    auditAnchorKey          = @{ Secret = 'audit-anchor-key';     On = $appExists; New = { New-Key 32 } }
    securityPinPepper       = @{ Secret = 'pin-pepper';           On = $appExists; New = { New-Key 48 } }
}

$me = Invoke-Az ad signed-in-user show
$parameters = [ordered]@{
    entraAdminObjectId = @{ value = $me['id'] }
    entraAdminLogin    = @{ value = $me['userPrincipalName'] }
    deployApp          = @{ value = [bool]$DeployApp }
    sqlAdminPassword   = @{ value = New-Password }
}
$report = [System.Collections.Generic.List[string]]::new()
$report.Add('sqlAdminPassword: new on every run, kept nowhere')

if ($DeployApp) {
    if ($ImageTag -cnotmatch '^[0-9a-f]{40}$') { throw 'Pass -ImageTag with the full SHA of the commit whose three images are published. Nothing was written.' }
    $parameters['imageTag'] = @{ value = $ImageTag }

    # Where the three alerts write. The report says which source, never the address.
    $alertEmail = $env:AZUREBANK_ALERT_EMAIL
    $alertSource = 'from AZUREBANK_ALERT_EMAIL'
    if (-not $alertEmail) {
        $alertEmail = if ($me.ContainsKey('mail')) { $me['mail'] } else { $null }
        $alertSource = "the signed-in account's own mailbox"
    }
    if (-not $alertEmail -or $alertEmail -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
        throw 'No usable address for the alerts: the signed-in account has no mailbox. Set AZUREBANK_ALERT_EMAIL and run again. Nothing was written.'
    }
    $parameters['alertEmail'] = @{ value = $alertEmail }
    $report.Add("alertEmail: $alertSource")

    foreach ($name in $plan.Keys) {
        $entry = $plan[$name]
        $value = $live[$entry.Secret]
        if ($value -and $entry.ContainsKey('Password')) { $value = PasswordIn $value }
        $source = 'kept from the deployed resource'
        if (-not $value -and $entry.On) {
            # A deployed resource without its secret is not a case to paper over with a new value.
            throw "$name could not be read from the deployed resource. Nothing was written."
        }
        if (-not $value -and $previous.ContainsKey($name) -and $previous[$name]) { $value = $previous[$name]; $source = 'kept from the earlier file' }
        if (-not $value) { $value = & $entry.New; $source = 'generated' }
        $parameters[$name] = @{ value = $value }
        $report.Add("${name}: $source")
    }
}

$document = [ordered]@{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = $parameters
}
[System.IO.File]::WriteAllText($File, ($document | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
if (-not $IsWindows) {
    & chmod 600 $File
    if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the file.' }
}
foreach ($line in $report) { Say $line }
Say "Wrote $File ($($parameters.Count) parameters). Remove it with -Action Remove once the deployment has finished."
