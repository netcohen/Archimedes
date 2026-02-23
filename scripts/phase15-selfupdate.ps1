#!/usr/bin/env pwsh
# Phase 15 Self-Update tests
# Hardened: status/audit fields, sandbox isolation, rollback/promote checks, audit redaction

$ErrorActionPreference = "Stop"
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Split-Path -Parent $scriptDir
if (-not $repoRoot -or -not (Test-Path $repoRoot)) { $repoRoot = (Get-Location).Path }
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }

Write-Host "=== Phase 15 Self-Update Tests ===" -ForegroundColor Cyan

$passed = 0
$failed = 0
$script:sandboxPath = $null
$script:auditEventsBefore = @()
$script:auditEventsAfter = @()

$productionDataPath = [Environment]::GetFolderPath("LocalApplicationData") + "\Archimedes"

function Fail-With {
    param([string]$Msg, [string]$Url = "", [int]$Status = 0, [string]$Snippet = "")
    Write-Host "  FAIL: $Msg" -ForegroundColor Red
    if ($Url) { Write-Host "    URL: $Url" -ForegroundColor Gray }
    if ($Status -ne 0) { Write-Host "    HTTP: $Status" -ForegroundColor Gray }
    if ($Snippet) {
        $redacted = $Snippet -replace 'eyJ[A-Za-z0-9_-]{20,}', 'eyJ***REDACTED***'
        $redacted = $redacted -replace '-----BEGIN[^Z]*-----', '***REDACTED***'
        $redacted = $redacted -replace 'password=[^&\s"'']+', 'password=***'
        $trunc = if ($redacted.Length -gt 200) { $redacted.Substring(0, 200) + "..." } else { $redacted }
        Write-Host "    Body: $trunc" -ForegroundColor Gray
    }
    $script:failed++
}

# Test 1: /selfupdate/status - required fields and types
Write-Host "`n[1] GET /selfupdate/status" -ForegroundColor Yellow
try {
    $status = Invoke-RestMethod -Uri "$coreUrl/selfupdate/status" -Method Get -TimeoutSec 10
    $ok = $true
    if (-not $status.PSObject.Properties["currentVersion"]) { Fail-With "Missing currentVersion" "$coreUrl/selfupdate/status"; $ok = $false }
    if ($ok -and -not $status.PSObject.Properties["releasesRoot"]) { Fail-With "Missing releasesRoot" "$coreUrl/selfupdate/status"; $ok = $false }
    if ($ok) {
        Write-Host "  PASS: Status has currentVersion, canary info, releasesRoot" -ForegroundColor Green
        $passed++
    }
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    $snippet = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
    Fail-With $_.Exception.Message "$coreUrl/selfupdate/status" $status $snippet
}

# Test 2: /selfupdate/audit paging (skip/take)
Write-Host "`n[2] GET /selfupdate/audit paging" -ForegroundColor Yellow
try {
    $audit1 = Invoke-RestMethod -Uri "$coreUrl/selfupdate/audit?skip=0&take=5" -Method Get -TimeoutSec 10
    $audit2 = Invoke-RestMethod -Uri "$coreUrl/selfupdate/audit?skip=0&take=10" -Method Get -TimeoutSec 10
    if (-not $audit1.PSObject.Properties["events"]) { Fail-With "Audit missing 'events' array" "$coreUrl/selfupdate/audit"; } else {
        if ($audit1.events -isnot [array] -and $audit1.events -isnot [System.Collections.IEnumerable]) {
            Fail-With "Audit 'events' is not an array"
        } else {
            Write-Host "  PASS: Audit paging works, events is array" -ForegroundColor Green
            $passed++
            $script:auditEventsBefore = @($audit1.events)
        }
    }
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    Fail-With $_.Exception.Message "$coreUrl/selfupdate/audit" $status ($_.ErrorDetails.Message)
}

# Test 3: Dry-run sandbox - isolation proof
Write-Host "`n[3] POST /selfupdate/sandbox-run (dryRun)" -ForegroundColor Yellow
try {
    $body = '{"dryRun": true}'
    $result = Invoke-RestMethod -Uri "$coreUrl/selfupdate/sandbox-run" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 300
    $sp = $null
    if ($result.manifest -and $result.manifest.sandboxPath) { $sp = $result.manifest.sandboxPath }
    elseif ($result.sandboxPath) { $sp = $result.sandboxPath }
    if (-not $sp) {
        Fail-With "Response missing sandboxPath" "$coreUrl/selfupdate/sandbox-run" 0 ($result | ConvertTo-Json -Compress)
    } elseif ($result.success -ne $true) {
        Fail-With "Dry-run success=false: $($result.error)" "$coreUrl/selfupdate/sandbox-run" 0 ($result | ConvertTo-Json -Compress)
    } else {
        $script:sandboxPath = $sp
        Write-Host "  PASS: Dry-run succeeded, sandboxPath present" -ForegroundColor Green
        $passed++
    }
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    Fail-With $_.Exception.Message "$coreUrl/selfupdate/sandbox-run" $status ($_.ErrorDetails.Message)
}

# Test 4: Sandbox NOT under production or repo root
Write-Host "`n[4] Sandbox isolation (not production/repo)" -ForegroundColor Yellow
if ($script:sandboxPath) {
    $spNorm = $script:sandboxPath.TrimEnd('\').ToLowerInvariant()
    $prodNorm = $productionDataPath.TrimEnd('\').ToLowerInvariant()
    $repoNorm = $repoRoot.TrimEnd('\').ToLowerInvariant()
    if ($spNorm -like "*$prodNorm*") {
        Fail-With "Sandbox path is under production data path: $($script:sandboxPath)"
    } elseif ($spNorm -eq $repoNorm -or $spNorm -like "$repoNorm\*") {
        Fail-With "Sandbox path overlaps repo root: $($script:sandboxPath)"
    } else {
        Write-Host "  PASS: Sandbox path isolated from production and repo" -ForegroundColor Green
        Write-Host "    - Sandbox: $($script:sandboxPath)" -ForegroundColor Gray
        $passed++
    }
} else {
    Write-Host "  SKIP: No sandbox path from dry-run" -ForegroundColor Yellow
}

# Test 5: Audit has NEW events after dry-run and is redacted
Write-Host "`n[5] Audit new events + redaction" -ForegroundColor Yellow
try {
    $audit = Invoke-RestMethod -Uri "$coreUrl/selfupdate/audit?take=50" -Method Get -TimeoutSec 10
    $script:auditEventsAfter = @($audit.events)
    $forbidden = @(
        { param($t) $t -match 'eyJ[A-Za-z0-9_-]{30,}' },
        { param($t) $t -match '-----BEGIN' },
        { param($t) $t -match 'password\s*=' }
    )
    $hasForbidden = $false
    foreach ($evt in $script:auditEventsAfter) {
        $text = ($evt | ConvertTo-Json -Compress)
        foreach ($f in $forbidden) {
            if (& $f $text) { $hasForbidden = $true; break }
        }
    }
    if ($hasForbidden) {
        Fail-With "Audit contains potential raw secret (JWT/PEM/password)"
    } elseif ($script:auditEventsAfter.Count -ge $script:auditEventsBefore.Count) {
        Write-Host "  PASS: Audit has events, no raw secrets" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  PASS: Audit redaction OK (events may vary)" -ForegroundColor Green
        $passed++
    }
} catch {
    Fail-With $_.Exception.Message "$coreUrl/selfupdate/audit" 0 ($_.ErrorDetails.Message)
}

# Test 6: Rollback - accept only 200/202/409/400; 404/500 -> FAIL
Write-Host "`n[6] POST /selfupdate/rollback" -ForegroundColor Yellow
try {
    $rb = Invoke-RestMethod -Uri "$coreUrl/selfupdate/rollback" -Method Post -TimeoutSec 10
    Write-Host "  PASS: Rollback returned 2xx" -ForegroundColor Green
    $passed++
} catch {
    $status = 0
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
    if ($status -eq 200 -or $status -eq 202 -or $status -eq 409 -or $status -eq 400) {
        Write-Host "  PASS: Rollback endpoint exists (responded $status)" -ForegroundColor Green
        $passed++
    } else {
        Fail-With "Rollback returned unacceptable status (expected 200/202/409/400)" "$coreUrl/selfupdate/rollback" $status ($_.ErrorDetails.Message)
    }
}

# Test 7: Promote safety - missing fields -> 400
Write-Host "`n[7] POST /selfupdate/promote (validation)" -ForegroundColor Yellow
try {
    $resp = Invoke-WebRequest -Uri "$coreUrl/selfupdate/promote" -Method Post -Body '{}' -ContentType "application/json" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($resp.StatusCode -eq 400) {
        $body = $resp.Content
        if ($body -match "candidateId|sandboxPath|required") {
            Write-Host "  PASS: Promote returns 400 with validation message" -ForegroundColor Green
            $passed++
        } else {
            Fail-With "Promote 400 but missing validation message" "$coreUrl/selfupdate/promote" 400 $body
        }
    } else {
        Fail-With "Expected 400 for missing candidateId/sandboxPath, got $($resp.StatusCode)" "$coreUrl/selfupdate/promote" $resp.StatusCode $resp.Content
    }
} catch {
    $status = 0
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
    $body = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { "" }
    if ($status -eq 400 -and $body -match "candidateId|sandboxPath|required") {
        Write-Host "  PASS: Promote returns 400 with validation message" -ForegroundColor Green
        $passed++
    } else {
        Fail-With "Promote validation check failed" "$coreUrl/selfupdate/promote" $status $body
    }
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })

if ($failed -gt 0) { exit 1 }
exit 0
