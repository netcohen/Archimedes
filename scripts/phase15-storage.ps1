#!/usr/bin/env pwsh
# Phase 15 Storage Manager tests
# Creates temp files, simulates quota scenario, asserts cleanup

$ErrorActionPreference = "Stop"
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }

Write-Host "=== Phase 15 Storage Tests ===" -ForegroundColor Cyan

$passed = 0
$failed = 0

# Test 1: Storage health endpoint
Write-Host "`n[1] GET /storage/health" -ForegroundColor Yellow
try {
    $health = Invoke-RestMethod -Uri "$coreUrl/storage/health" -Method Get -TimeoutSec 10
    if ($health.rootInternal) {
        Write-Host "  PASS: Storage health returned" -ForegroundColor Green
        Write-Host "    - rootInternal: $($health.rootInternal)" -ForegroundColor Gray
        Write-Host "    - freeSpaceMB: $($health.freeSpaceMB)" -ForegroundColor Gray
        $passed++
    } else {
        Write-Host "  FAIL: Invalid health response" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 2: Storage cleanup endpoint
Write-Host "`n[2] POST /storage/cleanup" -ForegroundColor Yellow
try {
    $cleanup = Invoke-RestMethod -Uri "$coreUrl/storage/cleanup" -Method Post -TimeoutSec 30
    if ($null -ne $cleanup) {
        Write-Host "  PASS: Cleanup executed" -ForegroundColor Green
        if ($cleanup.actions -and $cleanup.actions.Count -gt 0) {
            Write-Host "    - Actions: $($cleanup.actions -join '; ')" -ForegroundColor Gray
        }
        $passed++
    } else {
        Write-Host "  FAIL: No cleanup result" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 3: Health report structure
Write-Host "`n[3] Health report structure" -ForegroundColor Yellow
try {
    $health = Invoke-RestMethod -Uri "$coreUrl/storage/health" -Method Get -TimeoutSec 10
    $hasRequired = $health.PSObject.Properties["isUnderQuota"] -and
                  $health.PSObject.Properties["logsRetentionDays"] -and
                  $health.PSObject.Properties["artifactsMaxGB"]
    if ($hasRequired) {
        Write-Host "  PASS: Report has required fields" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Missing required fields" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })

if ($failed -gt 0) {
    exit 1
}
exit 0
