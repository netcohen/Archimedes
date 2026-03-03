# phase26-ready-gate.ps1
# Gate: Phase 26 - Goal Layer + Adaptive Planner

$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }
$passed  = 0
$failed  = 0

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Phase 26 - Goal Layer + Adaptive Planner" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

function Pass($msg) { Write-Host "  PASS  $msg" -ForegroundColor Green; $global:passed++ }
function Fail($msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red;   $global:failed++ }
function Info($msg) { Write-Host "  INFO  $msg" -ForegroundColor Gray }

# [1] build
Write-Host "`n[1] dotnet build" -ForegroundColor Yellow
try {
    $build = & dotnet build "$(Split-Path $PSScriptRoot)/core" 2>&1 | Out-String
    if ($build -match "0 Error") { Pass "dotnet build 0 errors" }
    else                          { Fail "dotnet build has errors" }
} catch { Fail "dotnet build threw: $_" }

# [2] core sanity
Write-Host "`n[2] Core sanity" -ForegroundColor Yellow
try {
    $h = Invoke-RestMethod -Uri "$coreUrl/health" -TimeoutSec 10
    if ($h -match "OK") { Pass "Core is running" } else { Fail "Unexpected health: $h" }
} catch { Fail "Core unreachable: $_" }

# [3] POST /goals - create goal
Write-Host "`n[3] POST /goals - create goal" -ForegroundColor Yellow
$goalId = $null
try {
    $body = '{"userPrompt":"export testsite data to CSV","title":"Gate test goal","type":"ONE_TIME","maxRetries":1}'
    $g = Invoke-RestMethod -Uri "$coreUrl/goals" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 60
    if ($g.goalId) {
        $goalId = $g.goalId
        Pass "Goal created goalId=$($g.goalId)"
    } else { Fail "No goalId returned" }
    if ($g.state) { Pass "Has state=$($g.state)" } else { Fail "Missing state" }
    if ($g.intent) { Pass "Has intent=$($g.intent)" } else { Fail "Missing intent" }
    Info "intent=$($g.intent) type=$($g.type)"
} catch { Fail "POST /goals failed: $_" }

# [4] GET /goals - list
Write-Host "`n[4] GET /goals - list" -ForegroundColor Yellow
try {
    $list = Invoke-RestMethod -Uri "$coreUrl/goals" -TimeoutSec 10
    if ($null -ne $list.count) { Pass "Has count=$($list.count)" } else { Fail "Missing count" }
    if ($list.count -ge 1)     { Pass "At least 1 goal in list" }  else { Fail "List empty" }
} catch { Fail "GET /goals failed: $_" }

# [5] GET /goals/{id} - detail
Write-Host "`n[5] GET /goals/{id} - detail" -ForegroundColor Yellow
try {
    if ($null -eq $goalId) { Fail "No goalId from step 3"; }
    else {
        $detail = Invoke-RestMethod -Uri "$coreUrl/goals/$goalId" -TimeoutSec 10
        if ($detail.goalId -eq $goalId) { Pass "goalId matches" }           else { Fail "goalId mismatch" }
        if ($null -ne $detail.state)    { Pass "Has state=$($detail.state)" } else { Fail "Missing state" }
        if ($null -ne $detail.progress) { Pass "Has progress=$($detail.progress)" } else { Fail "Missing progress" }
        if ($null -ne $detail.taskIds)  { Pass "Has taskIds (count=$($detail.taskIds.Count))" } else { Fail "Missing taskIds" }
        Info "state=$($detail.state) intent=$($detail.intent) taskCount=$($detail.taskIds.Count)"
    }
} catch { Fail "GET /goals/{id} failed: $_" }

# [6] GET /goals/{id}/tasks - tasks linked to goal
Write-Host "`n[6] GET /goals/{id}/tasks" -ForegroundColor Yellow
try {
    if ($null -eq $goalId) { Fail "No goalId from step 3" }
    else {
        $tasks = Invoke-RestMethod -Uri "$coreUrl/goals/$goalId/tasks" -TimeoutSec 10
        if ($null -ne $tasks.count) { Pass "Has count=$($tasks.count)" } else { Fail "Missing count" }
        Pass "GET /goals/{id}/tasks returned ok"
        Info "tasks linked to goal: $($tasks.count)"
    }
} catch { Fail "GET /goals/{id}/tasks failed: $_" }

# [7] POST /goals/{id}/evaluate - force evaluation
Write-Host "`n[7] POST /goals/{id}/evaluate" -ForegroundColor Yellow
try {
    if ($null -eq $goalId) { Fail "No goalId from step 3" }
    else {
        $ev = Invoke-RestMethod -Uri "$coreUrl/goals/$goalId/evaluate" -Method Post -TimeoutSec 10
        if ($null -ne $ev.isAchieved) { Pass "Has isAchieved=$($ev.isAchieved)" } else { Fail "Missing isAchieved" }
        if ($ev.reason)               { Pass "Has reason=$($ev.reason)" }           else { Fail "Missing reason" }
        if ($ev.nextAction)           { Pass "Has nextAction=$($ev.nextAction)" }   else { Fail "Missing nextAction" }
        Info "isAchieved=$($ev.isAchieved) reason=$($ev.reason) nextAction=$($ev.nextAction)"
    }
} catch { Fail "POST /goals/{id}/evaluate failed: $_" }

# [8] POST /goals/{id}/pause - pause goal
Write-Host "`n[8] POST /goals/{id}/pause" -ForegroundColor Yellow
$pauseGoalId = $null
try {
    # Create a PERSISTENT goal to test pause/resume (ONE_TIME may complete fast)
    $pb = '{"userPrompt":"monitor testsite dashboard","title":"Pause test goal","type":"PERSISTENT","checkIntervalMinutes":60}'
    $pg = Invoke-RestMethod -Uri "$coreUrl/goals" -Method Post -Body $pb -ContentType "application/json" -TimeoutSec 60
    $pauseGoalId = $pg.goalId

    $paused = Invoke-RestMethod -Uri "$coreUrl/goals/$pauseGoalId/pause" -Method Post -TimeoutSec 10
    if ($paused.state -eq "IDLE") { Pass "Pause: state=IDLE" } else { Fail "Pause: state=$($paused.state) (expected IDLE)" }
} catch { Fail "Pause test failed: $_" }

# [9] POST /goals/{id}/resume - resume goal
Write-Host "`n[9] POST /goals/{id}/resume" -ForegroundColor Yellow
try {
    if ($null -eq $pauseGoalId) { Fail "No pauseGoalId from step 8" }
    else {
        $resumed = Invoke-RestMethod -Uri "$coreUrl/goals/$pauseGoalId/resume" -Method Post -TimeoutSec 10
        if ($resumed.state -eq "ACTIVE") { Pass "Resume: state=ACTIVE" } else { Fail "Resume: state=$($resumed.state) (expected ACTIVE)" }
    }
} catch { Fail "Resume test failed: $_" }

# [10] Adaptive replan: goal survives a task failure (not immediately FAILED)
Write-Host "`n[10] Adaptive replan: goal survives task failure" -ForegroundColor Yellow
try {
    # Create ONE_TIME goal with maxRetries=2 - it will try alternatives
    $rb = '{"userPrompt":"export testsite data to CSV","title":"Adaptive test","type":"ONE_TIME","maxRetries":2}'
    $rg = Invoke-RestMethod -Uri "$coreUrl/goals" -Method Post -Body $rb -ContentType "application/json" -TimeoutSec 60
    $rid = $rg.goalId
    Start-Sleep -Seconds 3
    $rdet = Invoke-RestMethod -Uri "$coreUrl/goals/$rid" -TimeoutSec 10
    # Goal should NOT be in FAILED state immediately (adaptive replan gives it chances)
    if ($rdet.state -ne "FAILED" -or $rdet.retryCount -gt 0) {
        Pass "Goal survived first tick (state=$($rdet.state) retries=$($rdet.retryCount))"
    } else {
        Fail "Goal went to FAILED too fast without retrying"
    }
} catch { Fail "Adaptive replan test failed: $_" }

# [11] DELETE /goals/{id} - cancel and remove
Write-Host "`n[11] DELETE /goals/{id}" -ForegroundColor Yellow
try {
    if ($null -eq $goalId) { Fail "No goalId from step 3" }
    else {
        $del = Invoke-RestMethod -Uri "$coreUrl/goals/$goalId" -Method Delete -TimeoutSec 10
        if ($del.ok -eq $true) { Pass "Delete returned ok=true" } else { Fail "Delete ok=$($del.ok)" }
        # Verify it's gone
        try {
            Invoke-RestMethod -Uri "$coreUrl/goals/$goalId" -TimeoutSec 5 | Out-Null
            Fail "Goal still exists after delete"
        } catch { Pass "Goal not found after delete (expected 404)" }
    }
} catch { Fail "DELETE /goals/{id} failed: $_" }

# [12] Regression Phase 25
Write-Host "`n[12] Regression Phase 25" -ForegroundColor Yellow
try {
    $st = Invoke-RestMethod -Uri "$coreUrl/availability/status" -TimeoutSec 10
    if ($null -ne $st.isAvailable) { Pass "Phase 25: /availability/status ok" }
    else                            { Fail "Phase 25: /availability/status bad" }

    $resp = Invoke-WebRequest -Uri "$coreUrl/health" -UseBasicParsing
    if ($resp.Headers["X-Correlation-Id"]) { Pass "Phase 19: X-Correlation-Id present" }
    else                                    { Fail "Phase 19: X-Correlation-Id missing" }

    $ch = Invoke-WebRequest -Uri "$coreUrl/chat" -UseBasicParsing
    if ($ch.Content -match "v0\.26\.0") { Pass "Chat version v0.26.0" }
    else                                { Fail "Chat version not v0.26.0" }
} catch { Fail "Regression check failed: $_" }

# Summary
Write-Host "`n=====================================================" -ForegroundColor Cyan
$total = $passed + $failed
if ($failed -eq 0) {
    Write-Host "  ALL PASS  $passed/$total passed" -ForegroundColor Green
    exit 0
} else {
    Write-Host "  FAIL  $passed/$total passed  ($failed failed)" -ForegroundColor Red
    exit 1
}
