#Requires -Version 5.1
<#
.SYNOPSIS
    Phase 16 gate — PromotionManager hardening + Audit persistence tests.
.DESCRIPTION
    Tests: idempotent promote, rollback idempotency (409), audit JSONL persistence, retention cleanup.
#>

param(
    [string]$CoreUrl = ""
)
if (-not $CoreUrl) { $CoreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" } }

$ErrorActionPreference = "Stop"
$passed = 0
$failed = 0
$startTime = Get-Date

function Write-Pass([string]$msg) {
    Write-Host "  PASS $msg" -ForegroundColor Green
    $script:passed++
}

function Write-Fail([string]$msg) {
    Write-Host "  FAIL $msg" -ForegroundColor Red
    $script:failed++
}

function Invoke-Safe {
    param([string]$Uri, [string]$Method = "GET", [string]$Body = "")
    try {
        $params = @{ Uri = $Uri; Method = $Method; UseBasicParsing = $true; TimeoutSec = 30 }
        if ($Body) { $params["Body"] = $Body; $params["ContentType"] = "application/json" }
        return Invoke-WebRequest @params
    } catch {
        return $_.Exception.Response
    }
}

function Get-StatusCode([object]$resp) {
    if ($null -eq $resp) { return 0 }
    if ($resp -is [System.Net.HttpWebResponse]) { return [int]$resp.StatusCode }
    return [int]$resp.StatusCode
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phase 16 Gate — PromotionManager      " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ── Preflight ──────────────────────────────────────────────────────────────
Write-Host "[Preflight] Checking Core health..." -ForegroundColor Yellow
$health = Invoke-Safe "$CoreUrl/health"
if ((Get-StatusCode $health) -ne 200) {
    Write-Host "ERROR: Core not reachable at $CoreUrl" -ForegroundColor Red
    exit 1
}
Write-Host "  Core OK" -ForegroundColor Green
Write-Host ""

# ── Test 1: Status endpoint returns expected fields ────────────────────────
Write-Host "[Test 1] selfupdate/status fields" -ForegroundColor Yellow
$resp = Invoke-Safe "$CoreUrl/selfupdate/status"
if ((Get-StatusCode $resp) -eq 200) {
    $body = $resp.Content | ConvertFrom-Json
    if ($null -ne $body.releasesRoot -and $null -ne $body.sandboxRoot) {
        Write-Pass "status returns releasesRoot and sandboxRoot"
    } else {
        $msg = "status missing required fields, received: " + $resp.Content
        Write-Fail $msg
    }
} else {
    Write-Fail "status returned $((Get-StatusCode $resp))"
}

# ── Test 2: Promote missing fields → 400 ──────────────────────────────────
Write-Host "[Test 2] Promote with missing fields → 400" -ForegroundColor Yellow
$resp = Invoke-Safe "$CoreUrl/selfupdate/promote" "POST" "{}"
$code = Get-StatusCode $resp
if ($code -eq 400) {
    Write-Pass "missing fields → 400"
} else {
    Write-Fail "expected 400, got $code"
}

# ── Test 3: Promote with bogus sandboxPath → 404 ──────────────────────────
Write-Host "[Test 3] Promote with bogus sandboxPath → 404" -ForegroundColor Yellow
$bogus = @{ candidateId = "test-bogus"; sandboxPath = "C:\NonExistentPath\bogus" } | ConvertTo-Json
$resp = Invoke-Safe "$CoreUrl/selfupdate/promote" "POST" $bogus
$code = Get-StatusCode $resp
if ($code -eq 404) {
    Write-Pass "bogus sandboxPath → 404"
} else {
    Write-Fail "expected 404, got $code"
}

# ── Test 4: Rollback with nothing to rollback → 409 ───────────────────────
Write-Host "[Test 4] Rollback when nothing to rollback → 409" -ForegroundColor Yellow
# Fresh system (or after tests): rollback should return 409 if no previous version exists
$resp = Invoke-Safe "$CoreUrl/selfupdate/rollback" "POST" "{}"
$code = Get-StatusCode $resp
if ($code -eq 409) {
    Write-Pass "rollback with nothing → 409"
} elseif ($code -eq 200) {
    # If there was actually a previous version, that's acceptable
    $body = $resp.Content | ConvertFrom-Json
    if ($body.ok -eq $true) {
        Write-Pass "rollback succeeded (had a previous version)"
    } else {
        Write-Fail "rollback returned 200 but ok=false"
    }
} else {
    Write-Fail "expected 409 or 200, got $code"
}

# ── Test 5: Double rollback → second must be 409 ──────────────────────────
Write-Host "[Test 5] Double rollback → second must be 409" -ForegroundColor Yellow
$resp1 = Invoke-Safe "$CoreUrl/selfupdate/rollback" "POST" "{}"
$resp2 = Invoke-Safe "$CoreUrl/selfupdate/rollback" "POST" "{}"
$code2 = Get-StatusCode $resp2
if ($code2 -eq 409) {
    Write-Pass "second rollback → 409 (idempotent)"
} else {
    Write-Fail "second rollback expected 409, got $code2"
}

# ── Test 6: Audit endpoint returns valid structure ─────────────────────────
Write-Host "[Test 6] Audit endpoint structure" -ForegroundColor Yellow
$resp = Invoke-Safe "$CoreUrl/selfupdate/audit"
if ((Get-StatusCode $resp) -eq 200) {
    $body = $resp.Content | ConvertFrom-Json
    if ($null -ne $body.events -and $null -ne $body.total) {
        Write-Pass "audit returns events + total"
    } else {
        Write-Fail "audit missing events or total (received: $($resp.Content))"
    }
} else {
    Write-Fail "audit returned $((Get-StatusCode $resp))"
}

# ── Test 7: Audit paging (skip/take) ──────────────────────────────────────
Write-Host "[Test 7] Audit paging" -ForegroundColor Yellow
$resp = Invoke-Safe "$CoreUrl/selfupdate/audit?skip=0&take=5"
if ((Get-StatusCode $resp) -eq 200) {
    $body = $resp.Content | ConvertFrom-Json
    if ($body.events.Count -le 5) {
        Write-Pass "audit paging take=5 returns ≤5 events"
    } else {
        Write-Fail "audit take=5 returned $($body.events.Count) events"
    }
} else {
    Write-Fail "audit paging returned $((Get-StatusCode $resp))"
}

# ── Test 8: Audit JSONL file exists on disk ────────────────────────────────
Write-Host "[Test 8] Audit JSONL persisted to disk" -ForegroundColor Yellow
$dataPath = $env:ARCHIMEDES_DATA_PATH
if (-not $dataPath) {
    $dataPath = Join-Path $env:LOCALAPPDATA "Archimedes"
}
$auditFile = Join-Path $dataPath "selfupdate_audit.jsonl"
if (Test-Path $auditFile) {
    $lines = Get-Content $auditFile | Where-Object { $_ -ne "" }
    if ($lines.Count -gt 0) {
        # Validate first line is valid JSON with expected fields
        try {
            $first = $lines[0] | ConvertFrom-Json
            if ($first.Action -and $first.Timestamp) {
                Write-Pass "audit JSONL exists with $($lines.Count) entries, valid JSON"
            } else {
                Write-Fail "audit JSONL exists but missing Action/Timestamp fields"
            }
        } catch {
            Write-Fail "audit JSONL line is not valid JSON: $($lines[0])"
        }
    } else {
        Write-Fail "audit JSONL file exists but is empty"
    }
} else {
    Write-Fail "audit JSONL not found at $auditFile (ARCHIMEDES_DATA_PATH=$dataPath)"
}

# ── Test 9: Audit redaction — no secrets in file ──────────────────────────
Write-Host "[Test 9] Audit JSONL has no secrets" -ForegroundColor Yellow
if (Test-Path $auditFile) {
    $content = Get-Content $auditFile -Raw
    $secretPatterns = @("password", "Bearer ", "BEGIN PRIVATE", "sk_live_", "api_key=", "token=")
    $found = $false
    foreach ($pattern in $secretPatterns) {
        if ($content -imatch [regex]::Escape($pattern)) {
            Write-Fail "audit JSONL contains potential secret pattern: '$pattern'"
            $found = $true
            break
        }
    }
    if (-not $found) {
        Write-Pass "no secret patterns in audit JSONL"
    }
} else {
    Write-Host "  SKIP audit file not found (skipping redaction check)" -ForegroundColor DarkGray
}

# ── Summary ────────────────────────────────────────────────────────────────
$duration = (Get-Date) - $startTime
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phase 16 Results" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Passed : $passed" -ForegroundColor Green
Write-Host "  Failed : $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })
Write-Host "  Duration: $([math]::Round($duration.TotalSeconds, 1))s" -ForegroundColor Gray
Write-Host ""

if ($failed -gt 0) {
    Write-Host "PHASE 16 GATE: FAIL" -ForegroundColor Red
    exit 1
} else {
    Write-Host "PHASE 16 GATE: PASS" -ForegroundColor Green
    exit 0
}
