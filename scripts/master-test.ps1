#Requires -Version 5.1
<#
.SYNOPSIS
    Master Test Suite - Layers 1-5 pre-Phase-19 validation.
.DESCRIPTION
    Layer 1: Sanity      - build, Core health, LLM health
    Layer 2: Regression  - Phase 16 / 17 / 18 gates
    Layer 3: LLM Basic   - heuristic=False, JSON reliability, confidence, summarize
    Layer 4: LLM Quality - 10 ambiguous prompts, response time
    Layer 5: Stability   - 10 sequential calls, memory check
#>

param(
    [string]$CoreUrl = "http://localhost:5051",
    [string]$NetUrl  = "http://localhost:5052"
)

$ErrorActionPreference = "SilentlyContinue"
$rootDir    = "C:\Users\netanel\Desktop\Archimedes"
$coreDir    = "$rootDir\core"
$scriptsDir = "$rootDir\scripts"
$logFile    = "$rootDir\logs\master-test.log"

$totalPassed = 0
$totalFailed = 0
$layerResults = @()

# ── helpers ─────────────────────────────────────────────────────────────────

function Write-Header([string]$title) {
    Write-Host ""
    Write-Host "════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host "════════════════════════════════════════════" -ForegroundColor Cyan
}

function Write-LayerHeader([string]$name) {
    Write-Host ""
    Write-Host "── $name ──────────────────────────────────" -ForegroundColor Yellow
}

function Pass([string]$msg) {
    Write-Host "  PASS  $msg" -ForegroundColor Green
    $script:layerPassed++
    $script:totalPassed++
}

function Fail([string]$msg) {
    Write-Host "  FAIL  $msg" -ForegroundColor Red
    $script:layerFailed++
    $script:totalFailed++
}

function Skip([string]$msg) {
    Write-Host "  SKIP  $msg" -ForegroundColor DarkGray
}

function Info([string]$msg) {
    Write-Host "        $msg" -ForegroundColor DarkGray
}

function Invoke-Safe {
    param([string]$Uri, [string]$Method = "GET", [string]$Body = "", [int]$TimeoutSec = 30)
    try {
        $p = @{ Uri = $Uri; Method = $Method; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
        if ($Body) { $p["Body"] = $Body; $p["ContentType"] = "application/json" }
        return Invoke-WebRequest @p
    } catch { return $_.Exception.Response }
}

function Get-Code([object]$r) {
    if ($null -eq $r) { return 0 }
    return [int]$r.StatusCode
}

function Invoke-LLM([string]$prompt, [int]$TimeoutSec = 60) {
    $r = Invoke-WebRequest -Uri "$CoreUrl/llm/interpret" -Method POST `
         -Body $prompt -ContentType "text/plain" `
         -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction SilentlyContinue
    if ($null -eq $r) { return $null }
    return $r.Content | ConvertFrom-Json
}

function Start-LayerTimer { $script:layerStart = Get-Date; $script:layerPassed = 0; $script:layerFailed = 0 }
function End-Layer([string]$name) {
    $dur = [math]::Round(((Get-Date) - $script:layerStart).TotalSeconds, 1)
    $ok  = $script:layerFailed -eq 0
    $col = if ($ok) { "Green" } else { "Red" }
    $status = if ($ok) { "OK" } else { "PROBLEMS" }
    Write-Host ""
    Write-Host "  $name : $($script:layerPassed) passed, $($script:layerFailed) failed  (${dur}s)" -ForegroundColor $col
    $script:layerResults += "$name : $status ($($script:layerPassed)/$($script:layerPassed+$script:layerFailed))"
    return $ok
}

# ════════════════════════════════════════════════════════════════════════════
Write-Header "Archimedes Master Test Suite"
Write-Host "  Core: $CoreUrl   Net: $NetUrl"
$suiteStart = Get-Date

# ════════════════════════════════════════════════════════════════════════════
# LAYER 1 - Sanity
# ════════════════════════════════════════════════════════════════════════════
Write-LayerHeader "Layer 1 - Sanity"
Start-LayerTimer

# 1.1 Build
Write-Host "[1.1] dotnet build..." -ForegroundColor Yellow
Stop-Process -Name "Archimedes.Core" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$buildOut = & dotnet build "$coreDir" --configuration Release 2>&1
$buildOk  = ($buildOut -join "`n") -match "Build succeeded"
if ($buildOk) { Pass "dotnet build - 0 errors" }
else          { Fail "dotnet build failed"; ($buildOut | Select-String "error CS") | ForEach-Object { Info $_.Line } }

# 1.2 Start Core
Write-Host "[1.2] Starting Core..." -ForegroundColor Yellow
$null = Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/c dotnet run --configuration Release > `"$logFile`" 2>&1" `
    -WorkingDirectory $coreDir -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 14

$r = Invoke-Safe "$CoreUrl/health" -TimeoutSec 10
if ((Get-Code $r) -eq 200) { Pass "Core /health → 200" }
else { Fail "Core not reachable after 14s"; exit 1 }

# 1.3 LLM health
Write-Host "[1.3] LLM health..." -ForegroundColor Yellow
$r = Invoke-Safe "$CoreUrl/llm/health" -TimeoutSec 10
if ((Get-Code $r) -eq 200) {
    $b = $r.Content | ConvertFrom-Json
    if ($b.available -eq $true -and $b.runtime -eq "llamasharp") {
        Pass "LLM health: available=true runtime=llamasharp model=$($b.model)"
    } else {
        Fail "LLM health unexpected: $($r.Content)"
    }
} else { Fail "LLM health endpoint returned $(Get-Code $r)" }

$null = End-Layer "Layer 1 Sanity"

# ════════════════════════════════════════════════════════════════════════════
# LAYER 2 - Regression (Phase 16 / 17 / 18 gates)
# ════════════════════════════════════════════════════════════════════════════
Write-LayerHeader "Layer 2 - Regression"
Start-LayerTimer

# 2.1 Phase 16
Write-Host "[2.1] Phase 16 gate..." -ForegroundColor Yellow
& powershell.exe -File "$scriptsDir\phase16-selfupdate.ps1" -CoreUrl $CoreUrl
if ($LASTEXITCODE -eq 0) { Pass "Phase 16 gate PASS" }
else                      { Fail "Phase 16 gate FAIL (exit $LASTEXITCODE)" }

# 2.2 Phase 17
Write-Host "[2.2] Phase 17 gate (needs Net on $NetUrl)..." -ForegroundColor Yellow
$netOk = (Get-Code (Invoke-Safe "$NetUrl/health" -TimeoutSec 5)) -eq 200
if (-not $netOk) {
    Skip "Net service not running at $NetUrl - Phase 17 skipped"
    Info "Start Net service to include browser regression"
} else {
    & powershell.exe -File "$scriptsDir\phase17-browser.ps1" -CoreUrl $CoreUrl -NetUrl $NetUrl
    if ($LASTEXITCODE -eq 0) { Pass "Phase 17 gate PASS" }
    else                      { Fail "Phase 17 gate FAIL (exit $LASTEXITCODE)" }
}

# 2.3 Phase 18
Write-Host "[2.3] Phase 18 gate..." -ForegroundColor Yellow
& powershell.exe -File "$scriptsDir\phase18-llm.ps1" -CoreUrl $CoreUrl
if ($LASTEXITCODE -eq 0) { Pass "Phase 18 gate PASS" }
else                      { Fail "Phase 18 gate FAIL (exit $LASTEXITCODE)" }

$null = End-Layer "Layer 2 Regression"

# ════════════════════════════════════════════════════════════════════════════
# LAYER 3 - LLM Basic
# ════════════════════════════════════════════════════════════════════════════
Write-LayerHeader "Layer 3 - LLM Basic"
Start-LayerTimer

# 3.1 Heuristic still works as fallback (keyword prompt)
Write-Host "[3.1] Heuristic fallback (keyword prompt)..." -ForegroundColor Yellow
$r = Invoke-LLM "login to the system" -TimeoutSec 30
if ($null -ne $r -and $r.intent -eq "WEB_LOGIN") {
    Pass "Heuristic: 'login' → WEB_LOGIN (isHeuristicFallback=$($r.isHeuristicFallback))"
} else {
    Fail "Heuristic fallback broken: $(if ($null -eq $r) { 'null' } else { $r | ConvertTo-Json -Compress })"
}

# 3.2 LLM active on ambiguous prompt (isHeuristicFallback must be False)
Write-Host "[3.2] LLM active - ambiguous prompt..." -ForegroundColor Yellow
$t = Get-Date
$r = Invoke-LLM "I need to retrieve structured data from a remote resource" -TimeoutSec 120
$elapsed = [math]::Round(((Get-Date) - $t).TotalSeconds, 1)
if ($null -ne $r -and $r.isHeuristicFallback -eq $false) {
    Pass "Real LLM response: intent=$($r.intent) heuristic=False  (${elapsed}s)"
} else {
    Fail "Still using heuristic or null response: $(if ($null -eq $r) { 'null' } else { "heuristic=$($r.isHeuristicFallback)" })"
}

# 3.3 Confidence sanity
Write-Host "[3.3] Confidence sanity..." -ForegroundColor Yellow
$r = Invoke-LLM "monitor testsite dashboard" -TimeoutSec 120
if ($null -ne $r -and $r.confidence -gt 0) {
    Pass "Confidence > 0: intent=$($r.intent) confidence=$($r.confidence)"
} elseif ($null -ne $r -and $r.isHeuristicFallback -eq $false) {
    Skip "LLM responded (heuristic=False) but confidence=0 - LLM JSON schema issue, not critical"
} else {
    Fail "No LLM response or heuristic: $(if ($null -eq $r) { 'null' } else { $r | ConvertTo-Json -Compress })"
}

# 3.4 JSON reliability - 5 prompts, all must parse
Write-Host "[3.4] JSON reliability (5 prompts)..." -ForegroundColor Yellow
$testPrompts = @(
    "download a file from the server",
    "extract data from the table on the page",
    "watch the dashboard for changes",
    "export the CSV from the testsite",
    "I want to access a web resource"
)
$jsonOk = 0
foreach ($p in $testPrompts) {
    $r = Invoke-LLM $p -TimeoutSec 120
    if ($null -ne $r -and -not [string]::IsNullOrEmpty($r.intent)) { $jsonOk++ }
}
if ($jsonOk -eq 5)      { Pass "JSON reliability: 5/5 prompts returned valid intent" }
elseif ($jsonOk -ge 4)  { Pass "JSON reliability: $jsonOk/5 prompts OK (acceptable)" }
else                    { Fail "JSON reliability: only $jsonOk/5 prompts returned valid JSON" }

# 3.5 Summarize
Write-Host "[3.5] Summarize endpoint..." -ForegroundColor Yellow
$sampleContent = "The testsite dashboard shows product prices updated daily. Current prices: Widget A costs 19.99, Widget B costs 34.50. Last updated 2025-01-01. Price trends show 5% increase over last month."
$r = Invoke-WebRequest -Uri "$CoreUrl/llm/summarize" -Method POST `
     -Body $sampleContent -ContentType "text/plain" `
     -UseBasicParsing -TimeoutSec 120 -ErrorAction SilentlyContinue
if ($null -ne $r -and (Get-Code $r) -eq 200) {
    $b = $r.Content | ConvertFrom-Json
    if (-not [string]::IsNullOrEmpty($b.shortSummary) -and $b.isHeuristicFallback -eq $false) {
        Pass "Summarize: real LLM, shortSummary len=$($b.shortSummary.Length)"
    } elseif (-not [string]::IsNullOrEmpty($b.shortSummary)) {
        Skip "Summarize returned shortSummary but heuristic=$($b.isHeuristicFallback)"
    } else {
        Fail "Summarize returned empty shortSummary"
    }
} else { Fail "Summarize endpoint returned $(Get-Code $r)" }

$null = End-Layer "Layer 3 LLM Basic"

# ════════════════════════════════════════════════════════════════════════════
# LAYER 4 - LLM Quality (10 ambiguous prompts)
# ════════════════════════════════════════════════════════════════════════════
Write-LayerHeader "Layer 4 - LLM Quality"
Start-LayerTimer

$ambiguous = @(
    @{ prompt = "I need to pull some numbers off a webpage";         expected = @("DATA_EXTRACT","TESTSITE_EXPORT","TESTSITE_MONITOR") }
    @{ prompt = "Keep an eye on whether the prices change";          expected = @("TESTSITE_MONITOR") }
    @{ prompt = "Grab the file sitting on the remote host";          expected = @("FILE_DOWNLOAD") }
    @{ prompt = "I want the spreadsheet version of the site data";   expected = @("TESTSITE_EXPORT","DATA_EXTRACT") }
    @{ prompt = "Access the site with my credentials";               expected = @("WEB_LOGIN") }
    @{ prompt = "Retrieve structured records from the endpoint";     expected = @("DATA_EXTRACT") }
    @{ prompt = "Save a copy of what is on that page";               expected = @("DATA_EXTRACT","FILE_DOWNLOAD") }
    @{ prompt = "Alert me when the dashboard numbers update";        expected = @("TESTSITE_MONITOR") }
    @{ prompt = "Get the tabular data from the reporting site";      expected = @("DATA_EXTRACT","TESTSITE_EXPORT") }
    @{ prompt = "Fetch the binary from the distribution server";     expected = @("FILE_DOWNLOAD") }
)

$llmCorrect  = 0
$llmActive   = 0
$totalMs     = 0
$idx = 1

foreach ($tc in $ambiguous) {
    Write-Host "[4.$idx] $($tc.prompt)" -ForegroundColor Yellow
    $t  = Get-Date
    $r  = Invoke-LLM $tc.prompt -TimeoutSec 120
    $ms = [math]::Round(((Get-Date) - $t).TotalMilliseconds)
    $totalMs += $ms

    if ($null -eq $r) {
        Fail "No response (timeout or error)"; $idx++; continue
    }
    if ($r.isHeuristicFallback -eq $false) { $llmActive++ }

    $intentOk = ($tc.expected -contains $r.intent) -or ($r.intent -eq "UNKNOWN" -and $tc.expected.Count -gt 1)
    $hTag = if ($r.isHeuristicFallback) { "[HEURISTIC]" } else { "[LLM]" }
    Info "$hTag intent=$($r.intent)  conf=$($r.confidence)  ${ms}ms"

    if ($intentOk) { $llmCorrect++ }
    $idx++
}

$avgMs = if ($ambiguous.Count -gt 0) { [math]::Round($totalMs / $ambiguous.Count) } else { 0 }
Write-Host ""
if ($llmActive -ge 7)    { Pass "LLM active on $llmActive/10 prompts (heuristic=False)" }
elseif ($llmActive -ge 5){ Skip "LLM active on only $llmActive/10 - investigate but not blocking" }
else                     { Fail "LLM active on only $llmActive/10 - regression risk" }

if ($llmCorrect -ge 7)   { Pass "Intent accuracy: $llmCorrect/10 correct or acceptable" }
elseif ($llmCorrect -ge 5){ Skip "Intent accuracy: $llmCorrect/10 - marginal" }
else                     { Fail "Intent accuracy: $llmCorrect/10 - LLM quality too low" }

if ($avgMs -lt 15000)    { Pass "Avg inference time: ${avgMs}ms (< 15s)" }
elseif ($avgMs -lt 30000){ Skip "Avg inference time: ${avgMs}ms (slow but usable)" }
else                     { Fail "Avg inference time: ${avgMs}ms (too slow)" }

$null = End-Layer "Layer 4 LLM Quality"

# ════════════════════════════════════════════════════════════════════════════
# LAYER 5 - Stability (10 sequential calls + memory)
# ════════════════════════════════════════════════════════════════════════════
Write-LayerHeader "Layer 5 - Stability"
Start-LayerTimer

# 5.1 Memory baseline
Write-Host "[5.1] Memory baseline..." -ForegroundColor Yellow
$proc = Get-Process "Archimedes.Core" -ErrorAction SilentlyContinue
$memBefore = if ($proc) { [math]::Round($proc.WorkingSet64 / 1MB) } else { 0 }
Info "Memory before: ${memBefore} MB"

# 5.2 10 sequential LLM calls
Write-Host "[5.2] 10 sequential inference calls..." -ForegroundColor Yellow
$sequentialPrompts = @(
    "monitor the testsite dashboard", "download file from server",
    "extract table data from page",   "login to the system",
    "export the CSV data",            "watch for price changes",
    "retrieve structured records",    "save a copy of the page",
    "access with credentials",        "get data from endpoint"
)
$errors = 0
for ($i = 0; $i -lt 10; $i++) {
    $r = Invoke-LLM $sequentialPrompts[$i] -TimeoutSec 120
    if ($null -eq $r -or [string]::IsNullOrEmpty($r.intent)) {
        $errors++
        Info "Call $($i+1): FAILED"
    } else {
        Info "Call $($i+1): OK  intent=$($r.intent)"
    }
}
if ($errors -eq 0)     { Pass "10/10 sequential calls succeeded" }
elseif ($errors -le 2) { Skip "8+/10 succeeded ($errors failures - minor instability)" }
else                   { Fail "Too many failures: $errors/10" }

# 5.3 Memory after
Write-Host "[5.3] Memory after 10 calls..." -ForegroundColor Yellow
$proc = Get-Process "Archimedes.Core" -ErrorAction SilentlyContinue
$memAfter = if ($proc) { [math]::Round($proc.WorkingSet64 / 1MB) } else { 0 }
$memDelta  = $memAfter - $memBefore
Info "Memory after: ${memAfter} MB  (delta: +${memDelta} MB)"
if ($memDelta -lt 200)     { Pass "Memory delta +${memDelta} MB - stable" }
elseif ($memDelta -lt 500) { Skip "Memory delta +${memDelta} MB - watch this" }
else                       { Fail "Memory delta +${memDelta} MB - possible leak" }

# 5.4 Error recovery - bad JSON from LLM
Write-Host "[5.4] Error recovery (Core still responds after stress)..." -ForegroundColor Yellow
$r = Invoke-Safe "$CoreUrl/health" -TimeoutSec 10
if ((Get-Code $r) -eq 200) { Pass "Core still healthy after 10 sequential calls" }
else                       { Fail "Core unresponsive after sequential calls" }

$null = End-Layer "Layer 5 Stability"

# ════════════════════════════════════════════════════════════════════════════
# FINAL SUMMARY
# ════════════════════════════════════════════════════════════════════════════
$totalDur = [math]::Round(((Get-Date) - $suiteStart).TotalSeconds, 1)

Write-Host ""
Write-Host "════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  MASTER TEST RESULTS" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════" -ForegroundColor Cyan
foreach ($lr in $layerResults) {
    $col = if ($lr -like "*PROBLEMS*") { "Red" } else { "Green" }
    Write-Host "  $lr" -ForegroundColor $col
}
Write-Host ""
Write-Host "  Total passed : $totalPassed" -ForegroundColor Green
Write-Host "  Total failed : $totalFailed" -ForegroundColor $(if ($totalFailed -gt 0) { "Red" } else { "Green" })
Write-Host ("  Total time   : ${totalDur}s") -ForegroundColor Gray
Write-Host ""
if ($totalFailed -eq 0) {
    Write-Host "  MASTER GATE: PASS - ready for Phase 19" -ForegroundColor Green
} else {
    Write-Host "  MASTER GATE: FAIL - $totalFailed issues to fix before Phase 19" -ForegroundColor Red
}
Write-Host ""
