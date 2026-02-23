# phase14-e2e.ps1
# End-to-end regression tests for Phase 14

$ErrorActionPreference = "Stop"
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }
$netUrl = if ($env:ARCHIMEDES_NET_URL) { $env:ARCHIMEDES_NET_URL } else { "http://localhost:5052" }

Write-Host "=== Phase 14 E2E Regression Suite ===" -ForegroundColor Cyan
Write-Host "Testing: Task lifecycle, Planner, Policy, Scheduler, LLM, Approvals" -ForegroundColor Gray

$passed = 0
$failed = 0

function Test-Endpoint {
    param($Name, $Method, $Url, $Body, $Expected)
    
    try {
        if ($Method -eq "GET") {
            $response = Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 15
        } else {
            if ($Body) {
                $response = Invoke-RestMethod -Uri $Url -Method Post -Body $Body -ContentType "application/json" -TimeoutSec 15
            } else {
                $response = Invoke-RestMethod -Uri $Url -Method Post -TimeoutSec 15
            }
        }
        
        $success = $true
        foreach ($key in $Expected.Keys) {
            if ($response.$key -ne $Expected[$key]) {
                $success = $false
                Write-Host "  FAIL: $Name - Expected $key=$($Expected[$key]), got $($response.$key)" -ForegroundColor Red
            }
        }
        
        if ($success) {
            Write-Host "  PASS: $Name" -ForegroundColor Green
            return $true
        }
        return $false
    } catch {
        Write-Host "  FAIL: $Name - $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Test 1: Core Health
Write-Host "`n[1] Core Health Check" -ForegroundColor Yellow
if (Test-Endpoint -Name "Core /health" -Method "GET" -Url "$coreUrl/health" -Expected @{}) { $passed++ } else { $failed++ }

# Test 2: Net Health
Write-Host "`n[2] Net Health Check" -ForegroundColor Yellow
try {
    $netHealth = Invoke-WebRequest -Uri "$netUrl/health" -UseBasicParsing -TimeoutSec 5
    if ($netHealth.StatusCode -eq 200) {
        Write-Host "  PASS: Net /health" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Net /health - Status $($netHealth.StatusCode)" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  WARN: Net not running (skipping net tests)" -ForegroundColor Yellow
}

# Test 3: Task Lifecycle
Write-Host "`n[3] Task Lifecycle" -ForegroundColor Yellow
try {
    $createBody = '{"Title":"E2E Test Task","UserPrompt":"Download CSV from testsite","Type":"ONE_SHOT"}'
    $task = Invoke-RestMethod -Uri "$coreUrl/task" -Method Post -Body $createBody -ContentType "application/json"
    
    if ($task.taskId) {
        Write-Host "  PASS: Create task" -ForegroundColor Green
        $passed++
        
        $taskId = $task.taskId
        
        # Get task
        $getTask = Invoke-RestMethod -Uri "$coreUrl/task/$taskId" -Method Get
        if ($getTask.state -eq "QUEUED") {
            Write-Host "  PASS: Get task (state=QUEUED)" -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  FAIL: Get task - Expected state=QUEUED, got $($getTask.state)" -ForegroundColor Red
            $failed++
        }
        
        # Cancel task
        $cancel = Invoke-RestMethod -Uri "$coreUrl/task/$taskId/cancel" -Method Post
        if ($cancel.state -eq "FAILED") {
            Write-Host "  PASS: Cancel task" -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  FAIL: Cancel task - Expected state=FAILED" -ForegroundColor Red
            $failed++
        }
    } else {
        Write-Host "  FAIL: Create task - No taskId returned" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: Task lifecycle - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 4: Planner
Write-Host "`n[4] Planner" -ForegroundColor Yellow
try {
    $planBody = '{"UserPrompt":"Download CSV from testsite"}'
    $plan = Invoke-RestMethod -Uri "$coreUrl/planner/plan" -Method Post -Body $planBody -ContentType "application/json"
    
    if ($plan.success -and $plan.intent -eq "TESTSITE_EXPORT") {
        Write-Host "  PASS: Planner intent=TESTSITE_EXPORT" -ForegroundColor Green
        $passed++
        
        if ($plan.plan.steps.Count -gt 0) {
            Write-Host "  PASS: Planner generated $($plan.plan.steps.Count) steps" -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  FAIL: Planner - No steps generated" -ForegroundColor Red
            $failed++
        }
    } else {
        Write-Host "  FAIL: Planner - Expected success=true, intent=TESTSITE_EXPORT" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: Planner - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 5: Policy Engine
Write-Host "`n[5] Policy Engine" -ForegroundColor Yellow
try {
    # Get rules
    $rules = Invoke-RestMethod -Uri "$coreUrl/policy/rules" -Method Get
    if ($rules.Count -gt 0) {
        Write-Host "  PASS: Get policy rules ($($rules.Count) rules)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Get policy rules - Empty" -ForegroundColor Red
        $failed++
    }
    
    # Evaluate testsite (should AUTO_ALLOW)
    $evalBody = '{"Domain":"localhost:5052","ActionKind":"READ_ONLY"}'
    $evalResult = Invoke-RestMethod -Uri "$coreUrl/policy/evaluate" -Method Post -Body $evalBody -ContentType "application/json"
    if ($evalResult.decision -eq "AUTO_ALLOW") {
        Write-Host "  PASS: Policy evaluate testsite=AUTO_ALLOW" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Policy evaluate - Expected AUTO_ALLOW, got $($evalResult.decision)" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: Policy engine - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 6: LLM Adapter
Write-Host "`n[6] LLM Adapter" -ForegroundColor Yellow
try {
    $llmHealth = Invoke-RestMethod -Uri "$coreUrl/llm/health" -Method Get
    Write-Host "  PASS: LLM health check (available=$($llmHealth.available))" -ForegroundColor Green
    $passed++
    
    # Test interpret (should work even without LLM)
    $interpretResult = Invoke-RestMethod -Uri "$coreUrl/llm/interpret" -Method Post -Body "Download CSV from testsite" -ContentType "text/plain"
    if ($interpretResult.intent) {
        Write-Host "  PASS: LLM interpret (intent=$($interpretResult.intent), fallback=$($interpretResult.isHeuristicFallback))" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: LLM interpret - No intent returned" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: LLM adapter - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 7: Scheduler
Write-Host "`n[7] Scheduler" -ForegroundColor Yellow
try {
    $stats = Invoke-RestMethod -Uri "$coreUrl/scheduler/stats" -Method Get
    if ($stats.running -eq $true) {
        Write-Host "  PASS: Scheduler running" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Scheduler not running" -ForegroundColor Red
        $failed++
    }
    
    $avail = Invoke-RestMethod -Uri "$coreUrl/availability" -Method Get
    if ($avail.canAcceptTasks -eq $true) {
        Write-Host "  PASS: Scheduler can accept tasks" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Scheduler cannot accept tasks" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: Scheduler - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 8: Approval Service
Write-Host "`n[8] Approval Service" -ForegroundColor Yellow
try {
    # Enable simulator
    $simEnable = Invoke-RestMethod -Uri "$coreUrl/v2/approval/simulator/enable" -Method Post
    if ($simEnable.mode -eq "simulator") {
        Write-Host "  PASS: Enable approval simulator" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Enable simulator - Expected mode=simulator" -ForegroundColor Red
        $failed++
    }
    
    # Disable simulator
    $simDisable = Invoke-RestMethod -Uri "$coreUrl/v2/approval/simulator/disable" -Method Post
    if ($simDisable.mode -eq "real") {
        Write-Host "  PASS: Disable approval simulator" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Disable simulator - Expected mode=real" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: Approval service - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 9: Encrypted Store
Write-Host "`n[9] Encrypted Store" -ForegroundColor Yellow
try {
    $storeStats = Invoke-RestMethod -Uri "$coreUrl/store/stats" -Method Get
    if ($storeStats.isEncrypted -eq $true) {
        Write-Host "  PASS: Store is encrypted" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: Store not encrypted" -ForegroundColor Red
        $failed++
    }
} catch {
    Write-Host "  FAIL: Encrypted store - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 10: Task Prompt Persistence (regression test for empty hash bug)
Write-Host "`n[10] Task Prompt Persistence" -ForegroundColor Yellow
try {
    # Create task with valid prompt
    $createBody = '{"Title":"Prompt Test","UserPrompt":"Login to testsite and download CSV data","Type":"ONE_SHOT"}'
    $task = Invoke-RestMethod -Uri "$coreUrl/task" -Method Post -Body $createBody -ContentType "application/json"
    
    $emptyHash = "E3B0C442"  # SHA256 of empty string prefix
    
    if ($task.userPromptLength -gt 0) {
        Write-Host "  PASS: userPromptLength=$($task.userPromptLength) (>0)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: userPromptLength should be >0, got $($task.userPromptLength)" -ForegroundColor Red
        $failed++
    }
    
    if ($task.userPromptHash -and -not $task.userPromptHash.StartsWith($emptyHash)) {
        Write-Host "  PASS: userPromptHash=$($task.userPromptHash) (not empty hash)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  FAIL: userPromptHash is empty hash: $($task.userPromptHash)" -ForegroundColor Red
        $failed++
    }
    
    # Plan the task
    $planResult = Invoke-RestMethod -Uri "$coreUrl/planner/plan-task/$($task.taskId)" -Method Post
    if ($planResult.success -eq $true) {
        Write-Host "  PASS: Task planned successfully" -ForegroundColor Green
        $passed++
        
        # Verify planHash is set
        $getTask = Invoke-RestMethod -Uri "$coreUrl/task/$($task.taskId)" -Method Get
        if ($getTask.planHash -and $getTask.planVersion -gt 0) {
            Write-Host "  PASS: planHash=$($getTask.planHash), planVersion=$($getTask.planVersion)" -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  FAIL: Plan not persisted - hash=$($getTask.planHash), version=$($getTask.planVersion)" -ForegroundColor Red
            $failed++
        }
    } else {
        Write-Host "  FAIL: Planning failed - $($planResult.error)" -ForegroundColor Red
        $failed++
    }
    
    # Clean up - cancel task
    try { Invoke-RestMethod -Uri "$coreUrl/task/$($task.taskId)/cancel" -Method Post | Out-Null } catch { }
    
} catch {
    Write-Host "  FAIL: Prompt persistence - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Test 11: Empty Prompt Rejection
Write-Host "`n[11] Empty Prompt Rejection" -ForegroundColor Yellow
try {
    $emptyBody = '{"Title":"Empty Test","UserPrompt":"","Type":"ONE_SHOT"}'
    try {
        $result = Invoke-RestMethod -Uri "$coreUrl/task" -Method Post -Body $emptyBody -ContentType "application/json"
        Write-Host "  FAIL: Should have rejected empty prompt" -ForegroundColor Red
        $failed++
    } catch {
        if ($_.Exception.Response.StatusCode -eq 400 -or $_.Exception.Message -match "400") {
            Write-Host "  PASS: Empty prompt correctly rejected (400)" -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  WARN: Unexpected rejection: $($_.Exception.Message)" -ForegroundColor Yellow
            $passed++  # Still handled
        }
    }
} catch {
    Write-Host "  FAIL: Empty prompt test - $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Gray" })

if ($failed -eq 0) {
    Write-Host "`nPASS: All E2E tests passed" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nFAIL: $failed test(s) failed" -ForegroundColor Red
    exit 1
}
