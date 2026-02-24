#!/usr/bin/env pwsh
# Phase 15 Readiness Gate
# Orchestrates Phase 14 gate + Phase 15 tests. FAIL on first required failure.

param(
    [int]$SoakHours = 0,
    [switch]$IncludePhase14Soak = $false
)

$ErrorActionPreference = "Stop"

# Resolve repo root
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Split-Path -Parent $scriptDir
if (-not $repoRoot -or -not (Test-Path $repoRoot)) { $repoRoot = (Get-Location).Path }
Set-Location $repoRoot | Out-Null

# Default SoakHours to 8 when -IncludePhase14Soak and -SoakHours not specified
if ($IncludePhase14Soak -and -not $PSBoundParameters.ContainsKey('SoakHours')) { $SoakHours = 8 }

# URLs from env (same contract as Phase 15)
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }
$netUrl = if ($env:ARCHIMEDES_NET_URL) { $env:ARCHIMEDES_NET_URL } else { "http://localhost:5052" }

# Log file
$logDir = Join-Path $repoRoot "logs\gates"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir "phase15-gate-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"

function Get-Timestamp { return Get-Date -Format "yyyy-MM-dd HH:mm:ss" }

function Write-Gate {
    param([string]$Text, [string]$Color = "White")
    $line = "[$(Get-Timestamp)] $Text"
    Write-Host $line -ForegroundColor $Color
    Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
}

function Invoke-Step {
    param([string]$Name, [string]$ScriptPath, [hashtable]$ScriptParams = @{}, [switch]$Required)
    $fullPath = if ([System.IO.Path]::IsPathRooted($ScriptPath)) { $ScriptPath } else { Join-Path $scriptDir $ScriptPath }
    if (-not (Test-Path $fullPath)) {
        Write-Gate "FAIL: $Name - Script not found" "Red"
        if ($Required) { throw "Required script missing: $fullPath" }
        return $true
    }
    Write-Gate "RUN:  $Name" "Cyan"
    $exitCode = 0
    try {
        $env:ARCHIMEDES_CORE_URL = $coreUrl
        $env:ARCHIMEDES_NET_URL = $netUrl
        if ($ScriptParams -and $ScriptParams.Count -gt 0) {
            & $fullPath @ScriptParams
        } else {
            & $fullPath
        }
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) { $exitCode = 0 }
    } catch {
        $exitCode = 1
        Write-Gate "FAIL: $Name - $($_.Exception.Message)" "Red"
    }
    if ($exitCode -eq 0) {
        Write-Gate "PASS: $Name" "Green"
        return $true
    }
    Write-Gate "FAIL: $Name (exit $exitCode)" "Red"
    if ($Required) { throw "Gate failed: $Name (exit $exitCode)" }
    return $false
}

# HOW TO RUN (commented so not executable if pasted)
Write-Gate "# Quick: .\scripts\phase15-ready-gate.ps1 -SoakHours 0" "DarkGray"
Write-Gate "# Full:  .\scripts\phase15-ready-gate.ps1 -IncludePhase14Soak -SoakHours 8" "DarkGray"
Write-Gate "INFO: Log file: $logFile" "Gray"
Write-Gate "" "White"

Write-Gate "INFO: Phase 15 Readiness Gate - Soak: $SoakHours h, Phase14Soak: $IncludePhase14Soak, Repo: $repoRoot" "Cyan"
Write-Gate "INFO: Preflight: verifying Core and Net are reachable..." "Cyan"

# Preflight: Core and Net reachable
try {
    $coreResp = Invoke-WebRequest -Uri "$coreUrl/health" -Method Get -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($coreResp.StatusCode -ne 200) { throw "Core /health returned $($coreResp.StatusCode)" }
    Write-Gate "INFO: Preflight PASS: Core reachable at $coreUrl" "Green"
} catch {
    Write-Gate "FAIL: Preflight - Core not reachable at $coreUrl - $($_.Exception.Message)" "Red"
    Write-Gate "INFO: Ensure Core is running: cd core; dotnet run" "Gray"
    exit 1
}
try {
    $netResp = Invoke-WebRequest -Uri "$netUrl/health" -Method Get -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($netResp.StatusCode -ne 200) { throw "Net /health returned $($netResp.StatusCode)" }
    Write-Gate "INFO: Preflight PASS: Net reachable at $netUrl" "Green"
} catch {
    Write-Gate "FAIL: Preflight - Net not reachable at $netUrl - $($_.Exception.Message)" "Red"
    Write-Gate "INFO: Ensure Net is running: cd net; npm start" "Gray"
    exit 1
}

$gateStart = Get-Date

try {
    Invoke-Step -Name "check-no-secrets.ps1" -ScriptPath "check-no-secrets.ps1" -Required | Out-Null

    $phase14Soak = if ($IncludePhase14Soak) { $SoakHours } else { 0 }
    Invoke-Step -Name "phase14-ready-gate.ps1 (SoakHours=$phase14Soak)" -ScriptPath "phase14-ready-gate.ps1" -ScriptParams @{ SoakHours = $phase14Soak } -Required | Out-Null

    Invoke-Step -Name "phase15-storage.ps1" -ScriptPath "phase15-storage.ps1" -Required | Out-Null
    Invoke-Step -Name "phase15-selfupdate.ps1" -ScriptPath "phase15-selfupdate.ps1" -Required | Out-Null
} catch {
    $duration = (Get-Date) - $gateStart
    Write-Gate "GATE FAILED - $($_.Exception.Message)" "Red"
    Write-Gate "Duration: $($duration.TotalMinutes.ToString('F1')) min" "Gray"
    exit 1
}

$duration = (Get-Date) - $gateStart
Write-Gate "GATE PASSED - Duration: $($duration.TotalMinutes.ToString('F1')) min" "Green"
Write-Gate "INFO: Log written to $logFile" "Gray"
Write-Gate "" "White"
Write-Gate "Commands to run next:" "Cyan"
Write-Gate "  .\scripts\phase15-ready-gate.ps1 -SoakHours 0" "White"
Write-Gate "  .\scripts\phase15-ready-gate.ps1 -IncludePhase14Soak -SoakHours 8" "White"
Write-Gate "" "White"
exit 0
