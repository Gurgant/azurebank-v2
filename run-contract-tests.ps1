#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run Schemathesis contract tests for AzureBank API

.DESCRIPTION
    This script:
    1. Starts the API if not running
    2. Gets an authentication token
    3. Runs Schemathesis with custom hooks
    4. Reports results

.EXAMPLE
    ./run-contract-tests.ps1

.EXAMPLE
    ./run-contract-tests.ps1 -Verbose -MaxExamples 50
#>

param(
    [int]$MaxExamples = 100,
    [switch]$Verbose,
    [string]$BaseUrl = "http://localhost:5068"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " AzureBank Contract Testing            " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Step 1: Check if API is running
Write-Host "`n[1/4] Checking API availability..." -ForegroundColor Yellow
try {
    $null = Invoke-WebRequest -Uri "$BaseUrl/openapi/v1.json" -TimeoutSec 5
    Write-Host "  API is running at $BaseUrl" -ForegroundColor Green
} catch {
    Write-Host "  ERROR: API not available at $BaseUrl" -ForegroundColor Red
    Write-Host "  Start the API with: dotnet run --project src/AzureBank.Api" -ForegroundColor Yellow
    exit 1
}

# Step 2: Get authentication token
Write-Host "`n[2/4] Getting authentication token..." -ForegroundColor Yellow
try {
    $loginBody = '{"email":"john@example.com","password":"Test123!"}'
    $response = Invoke-RestMethod -Uri "$BaseUrl/api/auth/login" `
        -Method POST `
        -ContentType "application/json" `
        -Body $loginBody

    $token = $response.data.token

    if (-not $token) {
        throw "Token not found in response"
    }

    Write-Host "  Token acquired successfully" -ForegroundColor Green
} catch {
    Write-Host "  WARNING: Could not get token, testing unauthenticated" -ForegroundColor Yellow
    $token = $null
}

# Step 3: Set environment variables for hooks
Write-Host "`n[3/4] Configuring Schemathesis..." -ForegroundColor Yellow
$env:SCHEMATHESIS_HOOKS = "schemathesis.hooks"
$env:PYTHONPATH = (Get-Location).Path
Write-Host "  SCHEMATHESIS_HOOKS = schemathesis.hooks" -ForegroundColor Gray
Write-Host "  PYTHONPATH = $env:PYTHONPATH" -ForegroundColor Gray

# Step 4: Run Schemathesis
Write-Host "`n[4/4] Running Schemathesis tests..." -ForegroundColor Yellow
Write-Host "  Max examples: $MaxExamples" -ForegroundColor Gray

$schemathesisArgs = @(
    "run"
    "./docs/api/openapiv1.json"
    "--url", $BaseUrl
    "--hypothesis-seed=42"
    "--max-examples=$MaxExamples"
    "--workers=1"
)

if ($token) {
    $schemathesisArgs += @("-H", "Authorization: Bearer $token")
}

if ($Verbose) {
    $schemathesisArgs += "--verbosity=verbose"
}

Write-Host "`n" -NoNewline
Write-Host "Running: schemathesis $($schemathesisArgs -join ' ')" -ForegroundColor Gray
Write-Host ("-" * 60) -ForegroundColor Gray

# Execute Schemathesis
& schemathesis @schemathesisArgs

$exitCode = $LASTEXITCODE

# Report results
Write-Host "`n" -NoNewline
Write-Host ("=" * 60) -ForegroundColor Cyan
if ($exitCode -eq 0) {
    Write-Host "CONTRACT TESTS PASSED" -ForegroundColor Green
} else {
    Write-Host "CONTRACT TESTS FAILED (exit code: $exitCode)" -ForegroundColor Red
}
Write-Host ("=" * 60) -ForegroundColor Cyan

exit $exitCode
