#!/usr/bin/env pwsh
# Phase 14 Readiness Gate
# Orchestrates existing validation scripts. Gate PASS only if ALL required steps pass.
# Run before Phase 15. Includes 8-hour soak test.

param(
    [int]$SoakHours = 8
)

$ErrorActionPreference = "Stop"

# Resolve repo root (parent of scripts folder)
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Split-Path -Parent $scriptDir
if (-not $repoRoot -or -not (Test-Path $repoRoot)) {
    $repoRoot = Get-Location
}
Set-Location $repoRoot | Out-Null

function Get-Timestamp { return Get-Date -Format "yyyy-MM-dd HH:mm:ss" }

function Invoke-Step {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [hashtable]$ScriptParams = @{},
        [switch]$Required,
        [switch]$Optional
    )
    
    $fullPath = if ([System.IO.Path]::IsPathRooted($ScriptPath)) { $ScriptPath } else { Join-Path $scriptDir $ScriptPath }
    
    if (-not (Test-Path $fullPath)) {
        Write-Host "[$(Get-Timestamp)] FAIL: $Name - Script not found: $fullPath" -ForegroundColor Red
        if ($Required) {
            throw "Required script missing: $fullPath"
        }
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
    } else {
        if ($Optional) {
            Write-Host "[$(Get-Timestamp)] WARNING: $Name (exit $exitCode) - Ollama may be unavailable, continuing" -ForegroundColor Yellow
        } else {
            Write-Host "[$(Get-Timestamp)] FAIL: $Name (exit $exitCode)" -ForegroundColor Red
            if ($Required) {
                throw "Gate failed at step: $Name (exit $exitCode)"
            }
        }
        return $false
    }
}

# ========== Main ==========
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phase 14 Readiness Gate" -ForegroundColor Cyan
Write-Host "  Soak duration: $SoakHours hours" -ForegroundColor Gray
Write-Host "  Repo root: $repoRoot" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$gateStart = Get-Date

# OPTIONAL (informational) - LLM smoke - must NOT fail the gate
Write-Host "--- Optional: LLM Smoke ---" -ForegroundColor Yellow
Invoke-Step -Name "llm-smoke.ps1 (informational)" -ScriptPath "llm-smoke.ps1" -Optional | Out-Null
Write-Host ""

# REQUIRED steps - stop on first failure
Write-Host "--- Required Steps ---" -ForegroundColor Cyan

try {
    Invoke-Step -Name "check-no-secrets.ps1" -ScriptPath "check-no-secrets.ps1" -Required:$true | Out-Null
    Invoke-Step -Name "phase14-security.ps1" -ScriptPath "phase14-security.ps1" -Required:$true | Out-Null
    Invoke-Step -Name "phase14-e2e.ps1" -ScriptPath "phase14-e2e.ps1" -Required:$true | Out-Null
    Invoke-Step -Name "phase14-chaos.ps1" -ScriptPath "phase14-chaos.ps1" -Required:$true | Out-Null
    Invoke-Step -Name "e2e.ps1" -ScriptPath "e2e.ps1" -Required:$true | Out-Null
    Invoke-Step -Name "run-soak.ps1 (-DurationHours $SoakHours)" -ScriptPath "run-soak.ps1" -ScriptParams @{ DurationHours = $SoakHours } -Required:$true | Out-Null
} catch {
    $duration = (Get-Date) - $gateStart
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  GATE FAILED" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Duration: $($duration.TotalMinutes.ToString('F1')) minutes" -ForegroundColor Gray
    Write-Host "========================================" -ForegroundColor Red
    exit 1
}

$duration = (Get-Date) - $gateStart
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  GATE PASSED" -ForegroundColor Green
Write-Host "  All required steps completed" -ForegroundColor Green
Write-Host "  Duration: $($duration.TotalHours.ToString('F1')) hours" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Green
exit 0
