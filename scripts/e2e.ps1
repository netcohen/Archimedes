#!/usr/bin/env pwsh
# Phase 14.2 E2E Test Suite
# Runs comprehensive end-to-end tests for Archimedes Core + Net
# Expected runtime: < 2 minutes

param(
    [switch]$StartServers = $false,
    [switch]$Verbose = $false
)

$ErrorActionPreference = "Stop"
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }
$netUrl = if ($env:ARCHIMEDES_NET_URL) { $env:ARCHIMEDES_NET_URL } else { "http://localhost:5052" }
$passed = 0
$failed = 0
$startTime = Get-Date

Write-Host "=== Archimedes Phase 14.2 E2E Test Suite ===" -ForegroundColor Cyan
Write-Host "Start time: $startTime"
Write-Host ""

# Helper function using curl for reliability
function Test-Endpoint {
    param($Name, $Url, $Method = "GET", $Body = $null, $ExpectedStatus = 200)
    
    try {
        if ($Body) {
            $tempFile = [System.IO.Path]::GetTempFileName()
            Set-Content -Path $tempFile -Value $Body -Encoding ASCII
            $output = curl.exe -s -w "`n%{http_code}" -X $Method $Url -H "Content-Type: application/json" --data-binary "@$tempFile" 2>&1
            Remove-Item $tempFile -ErrorAction SilentlyContinue
        } else {
            $output = curl.exe -s -w "`n%{http_code}" -X $Method $Url 2>&1
        }
        
        $lines = $output -split "`n"
        $statusCode = [int]($lines[-1])
        $content = ($lines[0..($lines.Length-2)]) -join "`n"
        
        if ($statusCode -eq $ExpectedStatus) {
            Write-Host "  PASS: $Name" -ForegroundColor Green
            $data = $null
            if ($content) {
                try { $data = $content | ConvertFrom-Json } catch { }
            }
            return @{ Pass = $true; Data = $data }
        } else {
            Write-Host "  FAIL: $Name (status $statusCode)" -ForegroundColor Red
            return @{ Pass = $false }
        }
    } catch {
        Write-Host "  FAIL: $Name - $($_.Exception.Message)" -ForegroundColor Red
        return @{ Pass = $false }
    }
}

# ========== Section 1: Health Checks ==========
Write-Host "`n[1] Health Checks" -ForegroundColor Yellow

$coreHealth = Test-Endpoint "Core /health" "$coreUrl/health"
if ($coreHealth.Pass) { $passed++ } else { $failed++ }

$netHealth = Test-Endpoint "Net /health" "$netUrl/health"
if ($netHealth.Pass) { $passed++ } else { $failed++ }

$deepHealth = Test-Endpoint "Core /health/deep" "$coreUrl/health/deep"
if ($deepHealth.Pass) { 
    $passed++
    if ($deepHealth.Data.runner.running -eq $true) {
        Write-Host "    - Runner: running" -ForegroundColor Gray
    }
    if ($deepHealth.Data.runner.watchdogEnabled -eq $true) {
        Write-Host "    - Watchdog: enabled" -ForegroundColor Gray
    }
} else { $failed++ }

# ========== Section 2: Scheduler Config ==========
Write-Host "`n[2] Scheduler Config" -ForegroundColor Yellow

$configGet = Test-Endpoint "GET /scheduler/config" "$coreUrl/scheduler/config"
if ($configGet.Pass) { 
    $passed++
    Write-Host "    - runnerIntervalMs: $($configGet.Data.runnerIntervalMs)" -ForegroundColor Gray
    Write-Host "    - watchdogSeconds: $($configGet.Data.watchdogSeconds)" -ForegroundColor Gray
} else { $failed++ }

# Test POST with empty body (should succeed with no changes)
$configPostEmpty = Test-Endpoint "POST /scheduler/config (empty)" "$coreUrl/scheduler/config" "POST" "{}"
if ($configPostEmpty.Pass) { $passed++ } else { $failed++ }

# Test POST with invalid value (should return 400)
try {
    $invalidBody = '{"runnerIntervalMs": 10}'
    $response = Invoke-WebRequest -Uri "$coreUrl/scheduler/config" -Method POST -Body $invalidBody -ContentType "application/json" -ErrorAction Stop
    Write-Host "  FAIL: Invalid config should return 400" -ForegroundColor Red
    $failed++
} catch {
    if ($_.Exception.Response.StatusCode -eq 400) {
        Write-Host "  PASS: POST /scheduler/config (invalid) -> 400" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Expected 400, got $($_.Exception.Response.StatusCode)" -ForegroundColor Red
        $failed++
    }
}

# ========== Section 3: Task Lifecycle ==========
Write-Host "`n[3] Task Lifecycle" -ForegroundColor Yellow

# Create task
$createBody = '{"Title":"E2E Test Task","UserPrompt":"Login to the local testsite and download the CSV, then summarize the first 3 rows."}'
try {
    $createResponse = Invoke-RestMethod -Uri "$coreUrl/task" -Method POST -Body $createBody -ContentType "application/json"
    $taskId = $createResponse.taskId
    
    if ($taskId) {
        Write-Host "  PASS: Create task (id=$taskId)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Create task (no taskId)" -ForegroundColor Red
        $failed++
    }
    
    # Verify prompt hash
    if ($createResponse.userPromptLength -gt 0 -and $createResponse.userPromptHash -notlike "E3B0C442*") {
        Write-Host "  PASS: Prompt persisted (length=$($createResponse.userPromptLength))" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Prompt not persisted correctly" -ForegroundColor Red
        $failed++
    }
    
    # Start run
    $runResponse = Invoke-RestMethod -Uri "$coreUrl/task/$taskId/run" -Method POST
    if ($runResponse.state -eq "RUNNING") {
        Write-Host "  PASS: Start run (state=RUNNING)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Start run (state=$($runResponse.state))" -ForegroundColor Red
        $failed++
    }
    
    # Wait for completion (max 30s)
    $deadline = (Get-Date).AddSeconds(30)
    $finalState = "RUNNING"
    while ((Get-Date) -lt $deadline -and $finalState -eq "RUNNING") {
        Start-Sleep -Seconds 2
        $checkResponse = Invoke-RestMethod -Uri "$coreUrl/task/$taskId" -Method GET
        $finalState = $checkResponse.state
        if ($Verbose) {
            Write-Host "    - Check: state=$finalState, step=$($checkResponse.currentStep)" -ForegroundColor Gray
        }
    }
    
    # Verify final state
    $finalTask = Invoke-RestMethod -Uri "$coreUrl/task/$taskId" -Method GET
    
    if ($finalTask.planHash) {
        Write-Host "  PASS: Plan generated (hash=$($finalTask.planHash))" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Plan not generated" -ForegroundColor Red
        $failed++
    }
    
    if ($finalTask.currentStep -gt 0) {
        Write-Host "  PASS: Steps executed (currentStep=$($finalTask.currentStep))" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: No steps executed" -ForegroundColor Red
        $failed++
    }
    
    if ($finalTask.state -eq "DONE") {
        Write-Host "  PASS: Task completed (state=DONE)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Task not completed (state=$($finalTask.state))" -ForegroundColor Red
        $failed++
    }
    
    if ($finalTask.resultSummary -match "Alpha|Beta|Gamma") {
        Write-Host "  PASS: Summary contains CSV data" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Summary missing CSV data" -ForegroundColor Red
        $failed++
    }
    
} catch {
    Write-Host "  FAIL: Task lifecycle - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# ========== Section 4: Debug Endpoints ==========
Write-Host "`n[4] Debug Endpoints" -ForegroundColor Yellow

if ($taskId) {
    $traceResult = Test-Endpoint "GET /task/$taskId/trace" "$coreUrl/task/$taskId/trace"
    if ($traceResult.Pass) { 
        $passed++
        if ($traceResult.Data.traceLogs.Count -gt 0) {
            Write-Host "    - Trace logs: $($traceResult.Data.traceLogs.Count) entries" -ForegroundColor Gray
        }
    } else { $failed++ }
}

$runningResult = Test-Endpoint "GET /tasks/running" "$coreUrl/tasks/running"
if ($runningResult.Pass) { 
    $passed++
    Write-Host "    - Running tasks: $($runningResult.Data.count)" -ForegroundColor Gray
} else { $failed++ }

# ========== Section 5: Testsite API ==========
Write-Host "`n[5] Testsite API" -ForegroundColor Yellow

$testsiteInfo = Test-Endpoint "GET /testsite/api/info" "$netUrl/testsite/api/info"
if ($testsiteInfo.Pass) { $passed++ } else { $failed++ }

$testsiteData = Test-Endpoint "GET /testsite/api/data" "$netUrl/testsite/api/data"
if ($testsiteData.Pass) { 
    $passed++
    if ($testsiteData.Data.rows.Count -eq 4) {
        Write-Host "    - Data rows: 4" -ForegroundColor Gray
    }
} else { $failed++ }

# Test login API
$loginBody = '{"username":"test","password":"test"}'
try {
    $loginResponse = Invoke-RestMethod -Uri "$netUrl/testsite/api/login" -Method POST -Body $loginBody -ContentType "application/json"
    if ($loginResponse.success -eq $true) {
        Write-Host "  PASS: POST /testsite/api/login" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Login failed" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: Login API - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# ========== Section 6: Empty Prompt Rejection ==========
Write-Host "`n[6] Input Validation" -ForegroundColor Yellow

try {
    $emptyBody = '{"Title":"Empty","UserPrompt":""}'
    Invoke-RestMethod -Uri "$coreUrl/task" -Method POST -Body $emptyBody -ContentType "application/json" -ErrorAction Stop
    Write-Host "  FAIL: Empty prompt should be rejected" -ForegroundColor Red
    $failed++
} catch {
    Write-Host "  PASS: Empty prompt rejected" -ForegroundColor Green
    $passed++
}

# ========== Section 7: Concurrent Tasks ==========
Write-Host "`n[7] Concurrent Tasks" -ForegroundColor Yellow

$concurrentTasks = @()
for ($i = 1; $i -le 3; $i++) {
    $body = "{`"Title`":`"Concurrent $i`",`"UserPrompt`":`"Login to testsite and download CSV $i`"}"
    try {
        $task = Invoke-RestMethod -Uri "$coreUrl/task" -Method POST -Body $body -ContentType "application/json"
        Invoke-RestMethod -Uri "$coreUrl/task/$($task.taskId)/run" -Method POST | Out-Null
        $concurrentTasks += $task.taskId
    } catch {
        Write-Host "  FAIL: Create concurrent task $i" -ForegroundColor Red
        $failed++
    }
}

if ($concurrentTasks.Count -eq 3) {
    Write-Host "  PASS: Created 3 concurrent tasks" -ForegroundColor Green
    $passed++
    
    # Wait for all to complete
    Start-Sleep -Seconds 15
    
    $allDone = $true
    foreach ($tid in $concurrentTasks) {
        $check = Invoke-RestMethod -Uri "$coreUrl/task/$tid" -Method GET
        if ($check.state -ne "DONE") {
            $allDone = $false
            Write-Host "    - Task ${tid}: $($check.state)" -ForegroundColor Yellow
        }
    }
    
    if ($allDone) {
        Write-Host "  PASS: All concurrent tasks completed" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Not all tasks completed" -ForegroundColor Red
        $failed++
    }
}

# ========== Summary ==========
$endTime = Get-Date
$duration = $endTime - $startTime

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
Write-Host "Duration: $($duration.TotalSeconds.ToString('F1'))s"

if ($failed -eq 0) {
    Write-Host "`nPASS: All E2E tests passed" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nFAIL: $failed tests failed" -ForegroundColor Red
    exit 1
}
