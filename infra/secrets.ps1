#Requires -Version 7.2
<#
.SYNOPSIS
  Writes, or removes, the one parameter file a run of main.bicep needs. PowerShell 7, Azure CLI signed in.

.DESCRIPTION
  -Action New writes parameters.json into a folder only the current user can open, outside the
  repository: %LOCALAPPDATA%\AzureBank\deploy on Windows, ~/.azurebank-deploy elsewhere.
    * Without -DeployApp: what the foundation needs (the Microsoft Entra administrator and
      deployApp=false). That file holds no secret; it stays in the folder because the
      administrator's sign-in name is shaped like an e-mail address.
    * With -DeployApp: also the image tag, the address the alerts write to, and the seven
      application secrets. Each secret comes from the first place that has it: the deployed app,
      then a file left by a run that stopped, then the system's random generator.
    * No database password is written, because none exists: the app and the migrate job sign in
      as managed identities.
    * The alerts write to -AlertEmail; without it to AZUREBANK_ALERT_EMAIL; without that to the
      address the deployed alerts already write to; and only then to the signed-in account's own
      mailbox.
    * keepLogs is what the deployed environment does now: true if it sends its logs to Azure
      Monitor, false if it sends them nowhere. -LogsOff writes false whatever is deployed. With no
      environment yet and no -LogsOff the template's own default applies.
    * "Could not read" is never taken for "absent": a failed az call, or a deployed app without
      one of its secrets, stops the script and no file is written.
    * Once the app exists its images move through the deploy workflow only: another -ImageTag is
      refused, and without one the running tag is written back.

  -Action Remove deletes parameters.json, what-if.json and budget.json by name, then the folder if
  nothing else is in it. It never deletes anything else, whatever -Directory says.

  Nothing is written to standard output, and no secret is ever printed or put on a command line:
  the report on standard error names each value and says where it came from, never what it is.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('New', 'Remove')][string]$Action,
    [string]$ResourceGroup = 'azurebank-demo',
    [switch]$DeployApp,
    [string]$ImageTag = '',
    [string]$AlertEmail = '',
    [switch]$LogsOff,
    [string]$Directory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppName = 'azurebank'
$EnvironmentName = 'azurebank-env'
$AlertGroupName = 'azurebank-owner'
$Api = '2025-01-01'
# The environment is read with the API version main.bicep creates it with.
$EnvironmentApi = '2026-07-01'
# Everything a session may leave in the folder. Remove deletes these and nothing else.
$SessionFiles = 'parameters.json', 'what-if.json', 'budget.json'

function Say([string]$Message) { [Console]::Error.WriteLine($Message) }

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

$mailbox = '^[^@\s]+@[^@\s]+\.[^@\s]+$'
if ($DeployApp -and $ImageTag -and $ImageTag -cnotmatch '^[0-9a-f]{40}$') { throw 'ImageTag must be a full lowercase commit SHA.' }
if ($AlertEmail -and $AlertEmail -notmatch $mailbox) { throw '-AlertEmail is not an e-mail address. Nothing was written.' }

Protect-Directory $Directory

$subscription = (Invoke-Az account show)['id']
$arm = 'https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/{2}?api-version={3}'
function Url([string]$Path, [string]$Version = $Api) { $arm -f $subscription, $ResourceGroup, $Path, $Version }
$resources = @(Invoke-Az resource list --resource-group $ResourceGroup)
# Azure does not promise the case of a type name.
function Deployed([string]$Type, [string]$Name) {
    @($resources | Where-Object { $_['type'] -ieq $Type -and $_['name'] -eq $Name }).Count -eq 1
}
$appExists = Deployed 'Microsoft.App/containerApps' $AppName

$report = [System.Collections.Generic.List[string]]::new()

# What the environment does with its logs now is kept: a later run must not bring back a workspace
# that was switched off, nor switch off one that is in use.
$keepLogs = $null
if ($LogsOff) {
    $keepLogs = $false
    $report.Add('keepLogs: false, asked for with -LogsOff')
} elseif (Deployed 'Microsoft.App/managedEnvironments' $EnvironmentName) {
    $environment = Invoke-Az rest --method GET --url (Url "Microsoft.App/managedEnvironments/$EnvironmentName" $EnvironmentApi)
    $logs = $environment['properties']['appLogsConfiguration']
    $destination = if ($logs -and $logs.ContainsKey('destination')) { $logs['destination'] } else { $null }
    if ($destination -eq 'azure-monitor') { $keepLogs = $true }
    elseif (-not $destination -or $destination -eq 'none') { $keepLogs = $false }
    else { throw "The deployed environment sends its logs to '$destination', which this template never sets. Nothing was written." }
    $report.Add('keepLogs: kept from the deployed resource')
} else {
    $report.Add("keepLogs: not written, the template's default applies")
}

# What already exists, in order of authority: the deployed app, then a file left by a run that stopped.
$live = @{}
if ($DeployApp -and $appExists) {
    foreach ($s in (Invoke-Az rest --method POST --url (Url "Microsoft.App/containerApps/$AppName/listSecrets"))['value']) { $live[$s['name']] = $s['value'] }
    # Once the app exists its images move only through the deploy workflow, which migrates first.
    $app = Invoke-Az rest --method GET --url (Url "Microsoft.App/containerApps/$AppName")
    $bff = @($app['properties']['template']['containers'] | Where-Object { $_['name'] -eq 'bff' })[0]['image']
    $liveTag = ($bff -split ':')[-1]
    if ($ImageTag -and $ImageTag -ne $liveTag) { throw 'The app is deployed: its images move through the deploy workflow, not through this file. Nothing was written.' }
    $ImageTag = $liveTag
}
$previous = @{}
if (Test-Path -LiteralPath $File) {
    $old = Get-Content -LiteralPath $File -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in $old['parameters'].Keys) { $previous[$name] = $old['parameters'][$name]['value'] }
}

# parameter -> the secret of the app that holds it once deployed, and how a new one is made
$plan = [ordered]@{
    jwtSecret               = @{ Secret = 'jwt-secret';           New = { New-Key 64 } }
    idempotencyHashKey      = @{ Secret = 'idempotency-hash-key'; New = { New-Key 32 } }
    stepUpBindingKey        = @{ Secret = 'stepup-binding-key';   New = { New-Key 32 } }
    serviceCredentialBffKey = @{ Secret = 'service-key';          New = { New-Key 48 } }
    auditChainKey           = @{ Secret = 'audit-chain-key';      New = { New-Key 32 } }
    auditAnchorKey          = @{ Secret = 'audit-anchor-key';     New = { New-Key 32 } }
    securityPinPepper       = @{ Secret = 'pin-pepper';           New = { New-Key 48 } }
}

$me = Invoke-Az ad signed-in-user show
$parameters = [ordered]@{
    entraAdminObjectId = @{ value = $me['id'] }
    entraAdminLogin    = @{ value = $me['userPrincipalName'] }
    deployApp          = @{ value = [bool]$DeployApp }
}
if ($null -ne $keepLogs) { $parameters['keepLogs'] = @{ value = $keepLogs } }

if ($DeployApp) {
    if ($ImageTag -cnotmatch '^[0-9a-f]{40}$') { throw 'Pass -ImageTag with the full SHA of the commit whose three images are published. Nothing was written.' }
    $parameters['imageTag'] = @{ value = $ImageTag }

    # Where the alerts write. The report says which source, never the address.
    $address = $AlertEmail
    $source = 'from -AlertEmail'
    if (-not $address) {
        $address = $env:AZUREBANK_ALERT_EMAIL
        $source = 'from AZUREBANK_ALERT_EMAIL'
    }
    if (-not $address -and (Deployed 'Microsoft.Insights/actionGroups' $AlertGroupName)) {
        $group = Invoke-Az rest --method GET --url (Url "Microsoft.Insights/actionGroups/$AlertGroupName" '2023-01-01')
        $receivers = @($group['properties']['emailReceivers'])
        if ($receivers.Count -ne 1) { throw "The deployed alerts write to $($receivers.Count) addresses, not one. Pass -AlertEmail. Nothing was written." }
        $address = $receivers[0]['emailAddress']
        $source = 'kept from the deployed resource'
    }
    if (-not $address) {
        $address = if ($me.ContainsKey('mail')) { $me['mail'] } else { $null }
        $source = "the signed-in account's own mailbox"
    }
    if (-not $address -or $address -notmatch $mailbox) {
        throw 'No usable address for the alerts: the signed-in account has no mailbox. Pass -AlertEmail or set AZUREBANK_ALERT_EMAIL. Nothing was written.'
    }
    $parameters['alertEmail'] = @{ value = $address }
    $report.Add("alertEmail: $source")

    foreach ($name in $plan.Keys) {
        $entry = $plan[$name]
        $value = $live[$entry.Secret]
        $source = 'kept from the deployed resource'
        if (-not $value -and $appExists) {
            # A deployed app without one of its secrets is not a case to paper over with a new value.
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
