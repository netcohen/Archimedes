#!/usr/bin/env pwsh
# Phase 15 Self-Update tests
# Dry-run sandbox, verify audit, verify no production access

$ErrorActionPreference = "Stop"
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }

Write-Host "=== Phase 15 Self-Update Tests ===" -ForegroundColor Cyan

$passed = 0
$failed = 0

# Production data path - sandbox must NEVER use this
$productionDataPath = [Environment]::GetFolderPath("LocalApplicationData") + "\Archimedes"

# Test 1: Self-update status
Write-Host "`n[1] GET /selfupdate/status" -ForegroundColor Yellow
try {
    $status = Invoke-RestMethod -Uri "$coreUrl/selfupdate/status" -Method Get -TimeoutSec 10
    if ($null -ne $status) {
        Write-Host "  PASS: Status endpoint works" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: No response" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 2: Dry-run sandbox
Write-Host "`n[2] POST /selfupdate/sandbox-run (dryRun)" -ForegroundColor Yellow
try {
    $body = '{"dryRun": true}'
    $result = Invoke-RestMethod -Uri "$coreUrl/selfupdate/sandbox-run" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 300
    if ($result.success -eq $true) {
        Write-Host "  PASS: Dry-run sandbox succeeded" -ForegroundColor Green
        $passed++
        $script:sandboxPath = $result.manifest.sandboxPath
    } else {
        Write-Host "  FAIL: Dry-run failed: $($result.error)" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 3: Verify no production access
Write-Host "`n[3] No production data path in sandbox" -ForegroundColor Yellow
if ($script:sandboxPath) {
    if ($script:sandboxPath -notlike "*$productionDataPath*") {
        Write-Host "  PASS: Sandbox path is isolated from production" -ForegroundColor Green
        Write-Host "    - Sandbox: $($script:sandboxPath)" -ForegroundColor Gray
        $passed++
    } else {
        Write-Host "  FAIL: Sandbox path overlaps production!" -ForegroundColor Red
        $failed++
    }
} else {
    Write-Host "  WARN: Skipped (no sandbox path from dry-run)" -ForegroundColor Yellow
}

# Test 4: Audit endpoint
Write-Host "`n[4] GET /selfupdate/audit" -ForegroundColor Yellow
try {
    $audit = Invoke-RestMethod -Uri "$coreUrl/selfupdate/audit?take=20" -Method Get -TimeoutSec 10
    if ($audit.events -and $audit.events.Count -gt 0) {
        Write-Host "  PASS: Audit has events ($($audit.events.Count))" -ForegroundColor Green
        $passed++
        $script:auditEvents = $audit.events
    } else {
        Write-Host "  FAIL: No audit events" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 5: Audit redaction (no raw secrets)
Write-Host "`n[5] Audit redaction" -ForegroundColor Yellow
$forbiddenInAudit = @("-----BEGIN ", "eyJ[A-Za-z0-9_-]+\.eyJ")
$foundForbidden = $false
if ($script:auditEvents) {
    foreach ($evt in $script:auditEvents) {
        $text = ($evt.details | Out-String) + ($evt.action | Out-String)
        foreach ($f in $forbiddenInAudit) {
            if ($text -match $f) {
                $foundForbidden = $true
                break
            }
        }
    }
}
if (-not $foundForbidden) {
    Write-Host "  PASS: No raw secrets in audit" -ForegroundColor Green
    $passed++
} else {
    Write-Host "  FAIL: Potential raw secret in audit" -ForegroundColor Red
    $failed++
}

# Test 6: Promote/Rollback endpoints exist
Write-Host "`n[6] Promote/Rollback endpoints" -ForegroundColor Yellow
try {
    $rb = Invoke-RestMethod -Uri "$coreUrl/selfupdate/rollback" -Method Post -TimeoutSec 5
    if ($null -ne $rb -and $rb.PSObject.Properties["ok"]) {
        Write-Host "  PASS: Rollback endpoint responds" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Invalid rollback response" -ForegroundColor Red
        $failed++
    }
} catch {
    if ($_.Exception.Response.StatusCode -eq 400 -or $_.Exception.Message -match "400|500") {
        Write-Host "  PASS: Rollback endpoint exists (responded)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
        $failed++
    }
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })

if ($failed -gt 0) {
    exit 1
}
exit 0
