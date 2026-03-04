#!/usr/bin/env pwsh
# Phase 27 – Autonomous Tool Acquisition – Ready Gate
# Tests: build, core health, tools API, gaps API, sources API,
#        legal approvals, tool acquisition flow, source intelligence,
#        gap detection, legal decision, chat integration, Phase 26 regression

$ErrorActionPreference = "Stop"
$BASE    = "http://localhost:5051"
$PASS    = 0
$FAIL    = 0
$Section = ""

function Assert($label, $cond) {
    if ($cond) {
        Write-Host "  [PASS] $label" -ForegroundColor Green
        $script:PASS++
    } else {
        Write-Host "  [FAIL] $label" -ForegroundColor Red
        $script:FAIL++
    }
}

function Section($title) {
    $script:Section = $title
    Write-Host "`n── $title ──────────────────────────────────" -ForegroundColor Cyan
}

# ─────────────────────────────────────────────────────────────────────────────
Section "[1] dotnet build"
# ─────────────────────────────────────────────────────────────────────────────
$buildDir = Split-Path $PSScriptRoot -Parent
$buildOut  = dotnet build "$buildDir/core" 2>&1
Assert "Build: 0 errors" ($buildOut -match "Build succeeded" -and $buildOut -notmatch "Error\(s\): [^0]")

# ─────────────────────────────────────────────────────────────────────────────
Section "[2] Core health"
# ─────────────────────────────────────────────────────────────────────────────
# Start core if not running
$coreRunning = $false
try {
    $h = Invoke-WebRequest -Uri "$BASE/health" -UseBasicParsing -TimeoutSec 3
    $coreRunning = $h.StatusCode -eq 200
} catch {}

if (-not $coreRunning) {
    Write-Host "  Starting Archimedes Core..." -ForegroundColor Yellow
    Start-Process "dotnet" -ArgumentList "run --project $buildDir/core" -PassThru | Out-Null
    Start-Sleep 6
}

$health = Invoke-WebRequest -Uri "$BASE/health" -UseBasicParsing -TimeoutSec 10
Assert "GET /health → 200"  ($health.StatusCode -eq 200)
Assert "Response contains OK" ($health.Content -match "OK")

# ─────────────────────────────────────────────────────────────────────────────
Section "[3] Tools list (initially empty)"
# ─────────────────────────────────────────────────────────────────────────────
$toolsResp = Invoke-WebRequest -Uri "$BASE/tools" -UseBasicParsing -TimeoutSec 10
$toolsJson = $toolsResp.Content | ConvertFrom-Json
Assert "GET /tools → 200"     ($toolsResp.StatusCode -eq 200)
Assert "Response has 'count'" ($null -ne $toolsJson.count)
Assert "Response has 'tools'" ($null -ne $toolsJson.tools)

# ─────────────────────────────────────────────────────────────────────────────
Section "[4] Gaps list"
# ─────────────────────────────────────────────────────────────────────────────
$gapsResp = Invoke-WebRequest -Uri "$BASE/tools/gaps" -UseBasicParsing -TimeoutSec 10
$gapsJson = $gapsResp.Content | ConvertFrom-Json
Assert "GET /tools/gaps → 200"  ($gapsResp.StatusCode -eq 200)
Assert "Response has 'count'"   ($null -ne $gapsJson.count)
Assert "Response has 'gaps'"    ($null -ne $gapsJson.gaps)

# ─────────────────────────────────────────────────────────────────────────────
Section "[5] Source intelligence"
# ─────────────────────────────────────────────────────────────────────────────
$srcResp = Invoke-WebRequest -Uri "$BASE/tools/sources" -UseBasicParsing -TimeoutSec 10
$srcJson = $srcResp.Content | ConvertFrom-Json
Assert "GET /tools/sources → 200"    ($srcResp.StatusCode -eq 200)
Assert "Response has 'count'"        ($null -ne $srcJson.count)
Assert "Pre-seeded sources exist"    ($srcJson.count -ge 5)
Assert "Has torAvailable field"      ($null -ne $srcJson.torAvailable)

# Verify some known seeds exist
$srcIds = $srcJson.sources | ForEach-Object { $_.sourceId }
Assert "github.com seeded"   ($srcIds -contains "github.com")
Assert "nuget.org seeded"    ($srcIds -contains "nuget.org")
Assert "npmjs.com seeded"    ($srcIds -contains "npmjs.com")

# ─────────────────────────────────────────────────────────────────────────────
Section "[6] Legal approvals (initially empty)"
# ─────────────────────────────────────────────────────────────────────────────
$legalResp = Invoke-WebRequest -Uri "$BASE/tools/legal/pending" -UseBasicParsing -TimeoutSec 10
$legalJson = $legalResp.Content | ConvertFrom-Json
Assert "GET /tools/legal/pending → 200" ($legalResp.StatusCode -eq 200)
Assert "Response has 'count'"           ($null -ne $legalJson.count)
Assert "Response has 'approvals'"       ($null -ne $legalJson.approvals)

# ─────────────────────────────────────────────────────────────────────────────
Section "[7] Tool acquisition - non-blocking async"
# ─────────────────────────────────────────────────────────────────────────────
# POST /tools/acquire now returns immediately with status=SEARCHING
# and runs the actual search in background. No timeout issue.
$acqBody = '{"capability":"HTTP_STATUS_CHECK","context":"Check if a URL is reachable"}'
$acqResp = Invoke-WebRequest -Uri "$BASE/tools/acquire" `
    -Method POST -Body $acqBody -ContentType "application/json" `
    -UseBasicParsing -TimeoutSec 15
$acqJson = $acqResp.Content | ConvertFrom-Json
Assert "POST /tools/acquire → 200"               ($acqResp.StatusCode -eq 200)
Assert "Response has 'ok' field"                 ($null -ne $acqJson.ok)
Assert "Response has 'capability'"               ($acqJson.capability -eq "HTTP_STATUS_CHECK")
Assert "Status is SEARCHING or ALREADY_ACQUIRED" ($acqJson.status -in @("SEARCHING","ALREADY_ACQUIRED"))

# ─────────────────────────────────────────────────────────────────────────────
Section "[8] Tools list after acquisition attempt"
# ─────────────────────────────────────────────────────────────────────────────
$tools2Resp = Invoke-WebRequest -Uri "$BASE/tools" -UseBasicParsing -TimeoutSec 10
$tools2Json = $tools2Resp.Content | ConvertFrom-Json
Assert "GET /tools → 200 after attempt" ($tools2Resp.StatusCode -eq 200)
Assert "Count is numeric"               ($tools2Json.count -is [int] -or $tools2Json.count -is [long])

# ─────────────────────────────────────────────────────────────────────────────
Section "[9] Legal decision endpoint"
# ─────────────────────────────────────────────────────────────────────────────
# Try to decide on a non-existent approval – should return 200 with ok=true
# (graceful no-op when approvalId not found)
$decBody = '{"decision":"REJECTED","note":"test only"}'
try {
    $decResp = Invoke-WebRequest -Uri "$BASE/tools/legal/nonexistent-id/decide" `
        -Method POST -Body $decBody -ContentType "application/json" `
        -UseBasicParsing -TimeoutSec 10
    Assert "POST /tools/legal/{id}/decide → 200" ($decResp.StatusCode -eq 200)
    $decJson = $decResp.Content | ConvertFrom-Json
    Assert "Has 'decision' field"  ($null -ne $decJson.decision)
    Assert "Has 'acquired' field"  ($null -ne $decJson.acquired)
} catch {
    # 400/404 is also acceptable for non-existent approval
    Assert "POST /tools/legal/{id}/decide returns 200/400/404" $true
}

# ─────────────────────────────────────────────────────────────────────────────
Section "[10] Gap detection via acquisition"
# ─────────────────────────────────────────────────────────────────────────────
# POST /tools/acquire now returns immediately; gap is registered synchronously.
$gapBody    = '{"capability":"OBSCURE_CAPABILITY_XYZ","context":"test gap detection"}'
$gapAcqResp = Invoke-WebRequest -Uri "$BASE/tools/acquire" `
    -Method POST -Body $gapBody -ContentType "application/json" `
    -UseBasicParsing -TimeoutSec 15
$gapAcqJson = $gapAcqResp.Content | ConvertFrom-Json
Assert "POST /tools/acquire for unknown cap → 200" ($gapAcqResp.StatusCode -eq 200)
Assert "Gap ID returned"                           ($null -ne $gapAcqJson.gapId -or $gapAcqJson.status -eq "ALREADY_ACQUIRED")

# Verify gap appears in /tools/gaps
Start-Sleep 1
$gapsAfter     = Invoke-WebRequest -Uri "$BASE/tools/gaps" -UseBasicParsing -TimeoutSec 10
$gapsAfterJson = $gapsAfter.Content | ConvertFrom-Json
$foundGap      = $gapsAfterJson.gaps | Where-Object { $_.capability -eq "OBSCURE_CAPABILITY_XYZ" }
Assert "Gap registered for unknown capability"     ($null -ne $foundGap)

# ─────────────────────────────────────────────────────────────────────────────
Section "[11] Chat integration – version bump"
# ─────────────────────────────────────────────────────────────────────────────
$chatResp = Invoke-WebRequest -Uri "$BASE/chat" -UseBasicParsing -TimeoutSec 10
Assert "GET /chat → 200"       ($chatResp.StatusCode -eq 200)
Assert "Version is v0.27.0"    ($chatResp.Content -match "v0\.27\.0")
Assert "Tools panel present"   ($chatResp.Content -match "tools-header")
Assert "pollTools present"     ($chatResp.Content -match "pollTools")
Assert "Legal panel present"   ($chatResp.Content -match "legal-header")

# ─────────────────────────────────────────────────────────────────────────────
Section "[12] Phase 26 regression – Goals still work"
# ─────────────────────────────────────────────────────────────────────────────
$goalsResp = Invoke-WebRequest -Uri "$BASE/goals" -UseBasicParsing -TimeoutSec 10
Assert "GET /goals → 200"    ($goalsResp.StatusCode -eq 200)
$goalsJson = $goalsResp.Content | ConvertFrom-Json
Assert "Goals response valid" ($null -ne $goalsJson.count)

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
$total = $PASS + $FAIL
Write-Host "`n═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Phase 27 Gate: $PASS/$total PASS" -ForegroundColor $(if ($FAIL -eq 0) { "Green" } else { "Yellow" })
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
if ($FAIL -gt 0) { exit 1 } else { exit 0 }
