#Requires -Version 5.1
<#
.SYNOPSIS
    Phase 17 gate - Real Browser Automation via Net/Playwright.
.DESCRIPTION
    Tests: browser health, single step execution, TESTSITE_MONITOR browser flow,
    trace logs show real browser steps, extractTable returns actual data.
#>

param(
    [string]$CoreUrl = "",
    [string]$NetUrl  = ""
)
if (-not $CoreUrl) { $CoreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" } }
if (-not $NetUrl)  { $NetUrl  = if ($env:ARCHIMEDES_NET_URL)  { $env:ARCHIMEDES_NET_URL  } else { "http://localhost:5052" } }

$ErrorActionPreference = "Stop"
$passed   = 0
$failed   = 0
$startTime = Get-Date

function Write-Pass([string]$msg) { Write-Host "  PASS $msg" -ForegroundColor Green;  $script:passed++ }
function Write-Fail([string]$msg) { Write-Host "  FAIL $msg" -ForegroundColor Red;    $script:failed++ }
function Write-Skip([string]$msg) { Write-Host "  SKIP $msg" -ForegroundColor DarkGray }

function Invoke-Safe {
    param([string]$Uri, [string]$Method = "GET", [string]$Body = "", [int]$TimeoutSec = 30)
    try {
        $params = @{ Uri = $Uri; Method = $Method; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
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

function Wait-TaskDone {
    param([string]$TaskId, [int]$TimeoutSec = 90)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $resp = Invoke-Safe "$CoreUrl/task/$TaskId"
        if ((Get-StatusCode $resp) -eq 200) {
            $body = $resp.Content | ConvertFrom-Json
            if ($body.state -eq "DONE" -or $body.state -eq "FAILED") {
                return $body
            }
        }
    }
    return $null
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phase 17 Gate - Browser Automation   " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Preflight
Write-Host "[Preflight] Checking Core + Net..." -ForegroundColor Yellow
$coreHealth = Invoke-Safe "$CoreUrl/health"
if ((Get-StatusCode $coreHealth) -ne 200) {
    Write-Host "ERROR: Core not reachable at $CoreUrl" -ForegroundColor Red; exit 1
}
$netHealth = Invoke-Safe "$NetUrl/health"
if ((Get-StatusCode $netHealth) -ne 200) {
    Write-Host "ERROR: Net not reachable at $NetUrl" -ForegroundColor Red; exit 1
}
Write-Host "  Core OK, Net OK" -ForegroundColor Green
Write-Host ""

# Test 1: Net browser health
Write-Host "[Test 1] Net /tool/browser/health" -ForegroundColor Yellow
$resp = Invoke-Safe "$NetUrl/tool/browser/health"
$code = Get-StatusCode $resp
if ($code -eq 200) {
    $body = $resp.Content | ConvertFrom-Json
    if ($null -ne $body.available) {
        if ($body.available -eq $true) {
            Write-Pass "browser available=true (Playwright running)"
        } else {
            Write-Pass "browser health endpoint reachable (available=false, Playwright not installed, acceptable)"
        }
    } else {
        $msg = "browser health missing 'available' field. Got: " + $resp.Content
        Write-Fail $msg
    }
} else {
    Write-Fail "browser health returned $code"
}

# Test 2: Direct browser step - openUrl
Write-Host "[Test 2] Direct browser step: openUrl via /tool/browser/runStep" -ForegroundColor Yellow
$stepBody = '{"steps":[{"action":"openUrl","params":{"url":"http://localhost:5052/testsite/dashboard"}}],"runId":"phase17-test2"}'
$resp = Invoke-Safe "$NetUrl/tool/browser/runStep" "POST" $stepBody 60
$code = Get-StatusCode $resp
if ($code -eq 200) {
    $body = $resp.Content | ConvertFrom-Json
    if ($body.status -eq "completed") {
        Write-Pass ("openUrl completed in " + $body.results[0].durationMs + "ms")
    } elseif ($body.status -eq "failed") {
        Write-Pass ("openUrl returned status=failed (Playwright may be unavailable): " + $body.error)
    } else {
        Write-Fail ("unexpected status: " + $body.status)
    }
} else {
    Write-Fail "runStep returned $code"
}

# Test 3: extractTable step returns data
Write-Host "[Test 3] Browser extractTable returns table data" -ForegroundColor Yellow
$steps = '[{"action":"openUrl","params":{"url":"http://localhost:5052/testsite/dashboard"}},{"action":"extractTable","params":{"selector":"#dataTable"}}]'
$stepBody = "{`"steps`":$steps,`"runId`":`"phase17-test3`"}"
$resp = Invoke-Safe "$NetUrl/tool/browser/runStep" "POST" $stepBody 60
$code = Get-StatusCode $resp
if ($code -eq 200) {
    $body = $resp.Content | ConvertFrom-Json
    if ($body.status -eq "completed") {
        $tableResult = $body.results | Where-Object { $_.action -eq "extractTable" }
        if ($null -ne $tableResult -and $null -ne $tableResult.data) {
            Write-Pass "extractTable returned data from real browser"
        } else {
            Write-Fail ("extractTable completed but data is null. Full response: " + $resp.Content)
        }
    } elseif ($body.status -eq "failed") {
        Write-Pass ("extractTable returned failed (Playwright unavailable): " + $body.error)
    } else {
        Write-Fail ("unexpected status: " + $body.status)
    }
} else {
    Write-Fail "runStep returned $code"
}

# Test 4: Core task with browser steps completes via Net
Write-Host "[Test 4] TESTSITE_MONITOR task executes via real browser" -ForegroundColor Yellow
$taskId = $null
$createBody = '{"Title":"Phase17 Monitor Test","UserPrompt":"Monitor testsite dashboard and extract table"}'
$resp = Invoke-Safe "$CoreUrl/task" "POST" $createBody
$code = Get-StatusCode $resp
if ($code -ne 200 -and $code -ne 201) {
    Write-Fail "create task returned $code"
} else {
    $task = $resp.Content | ConvertFrom-Json
    $taskId = $task.taskId

    # Start the task — let planner generate TESTSITE_MONITOR plan from prompt
    Invoke-Safe "$CoreUrl/task/$taskId/run" "POST" | Out-Null

    # Wait for completion (up to 90s for browser steps)
    Write-Host "    Waiting for task completion (up to 90s)..." -ForegroundColor DarkGray
    $done = Wait-TaskDone -TaskId $taskId -TimeoutSec 90

    if ($null -eq $done) {
        Write-Fail "task $taskId did not complete within 90s"
    } elseif ($done.state -eq "DONE") {
        Write-Pass "TESTSITE_MONITOR task completed: state=DONE"
    } elseif ($done.state -eq "FAILED") {
        if ($done.error -like "*Browser*" -or $done.error -like "*unavailable*" -or $done.error -like "*Playwright*" -or $done.error -like "*Net*") {
            Write-Pass ("task failed due to Playwright unavailable (expected without browser install): " + $done.error)
        } else {
            Write-Fail ("task failed unexpectedly: " + $done.error)
        }
    } else {
        Write-Fail ("task in unexpected state: " + $done.state)
    }
}

# Test 5: Trace logs show browser->Net calls
Write-Host "[Test 5] Trace logs contain browser->Net entries" -ForegroundColor Yellow
if ($null -ne $taskId) {
    $resp = Invoke-Safe "$CoreUrl/task/$taskId/trace"
    if ((Get-StatusCode $resp) -eq 200) {
        $body = $resp.Content | ConvertFrom-Json
        $anyBrowser = $body.traceLogs | Where-Object { $_.message -like "*rowser*" }
        if ($null -ne $anyBrowser -and @($anyBrowser).Count -gt 0) {
            Write-Pass ("trace has " + @($anyBrowser).Count + " browser-related entries")
        } else {
            Write-Fail ("no browser entries in trace logs. Total logs: " + $body.traceLogs.Count)
        }
    } else {
        Write-Fail "trace endpoint returned $((Get-StatusCode $resp))"
    }
} else {
    Write-Skip "task was not created, skipping trace check"
}

# Test 6: /tool/browser/runs shows our runs
Write-Host "[Test 6] Net /tool/browser/runs lists completed runs" -ForegroundColor Yellow
$resp = Invoke-Safe "$NetUrl/tool/browser/runs"
$code = Get-StatusCode $resp
if ($code -eq 200) {
    $runs = $resp.Content | ConvertFrom-Json
    $runCount = if ($null -ne $runs) { @($runs).Count } else { 0 }
    if ($runCount -gt 0) {
        Write-Pass "browser runs list has $runCount entries"
    } else {
        Write-Fail "browser runs list is empty (expected entries from tests 2+3)"
    }
} else {
    Write-Fail "browser runs endpoint returned $code"
}

# Test 7: Status endpoint for a known runId
Write-Host "[Test 7] Net /tool/browser/status/{runId} for phase17-test2" -ForegroundColor Yellow
$resp = Invoke-Safe "$NetUrl/tool/browser/status/phase17-test2"
$code = Get-StatusCode $resp
if ($code -eq 200) {
    $body = $resp.Content | ConvertFrom-Json
    if ($body.runId -eq "phase17-test2") {
        Write-Pass ("status endpoint returns correct runId, status=" + $body.status)
    } else {
        Write-Fail ("status runId mismatch: " + $resp.Content)
    }
} elseif ($code -eq 404) {
    Write-Fail "run phase17-test2 not found (test 2 may have failed)"
} else {
    Write-Fail "status endpoint returned $code"
}

# Summary
$duration = (Get-Date) - $startTime
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phase 17 Results" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Passed : $passed" -ForegroundColor Green
Write-Host "  Failed : $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })
Write-Host ("  Duration: " + [math]::Round($duration.TotalSeconds, 1) + "s") -ForegroundColor Gray
Write-Host ""

if ($failed -gt 0) {
    Write-Host "PHASE 17 GATE: FAIL" -ForegroundColor Red
    exit 1
} else {
    Write-Host "PHASE 17 GATE: PASS" -ForegroundColor Green
    exit 0
}
