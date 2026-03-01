# Archimedes Stress Test Suite  (Phase 19+20 era)
# Sections: Burst | LLM-Stress | Buffer-Overflow | Soak
# Usage: .\scripts\stress-test.ps1
# Duration: ~25-30 minutes

param(
    [string]$BaseUrl     = "http://localhost:5051",
    [int]   $SoakMinutes = 15
)

$ErrorActionPreference = "SilentlyContinue"

# ── counters ─────────────────────────────────────────────────────────────────
$pass   = 0
$fail   = 0
$warn   = 0
$suiteStart = Get-Date

function Ck-Pass([string]$msg) {
    Write-Host "  PASS  $msg" -ForegroundColor Green
    $script:pass++
}
function Ck-Fail([string]$msg) {
    Write-Host "  FAIL  $msg" -ForegroundColor Red
    $script:fail++
}
function Ck-Warn([string]$msg) {
    Write-Host "  WARN  $msg" -ForegroundColor Yellow
    $script:warn++
}
function Info([string]$msg) {
    Write-Host "        $msg" -ForegroundColor DarkGray
}

function Section([string]$title) {
    Write-Host ""
    Write-Host "=====================================================" -ForegroundColor Cyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host "=====================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Get-CoreMem {
    $p = Get-Process "Archimedes.Core" -ErrorAction SilentlyContinue
    if ($p) { return [math]::Round($p.WorkingSet64 / 1MB) }
    return 0
}

# ── pre-flight ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Archimedes Stress Test Suite" -ForegroundColor Cyan
Write-Host "  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

try {
    $h = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 5
    if ($h -ne "OK") { throw "health not OK" }
    Write-Host "  Core reachable at $BaseUrl" -ForegroundColor Green
} catch {
    Write-Host "  FATAL: Core not reachable at $BaseUrl - aborting" -ForegroundColor Red
    exit 1
}

# ============================================================================
# SECTION 1 - Burst (50 rapid /health requests, concurrency stress)
# ============================================================================
Section "Section 1 - Burst (50 concurrent /health requests)"

$burstN      = 50
$burstStart  = Get-Date
$jobs        = @()

Write-Host "  Firing $burstN requests via parallel jobs..." -ForegroundColor Yellow

for ($i = 0; $i -lt $burstN; $i++) {
    $jobs += Start-Job -ScriptBlock {
        param($url)
        try {
            $r = Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 10
            return $r.StatusCode
        } catch { return 0 }
    } -ArgumentList $BaseUrl
}

$results  = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job -Force

$burstElapsed = [math]::Round(((Get-Date) - $burstStart).TotalSeconds, 1)
$ok200        = ($results | Where-Object { $_ -eq 200 }).Count
$failures     = $burstN - $ok200
$rps          = if ($burstElapsed -gt 0) { [math]::Round($burstN / $burstElapsed, 1) } else { 0 }

Info "Completed in ${burstElapsed}s  (~${rps} req/s)"
Info "${ok200}/${burstN} returned 200,  failures=${failures}"

if ($failures -eq 0)     { Ck-Pass "50/50 burst requests returned 200" }
elseif ($failures -le 3) { Ck-Warn "Burst: $failures/$burstN failed (minor, possibly OS thread limits)" }
else                     { Ck-Fail "Burst: $failures/$burstN failed - server cannot handle concurrency" }

if ($rps -ge 5)  { Ck-Pass "Burst throughput ${rps} req/s >= 5" }
elseif ($rps -ge 2) { Ck-Warn "Burst throughput ${rps} req/s (slow but alive)" }
else             { Ck-Fail "Burst throughput ${rps} req/s - too slow" }

# Core still alive after burst
$r = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 5
if ($r -eq "OK") { Ck-Pass "Core alive after burst" }
else             { Ck-Fail "Core unresponsive after burst" }

# Trace count sanity after burst (should have created traces)
try {
    $traces = Invoke-RestMethod "$BaseUrl/traces" -TimeoutSec 5
    $inFlight = $traces.activeCount
    Info "Active traces after burst: $inFlight  (should be 0)"
    if ($inFlight -eq 0) { Ck-Pass "No orphaned traces after burst" }
    else                 { Ck-Warn "Active traces not zero after burst: $inFlight (may still be draining)" }
} catch { Ck-Warn "Could not check trace count" }

# ============================================================================
# SECTION 2 - LLM Stress (15 sequential calls + memory leak detection)
# ============================================================================
Section "Section 2 - LLM Stress (15 sequential inference calls)"

$memBefore = Get-CoreMem
Info "Memory before LLM stress: ${memBefore} MB"

$llmSuccess  = 0
$llmFailed   = 0
$llmTimes    = @()

$llmPrompts = @(
    "monitor testsite dashboard",
    "login to the system",
    "download file from server",
    "extract table data from page",
    "export the CSV data",
    "watch for price changes",
    "retrieve structured records",
    "save a copy of the page",
    "access with credentials",
    "get data from endpoint",
    "I need to pull numbers from a webpage",
    "keep an eye on whether prices change",
    "grab the file sitting on the remote host",
    "alert me when the dashboard numbers update",
    "fetch the binary from the distribution server"
)

Write-Host "  Running $($llmPrompts.Count) sequential LLM calls..." -ForegroundColor Yellow

for ($i = 0; $i -lt $llmPrompts.Count; $i++) {
    $t = Get-Date
    try {
        $r  = Invoke-WebRequest "$BaseUrl/llm/interpret" `
              -Method POST -Body $llmPrompts[$i] -ContentType "text/plain" `
              -UseBasicParsing -TimeoutSec 120 -ErrorAction Stop
        $ms = [math]::Round(((Get-Date) - $t).TotalMilliseconds)
        $b  = $r.Content | ConvertFrom-Json

        if (-not [string]::IsNullOrEmpty($b.intent)) {
            $llmSuccess++
            $llmTimes += $ms
            $hTag = if ($b.isHeuristicFallback) { "[H]" } else { "[L]" }
            Info "  [$($i+1)/15] $hTag intent=$($b.intent)  ${ms}ms"
        } else {
            $llmFailed++
            Info "  [$($i+1)/15] EMPTY intent  ${ms}ms"
        }
    } catch {
        $llmFailed++
        Info "  [$($i+1)/15] EXCEPTION: $($_.Exception.Message)"
    }
}

$avgMs = if ($llmTimes.Count -gt 0) { [math]::Round(($llmTimes | Measure-Object -Average).Average) } else { 0 }
$maxMs = if ($llmTimes.Count -gt 0) { [math]::Round(($llmTimes | Measure-Object -Maximum).Maximum) } else { 0 }

Info "avg=${avgMs}ms  max=${maxMs}ms  success=${llmSuccess}  failed=${llmFailed}"

if ($llmFailed -eq 0)     { Ck-Pass "15/15 LLM calls returned valid intent" }
elseif ($llmFailed -le 2) { Ck-Warn "LLM: $llmFailed/15 failures (minor)" }
else                      { Ck-Fail "LLM: $llmFailed/15 failures - degradation detected" }

if ($avgMs -gt 0 -and $avgMs -lt 20000) { Ck-Pass "LLM avg response time ${avgMs}ms < 20s" }
elseif ($avgMs -lt 40000)               { Ck-Warn "LLM avg response time ${avgMs}ms (slow)" }
elseif ($avgMs -gt 0)                   { Ck-Fail "LLM avg response time ${avgMs}ms (too slow)" }

# Memory delta after 15 calls
Start-Sleep -Seconds 3
$memAfter = Get-CoreMem
$memDelta = $memAfter - $memBefore
Info "Memory after LLM stress: ${memAfter} MB  (delta: +${memDelta} MB)"

if ($memDelta -lt 200)     { Ck-Pass "Memory delta +${memDelta} MB - no leak" }
elseif ($memDelta -lt 500) { Ck-Warn "Memory delta +${memDelta} MB - monitor this" }
else                       { Ck-Fail "Memory delta +${memDelta} MB - possible leak" }

# Core still alive
$r = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 5
if ($r -eq "OK") { Ck-Pass "Core alive after LLM stress" }
else             { Ck-Fail "Core unresponsive after LLM stress" }

# ============================================================================
# SECTION 3 - Buffer Overflow (250+ traces - circular buffer test)
# ============================================================================
Section "Section 3 - Buffer Overflow (250+ traces - circular buffer integrity)"

Write-Host "  Creating 260 traces via /health calls (buffer max = 200)..." -ForegroundColor Yellow
Write-Host "  This will take ~30s..." -ForegroundColor DarkGray

$traceStart     = Get-Date
$traceIds       = @()
$tracesCreated  = 0

for ($i = 0; $i -lt 260; $i++) {
    $cid = "stress-buf-$i-" + [System.Guid]::NewGuid().ToString("N").Substring(0,8)
    try {
        $null = Invoke-WebRequest "$BaseUrl/health" `
                -Headers @{ "X-Correlation-Id" = $cid } `
                -UseBasicParsing -TimeoutSec 5
        $traceIds   += $cid
        $tracesCreated++
    } catch { }
    # Small sleep every 10 to avoid overwhelming the port
    if ($i % 10 -eq 9) { Start-Sleep -Milliseconds 100 }
}

$traceElapsed = [math]::Round(((Get-Date) - $traceStart).TotalSeconds, 1)
Info "Created $tracesCreated traces in ${traceElapsed}s"

# Check buffer state
Start-Sleep -Milliseconds 500
try {
    $traceStatus = Invoke-RestMethod "$BaseUrl/traces" -TimeoutSec 5
    $bufCount    = $traceStatus.bufferCount
    $activeCount = $traceStatus.activeCount
    Info "bufferCount=$bufCount  activeCount=$activeCount  (max=200)"

    if ($bufCount -le 200) { Ck-Pass "Buffer capped at $bufCount <= 200 (not exceeded)" }
    else                   { Ck-Fail "Buffer overflowed: $bufCount > 200" }

    if ($activeCount -eq 0) { Ck-Pass "No orphaned active traces" }
    else                    { Ck-Warn "Active traces after buffer test: $activeCount" }
} catch {
    Ck-Fail "Could not read /traces after buffer test: $($_.Exception.Message)"
}

# Verify first trace was purged (FIFO eviction)
if ($traceIds.Count -gt 0) {
    $firstId = $traceIds[0]
    try {
        $null = Invoke-RestMethod "$BaseUrl/traces/$firstId" -TimeoutSec 5
        Ck-Warn "First trace ($firstId) still retrievable - may be loaded from disk"
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code -eq 404) {
            Ck-Pass "First trace correctly purged from in-memory buffer (404)"
        } else {
            Ck-Warn "Unexpected response code $code for purged trace"
        }
    }
}

# Verify last trace still retrievable (recent traces kept)
if ($traceIds.Count -ge 200) {
    $lastId = $traceIds[-1]
    try {
        $t = Invoke-RestMethod "$BaseUrl/traces/$lastId" -TimeoutSec 5
        if ($t.correlationId -eq $lastId) { Ck-Pass "Most recent trace still retrievable" }
        else                              { Ck-Warn "Last trace exists but correlationId mismatch" }
    } catch {
        Ck-Warn "Last trace not found in buffer (may already be disk-only)"
    }
}

# Core still alive after buffer flood
$r = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 5
if ($r -eq "OK") { Ck-Pass "Core alive after buffer overflow test" }
else             { Ck-Fail "Core unresponsive after buffer overflow" }

# ============================================================================
# SECTION 4 - Soak (lightweight, N minutes, request every 30s)
# ============================================================================
Section "Section 4 - Soak ($SoakMinutes min lightweight - request every 30s)"

$soakStart      = Get-Date
$soakEnd        = $soakStart.AddMinutes($SoakMinutes)
$soakChecks     = 0
$soakFailures   = 0
$soakMemBefore  = Get-CoreMem
$soakTasksCreated = 0
$soakTasksFailed  = 0
$soakTaskIds    = @()
$checkNum       = 0

Write-Host "  Running until $(Get-Date $soakEnd -Format 'HH:mm:ss')  (every 30s)" -ForegroundColor Yellow
Write-Host ""

while ((Get-Date) -lt $soakEnd) {
    $checkNum++
    $elapsed   = [math]::Round(((Get-Date) - $soakStart).TotalMinutes, 1)
    $remaining = [math]::Round(($soakEnd - (Get-Date)).TotalMinutes, 1)

    # A) Health check
    try {
        $h = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 8
        if ($h -eq "OK") { $soakChecks++ }
        else { $soakFailures++ }
    } catch {
        $soakFailures++
    }

    # B) LLM call (every other check to avoid piling up)
    $llmOk = $true
    if ($checkNum % 2 -eq 0) {
        try {
            $lr = Invoke-WebRequest "$BaseUrl/llm/interpret" `
                  -Method POST -Body "export data from testsite" `
                  -ContentType "text/plain" -UseBasicParsing -TimeoutSec 60
            $lb = $lr.Content | ConvertFrom-Json
            if ([string]::IsNullOrEmpty($lb.intent)) { $llmOk = $false }
        } catch { $llmOk = $false }
    }

    # C) Criteria verify
    $criteriaOk = $true
    try {
        $cv = Invoke-RestMethod "$BaseUrl/criteria/verify" `
              -Method POST `
              -Body '{"Action":"http.login","StepSuccess":true,"Data":"{\"token\":\"soak\"}"}' `
              -ContentType "application/json" -TimeoutSec 5
        if ($cv.outcome -ne "VERIFIED") { $criteriaOk = $false }
    } catch { $criteriaOk = $false }

    # D) Create a task every 3rd check (lightweight load)
    if ($checkNum % 3 -eq 0) {
        try {
            $body = '{"Title":"Soak","UserPrompt":"export data from testsite"}'
            $created = Invoke-RestMethod "$BaseUrl/task" -Method POST `
                       -Body $body -ContentType "application/json" -TimeoutSec 5
            $soakTaskIds    += $created.taskId
            $soakTasksCreated++
        } catch { $soakTasksFailed++ }
    }

    # E) Memory check
    $soakMem = Get-CoreMem

    $statusParts = @(
        "t=${elapsed}min"
        "health=$(if ($soakChecks -gt 0) { 'OK' } else { 'FAIL' })"
        "mem=${soakMem}MB"
        "criteria=$(if ($criteriaOk) { 'OK' } else { 'FAIL' })"
    )
    $statusLine = $statusParts -join " | "
    $color = if ($soakFailures -eq 0 -and $criteriaOk) { "Green" } else { "Yellow" }
    Write-Host "  [${elapsed}min / ${remaining}min left]  $statusLine" -ForegroundColor $color

    Start-Sleep -Seconds 30
}

# ── Soak results ─────────────────────────────────────────────────────────────
$soakMemAfter = Get-CoreMem
$soakMemDelta = $soakMemAfter - $soakMemBefore
$soakDuration = [math]::Round(((Get-Date) - $soakStart).TotalMinutes, 1)

Write-Host ""
Info "Soak duration: ${soakDuration}min"
Info "Health checks: $soakChecks OK, $soakFailures failed"
Info "Tasks created: $soakTasksCreated  failed to create: $soakTasksFailed"
Info "Memory drift: ${soakMemBefore}MB -> ${soakMemAfter}MB (delta: +${soakMemDelta}MB)"

if ($soakFailures -eq 0)     { Ck-Pass "Soak: all $soakChecks health checks passed" }
elseif ($soakFailures -le 2) { Ck-Warn "Soak: $soakFailures transient health failures (acceptable)" }
else                         { Ck-Fail "Soak: $soakFailures health failures - server unstable" }

if ($soakTasksFailed -eq 0)  { Ck-Pass "Soak: all $soakTasksCreated task creations succeeded" }
else                         { Ck-Warn "Soak: $soakTasksFailed task creations failed" }

if ($soakMemDelta -lt 300)   { Ck-Pass "Soak memory drift +${soakMemDelta}MB - stable" }
elseif ($soakMemDelta -lt 700){ Ck-Warn "Soak memory drift +${soakMemDelta}MB - monitor" }
else                         { Ck-Fail "Soak memory drift +${soakMemDelta}MB - leak risk" }

# Final health after soak
$r = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 5
if ($r -eq "OK") { Ck-Pass "Core alive after full soak" }
else             { Ck-Fail "Core unresponsive after soak" }

# ============================================================================
# FINAL SUMMARY
# ============================================================================
$totalDur = [math]::Round(((Get-Date) - $suiteStart).TotalMinutes, 1)

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  STRESS TEST RESULTS  (${totalDur}min total)" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  PASS  : $pass" -ForegroundColor Green
Write-Host "  WARN  : $warn" -ForegroundColor Yellow
Write-Host "  FAIL  : $fail" -ForegroundColor $(if ($fail -gt 0) { "Red" } else { "Green" })
Write-Host ""

$color  = if ($fail -eq 0) { "Green" } else { "Red" }
$status = if ($fail -eq 0) { "ALL PASS" } else { "FAILED" }
Write-Host "  $status  ($pass pass, $warn warn, $fail fail)" -ForegroundColor $color
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

if ($fail -gt 0) { exit 1 }
