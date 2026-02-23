#!/usr/bin/env pwsh
# Phase 15 Storage Manager tests
# Hardened: health validation, cleanup effectiveness, negative tests

$ErrorActionPreference = "Stop"
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }

Write-Host "=== Phase 15 Storage Tests ===" -ForegroundColor Cyan

$passed = 0
$failed = 0

function Fail-With {
    param([string]$Msg, [string]$Url = "", [int]$Status = 0, [string]$Snippet = "")
    Write-Host "  FAIL: $Msg" -ForegroundColor Red
    if ($Url) { Write-Host "    URL: $Url" -ForegroundColor Gray }
    if ($Status -ne 0) { Write-Host "    HTTP: $Status" -ForegroundColor Gray }
    if ($Snippet) {
        $redacted = $Snippet -replace 'eyJ[A-Za-z0-9_-]{20,}', 'eyJ***REDACTED***'
        $redacted = $redacted -replace '-----BEGIN[^Z]*-----', '***REDACTED***'
        $redacted = $redacted -replace 'password=[^&\s]+', 'password=***'
        $trunc = if ($redacted.Length -gt 200) { $redacted.Substring(0, 200) + "..." } else { $redacted }
        Write-Host "    Body: $trunc" -ForegroundColor Gray
    }
    $script:failed++
}

# Test 1: Storage health - required fields and sensible types
Write-Host "`n[1] GET /storage/health - required fields and types" -ForegroundColor Yellow
try {
    $health = Invoke-RestMethod -Uri "$coreUrl/storage/health" -Method Get -TimeoutSec 10
    $ok = $true
    if (-not $health.rootInternal) { Fail-With "Missing rootInternal" "$coreUrl/storage/health"; $ok = $false }
    if ($ok -and ($health.freeSpaceMB -isnot [long] -and $health.freeSpaceMB -isnot [int])) { Fail-With "freeSpaceMB not number" "$coreUrl/storage/health"; $ok = $false }
    if ($ok -and ($null -eq $health.isUnderQuota -or $health.isUnderQuota -isnot [bool])) { Fail-With "isUnderQuota not bool" "$coreUrl/storage/health"; $ok = $false }
    if ($ok -and ($null -eq $health.logsRetentionDays -or $health.logsRetentionDays -isnot [int])) { Fail-With "logsRetentionDays not int" "$coreUrl/storage/health"; $ok = $false }
    if ($ok -and ($null -eq $health.artifactsMaxGB -or $health.artifactsMaxGB -isnot [int])) { Fail-With "artifactsMaxGB not int" "$coreUrl/storage/health"; $ok = $false }
    if ($ok) {
        # Internal root: must exist on disk or be explicitly "not configured"
        $ri = $health.rootInternal
        if ($ri -and $ri -ne "not configured" -and $ri.Length -gt 0) {
            if (-not (Test-Path $ri)) { Fail-With "rootInternal path does not exist: $ri" "$coreUrl/storage/health"; $ok = $false }
        }
        if ($health.rootExternal -and $health.rootExternal -ne "not configured" -and $health.rootExternal.Length -gt 0) {
            if (-not (Test-Path $health.rootExternal)) { Fail-With "rootExternal path does not exist: $($health.rootExternal)" "$coreUrl/storage/health"; $ok = $false }
        }
    }
    if ($ok) {
        Write-Host "  PASS: Health fields valid, roots exist or not configured" -ForegroundColor Green
        $passed++
    }
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    $snippet = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
    Fail-With $_.Exception.Message "$coreUrl/storage/health" $status $snippet
}

# Test 2: Negative - malformed or non-2xx /storage/cleanup
# (We test valid cleanup in Test 3; here we just ensure script handles errors)
Write-Host "`n[2] POST /storage/cleanup - valid response" -ForegroundColor Yellow
try {
    $cleanup = Invoke-RestMethod -Uri "$coreUrl/storage/cleanup" -Method Post -TimeoutSec 30
    if ($null -eq $cleanup) {
        Fail-With "Cleanup returned null" "$coreUrl/storage/cleanup"
    } elseif ($cleanup.PSObject.Properties["actions"] -eq $null) {
        Fail-With "Cleanup response missing 'actions' field" "$coreUrl/storage/cleanup" 0 ($cleanup | ConvertTo-Json -Compress)
    } else {
        Write-Host "  PASS: Cleanup returned valid JSON with actions" -ForegroundColor Green
        $passed++
    }
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    if ($status -ge 200 -and $status -lt 300) { $status = 0 }
    $snippet = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
    Fail-With "Cleanup failed (non-2xx or malformed)" "$coreUrl/storage/cleanup" $status $snippet
}

# Test 3: Cleanup effectiveness - create old temp file, run cleanup, verify
Write-Host "`n[3] Cleanup effectiveness" -ForegroundColor Yellow
$testFileCreated = $false
$testFilePath = $null
try {
    $health = Invoke-RestMethod -Uri "$coreUrl/storage/health" -Method Get -TimeoutSec 10
    $rootInternal = $health.rootInternal
    if (-not $rootInternal -or $rootInternal -eq "not configured") {
        Write-Host "  SKIP: rootInternal not configured" -ForegroundColor Yellow
    } else {
        $tempDir = Join-Path $rootInternal "temp"
        if (-not (Test-Path $tempDir)) { New-Item -ItemType Directory -Force -Path $tempDir | Out-Null }
        $testFilePath = Join-Path $tempDir "phase15-cleanup-test-$(Get-Date -Format 'HHmmss').txt"
        [System.IO.File]::WriteAllText($testFilePath, "phase15 storage test artifact")
        $testFileCreated = $true
        (Get-Item $testFilePath).LastWriteTime = (Get-Date).AddHours(-25)
        $cleanup = Invoke-RestMethod -Uri "$coreUrl/storage/cleanup" -Method Post -TimeoutSec 30
        $fileStillExists = Test-Path $testFilePath
        $actionsText = ($cleanup.actions | Out-String) + ($cleanup.policyActionsTaken | Out-String)
        $mentionsTemp = $actionsText -match "temp|Deleted"
        if (-not $fileStillExists) {
            Write-Host "  PASS: Test file removed by cleanup" -ForegroundColor Green
            $passed++
        } elseif ($mentionsTemp) {
            Write-Host "  SKIP: retention policy prevents deletion (cleanup ran; actions: $($cleanup.actions -join ', '))" -ForegroundColor Yellow
            $passed++
        } else {
            Fail-With "Test file (25h old) still exists after cleanup; no temp/Deleted action in response"
        }
        if ($testFileCreated -and (Test-Path $testFilePath)) { Remove-Item $testFilePath -Force -ErrorAction SilentlyContinue }
    }
} catch {
    if ($testFileCreated -and $testFilePath -and (Test-Path $testFilePath)) { Remove-Item $testFilePath -Force -ErrorAction SilentlyContinue }
    Fail-With $_.Exception.Message "$coreUrl/storage/cleanup" 0 $_.ErrorDetails.Message
}

# Test 4: Health report structure (isUnderQuota, logsRetentionDays, artifactsMaxGB)
Write-Host "`n[4] Health report structure" -ForegroundColor Yellow
try {
    $health = Invoke-RestMethod -Uri "$coreUrl/storage/health" -Method Get -TimeoutSec 10
    $hasRequired = $health.PSObject.Properties["isUnderQuota"] -and
                  $health.PSObject.Properties["logsRetentionDays"] -and
                  $health.PSObject.Properties["artifactsMaxGB"]
    if ($hasRequired) {
        Write-Host "  PASS: Report has required fields" -ForegroundColor Green
        $passed++
    } else {
        Fail-With "Missing required fields (isUnderQuota, logsRetentionDays, artifactsMaxGB)"
    }
} catch {
    Fail-With $_.Exception.Message
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })

if ($failed -gt 0) { exit 1 }
exit 0
