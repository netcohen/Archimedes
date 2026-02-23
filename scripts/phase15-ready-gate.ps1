#!/usr/bin/env pwsh
# Phase 15 Readiness Gate
# Orchestrates Phase 14 gate + Phase 15 tests. FAIL on any required step.

param(
    [int]$SoakHours = 0,
    [switch]$IncludePhase14Soak = $false
)

$ErrorActionPreference = "Stop"

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Split-Path -Parent $scriptDir
if (-not $repoRoot -or -not (Test-Path $repoRoot)) { $repoRoot = Get-Location }
Set-Location $repoRoot | Out-Null

function Get-Timestamp { return Get-Date -Format "yyyy-MM-dd HH:mm:ss" }

function Invoke-Step {
    param([string]$Name, [string]$ScriptPath, [hashtable]$ScriptParams = @{}, [switch]$Required)
    $fullPath = if ([System.IO.Path]::IsPathRooted($ScriptPath)) { $ScriptPath } else { Join-Path $scriptDir $ScriptPath }
    if (-not (Test-Path $fullPath)) {
        Write-Host "[$(Get-Timestamp)] FAIL: $Name - Script not found" -ForegroundColor Red
        if ($Required) { throw "Required script missing: $fullPath" }
        return $true
    }
    Write-Host "[$(Get-Timestamp)] RUN:  $Name" -ForegroundColor Cyan
    $exitCode = 0
    try {
        if ($ScriptParams -and $ScriptParams.Count -gt 0) {
            & $fullPath @ScriptParams
        } else {
            & $fullPath
        }
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) { $exitCode = 0 }
    } catch {
        $exitCode = 1
        Write-Host "[$(Get-Timestamp)] FAIL: $Name - $($_.Exception.Message)" -ForegroundColor Red
    }
    if ($exitCode -eq 0) {
        Write-Host "[$(Get-Timestamp)] PASS: $Name" -ForegroundColor Green
        return $true
    }
    Write-Host "[$(Get-Timestamp)] FAIL: $Name (exit $exitCode)" -ForegroundColor Red
    if ($Required) { throw "Gate failed: $Name (exit $exitCode)" }
    return $false
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phase 15 Readiness Gate" -ForegroundColor Cyan
Write-Host "  Soak: $SoakHours h, Phase14Soak: $IncludePhase14Soak" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$gateStart = Get-Date

try {
    Invoke-Step -Name "check-no-secrets.ps1" -ScriptPath "check-no-secrets.ps1" -Required | Out-Null

    $phase14Soak = if ($IncludePhase14Soak) { $SoakHours } else { 0 }
    Invoke-Step -Name "phase14-ready-gate.ps1 (SoakHours=$phase14Soak)" -ScriptPath "phase14-ready-gate.ps1" -ScriptParams @{ SoakHours = $phase14Soak } -Required | Out-Null

    Invoke-Step -Name "phase15-storage.ps1" -ScriptPath "phase15-storage.ps1" -Required | Out-Null
    Invoke-Step -Name "phase15-selfupdate.ps1" -ScriptPath "phase15-selfupdate.ps1" -Required | Out-Null
} catch {
    $duration = (Get-Date) - $gateStart
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  GATE FAILED" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Duration: $($duration.TotalMinutes.ToString('F1')) min" -ForegroundColor Gray
    Write-Host "========================================" -ForegroundColor Red
    exit 1
}

$duration = (Get-Date) - $gateStart
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  GATE PASSED" -ForegroundColor Green
Write-Host "  Duration: $($duration.TotalMinutes.ToString('F1')) min" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Green
exit 0
