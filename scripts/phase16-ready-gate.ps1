#!/usr/bin/env pwsh
# Phase 16 Readiness Gate
# Runs Phase 15 gate first, then Phase 16 self-update happy path.
# FAIL on first required failure.

param(
    [int]$SoakHours = 0,
    [switch]$IncludePhase14Soak = $false
)

$ErrorActionPreference = "Stop"

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Split-Path -Parent $scriptDir
if (-not $repoRoot -or -not (Test-Path $repoRoot)) { $repoRoot = (Get-Location).Path }
Set-Location $repoRoot | Out-Null

if ($IncludePhase14Soak -and -not $PSBoundParameters.ContainsKey('SoakHours')) { $SoakHours = 8 }

$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }
$netUrl = if ($env:ARCHIMEDES_NET_URL) { $env:ARCHIMEDES_NET_URL } else { "http://localhost:5052" }

$logDir = Join-Path $repoRoot "logs\gates"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir "phase16-gate-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"

function Write-Gate {
    param([string]$Text, [string]$Color = "White")
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Text"
    Write-Host $line -ForegroundColor $Color
    Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
}

function Invoke-Step {
    param([string]$Name, [string]$ScriptPath, [hashtable]$ScriptParams = @{}, [switch]$Required)
    $fullPath = if ([System.IO.Path]::IsPathRooted($ScriptPath)) { $ScriptPath } else { Join-Path $scriptDir $ScriptPath }
    if (-not (Test-Path $fullPath)) {
        Write-Gate "FAIL: $Name - Script not found" "Red"
        if ($Required) { throw "Required script missing: $fullPath" }
        return $false
    }
    Write-Gate "RUN:  $Name" "Cyan"
    $exitCode = 0
    try {
        $env:ARCHIMEDES_CORE_URL = $coreUrl
        $env:ARCHIMEDES_NET_URL = $netUrl
        if ($ScriptParams -and $ScriptParams.Count -gt 0) { & $fullPath @ScriptParams } else { & $fullPath }
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

Write-Gate "# Quick: .\scripts\phase16-ready-gate.ps1 -SoakHours 0" "DarkGray"
Write-Gate "# Full:  .\scripts\phase16-ready-gate.ps1 -IncludePhase14Soak -SoakHours 8" "DarkGray"
Write-Gate "INFO: Log: $logFile" "Gray"
Write-Gate "" "White"

Write-Gate "Phase 16 Readiness Gate - Soak: $SoakHours h, Phase14Soak: $IncludePhase14Soak" "Cyan"

try {
    $r = Invoke-WebRequest -Uri "$coreUrl/health" -Method Get -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($r.StatusCode -ne 200) { throw "Core /health returned $($r.StatusCode)" }
} catch {
    Write-Gate "FAIL: Preflight - Core not reachable at $coreUrl" "Red"
    exit 1
}
try {
    $r = Invoke-WebRequest -Uri "$netUrl/health" -Method Get -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($r.StatusCode -ne 200) { throw "Net /health returned $($r.StatusCode)" }
} catch {
    Write-Gate "FAIL: Preflight - Net not reachable at $netUrl" "Red"
    exit 1
}
Write-Gate "INFO: Preflight PASS" "Green"

$gateStart = Get-Date

try {
    Invoke-Step -Name "phase15-ready-gate.ps1" -ScriptPath "phase15-ready-gate.ps1" -ScriptParams @{ SoakHours = $SoakHours; IncludePhase14Soak = $IncludePhase14Soak } -Required | Out-Null
    Invoke-Step -Name "phase16-selfupdate.ps1" -ScriptPath "phase16-selfupdate.ps1" -Required | Out-Null
} catch {
    $duration = (Get-Date) - $gateStart
    Write-Gate "GATE FAILED - $($_.Exception.Message)" "Red"
    Write-Gate "Duration: $($duration.TotalMinutes.ToString('F1')) min" "Gray"
    exit 1
}

$duration = (Get-Date) - $gateStart
Write-Gate "" "White"
Write-Gate "GATE PASSED - Duration: $($duration.TotalMinutes.ToString('F1')) min" "Green"
Write-Gate "" "White"
Write-Gate "Commands to run next:" "Cyan"
Write-Gate "  .\scripts\phase16-ready-gate.ps1 -SoakHours 0" "White"
Write-Gate "  .\scripts\phase16-ready-gate.ps1 -IncludePhase14Soak -SoakHours 8" "White"
Write-Gate "" "White"
exit 0
