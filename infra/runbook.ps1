<#
.SYNOPSIS
  What the steps of infra/README.md share: where a run of main.bicep works, and the two functions
  that make one. Dot-source it from the repository root, in PowerShell 7:  . ./infra/runbook.ps1

.DESCRIPTION
  It sets $group, the resource group, and $folder, the folder infra/secrets.ps1 writes the
  parameter file into, and it tells the Azure CLI to refuse a command that lives in an extension
  instead of installing the extension: nothing here needs one.

  Invoke-Template makes one run of the template between `secrets.ps1 -Action New` and
  `secrets.ps1 -Action Remove`: it compiles infra/main.bicep, prints what a what-if would change
  and deploys only on the answer "yes". Show-WhatIf is how it prints that what-if.

  No test runs these functions: test_scripts.py holds only that this file parses.
#>
$env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = 'no'
$group  = 'azurebank-demo'
$folder = Join-Path $env:LOCALAPPDATA 'AzureBank\deploy'   # where secrets.ps1 writes

# What a what-if would change: the change and the type of each resource, and for a resource it
# would modify, the properties that differ. Never a name, a value or the subscription.
function Show-WhatIf([string]$File) {
    (Get-Content -LiteralPath $File -Raw | ConvertFrom-Json).changes | ForEach-Object {
        $parts = ($_.resourceId -split '/providers/')[-1] -split '/'
        $type = @($parts[0]) + @(for ($i = 1; $i -lt $parts.Count; $i += 2) { $parts[$i] })
        $paths = if ($_.changeType -eq 'Modify') { ': ' + (@($_.delta.path) -join ', ') } else { '' }
        '{0,-12} {1}{2}' -f $_.changeType, ($type -join '/'), $paths
    }
}

# One run of the template: compiled by the Bicep CLI on PATH, a what-if into the protected folder,
# its changes, then the deployment. The answer of the deployment is one word; without --query az
# prints the parameters back. $Override takes parameters such as 'denyPolicy=false'.
function Invoke-Template([string]$Name, [string[]]$Override = @()) {
    $template = "$folder\main.json"
    try {
        bicep build infra/main.bicep --outfile $template
        if ($LASTEXITCODE -ne 0) { throw 'The template did not compile.' }
        az deployment group what-if --resource-group $group --template-file $template `
            --parameters "@$folder\parameters.json" @Override --no-pretty-print --only-show-errors > "$folder\what-if.json"
        if ($LASTEXITCODE -ne 0) { throw 'The what-if failed.' }
        Show-WhatIf "$folder\what-if.json"
        if ((Read-Host 'Deploy this? (yes/no)') -ne 'yes') { return }
        az deployment group create --name $Name --resource-group $group --template-file $template `
            --parameters "@$folder\parameters.json" @Override --query properties.provisioningState --output tsv
    } finally {
        Remove-Item -LiteralPath $template -ErrorAction Ignore
    }
}
