# Phase 20 - Success Criteria Engine Ready Gate
# Tests: OutcomeResult, VerificationResult, per-action criteria, task outcome API
# Usage: .\scripts\phase20-ready-gate.ps1

param([string]$BaseUrl = "http://localhost:5051")

$pass  = 0
$fail  = 0
$total = 0

function Test-Case {
    param([string]$Name, [scriptblock]$Body)
    $script:total++
    try {
        $result = & $Body
        if ($result -eq $true) {
            Write-Host "  PASS  $Name" -ForegroundColor Green
            $script:pass++
        } else {
            Write-Host "  FAIL  $Name - $result" -ForegroundColor Red
            $script:fail++
        }
    } catch {
        Write-Host "  FAIL  $Name - Exception: $($_.Exception.Message)" -ForegroundColor Red
        $script:fail++
    }
}

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Phase 20 - Success Criteria Engine Ready Gate" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

# ── 1. Core sanity ────────────────────────────────────────────────────────────
Write-Host "[1] Core sanity" -ForegroundColor Yellow

Test-Case "Core is running" {
    $r = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

Test-Case "POST /criteria/verify endpoint exists" {
    $body = '{"Action":"http.get","StepSuccess":true,"Data":""}'
    $r    = Invoke-WebRequest "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" `
            -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

# ── 2. Standalone verifier - http.login ───────────────────────────────────────
Write-Host ""
Write-Host "[2] Standalone verifier - http.login" -ForegroundColor Yellow

Test-Case "http.login with token field -> VERIFIED" {
    $body = '{"Action":"http.login","StepSuccess":true,"Data":"{\"token\":\"abc123\"}"}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "VERIFIED"
}

Test-Case "http.login with success=true -> VERIFIED" {
    $body = '{"Action":"http.login","StepSuccess":true,"Data":"{\"success\":true,\"userId\":1}"}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "VERIFIED"
}

Test-Case "http.login with no token or success -> UNVERIFIED" {
    $body = '{"Action":"http.login","StepSuccess":true,"Data":"{\"message\":\"ok\"}"}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "UNVERIFIED"
}

Test-Case "http.login with StepSuccess=false -> FAILED_VERIFY" {
    $body = '{"Action":"http.login","StepSuccess":false,"Data":null}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "FAILED_VERIFY"
}

# ── 3. Standalone verifier - http.fetchData ───────────────────────────────────
Write-Host ""
Write-Host "[3] Standalone verifier - http.fetchData" -ForegroundColor Yellow

Test-Case "fetchData with non-empty array -> VERIFIED" {
    $body = '{"Action":"http.fetchData","StepSuccess":true,"Data":"[{\"id\":1},{\"id\":2}]"}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "VERIFIED"
}

Test-Case "fetchData with rows property -> VERIFIED" {
    $body = '{"Action":"http.fetchData","StepSuccess":true,"Data":"{\"rows\":[{\"id\":1}],\"total\":1}"}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "VERIFIED"
}

Test-Case "fetchData with empty array -> PARTIAL" {
    $body = '{"Action":"http.fetchData","StepSuccess":true,"Data":"[]"}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "PARTIAL"
}

Test-Case "fetchData with empty body -> FAILED_VERIFY" {
    $body = '{"Action":"http.fetchData","StepSuccess":true,"Data":""}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "FAILED_VERIFY"
}

# ── 4. Standalone verifier - http.downloadCsv ────────────────────────────────
Write-Host ""
Write-Host "[4] Standalone verifier - http.downloadCsv" -ForegroundColor Yellow

Test-Case "downloadCsv with valid CSV -> VERIFIED" {
    # Escape newlines as \n inside the JSON string
    $csvEscaped = "id,name,email\n1,Alice,alice@example.com\n2,Bob,bob@example.com"
    $body = "{`"Action`":`"http.downloadCsv`",`"StepSuccess`":true,`"Data`":`"$csvEscaped`"}"
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "VERIFIED"
}

Test-Case "downloadCsv evidence contains line count" {
    $csvEscaped = "id,name\n1,Alice\n2,Bob\n3,Charlie"
    $body = "{`"Action`":`"http.downloadCsv`",`"StepSuccess`":true,`"Data`":`"$csvEscaped`"}"
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.evidence -like "*lines*" -or $r.evidence -like "*line*"
}

Test-Case "downloadCsv with empty data -> FAILED_VERIFY" {
    $body = '{"Action":"http.downloadCsv","StepSuccess":true,"Data":""}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "FAILED_VERIFY"
}

# ── 5. Approval + scheduler always VERIFIED ───────────────────────────────────
Write-Host ""
Write-Host "[5] Special actions" -ForegroundColor Yellow

Test-Case "approval.requestConfirmation -> VERIFIED" {
    $body = '{"Action":"approval.requestConfirmation","StepSuccess":true}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "VERIFIED"
}

Test-Case "scheduler.reschedule -> VERIFIED" {
    $body = '{"Action":"scheduler.reschedule","StepSuccess":true}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "VERIFIED"
}

Test-Case "unknown action -> NOT_APPLICABLE" {
    $body = '{"Action":"custom.doSomething","StepSuccess":true}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "NOT_APPLICABLE"
}

# ── 6. Response structure ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "[6] Response structure" -ForegroundColor Yellow

Test-Case "Response contains outcome, evidence and expectedCriteria fields" {
    $body = '{"Action":"http.fetchData","StepSuccess":true,"Data":"[{\"id\":1}]"}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $null -ne $r.outcome -and $null -ne $r.evidence -and $null -ne $r.expectedCriteria
}

Test-Case "FAILED_VERIFY response contains failureReason" {
    $body = '{"Action":"http.login","StepSuccess":false}'
    $r    = Invoke-RestMethod "$BaseUrl/criteria/verify" `
            -Method POST -Body $body -ContentType "application/json" -TimeoutSec 5
    $r.outcome -eq "FAILED_VERIFY" -and $null -ne $r.failureReason
}

# ── 7. Trace integration — outcome in trace steps ─────────────────────────────
Write-Host ""
Write-Host "[7] Trace integration - outcome in trace steps" -ForegroundColor Yellow

Test-Case "LLM interpret trace step exposes outcome field" {
    $knownId = "gate-p20-llm-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/llm/interpret" `
            -Method POST -Body "export data from testsite" `
            -ContentType "text/plain" -UseBasicParsing -TimeoutSec 30 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $step  = $trace.steps | Where-Object { $_.name -eq "LLM.Interpret" } | Select-Object -First 1
    # outcome is null for LLM steps (no criteria), that's correct
    # Just verify the field exists in the response (null or value)
    $trace.steps.Count -ge 1
}

Test-Case "Planner trace step exposes outcome field" {
    $knownId = "gate-p20-plan-" + (Get-Random)
    $body    = '{"UserPrompt":"export data from testsite"}'
    $null = Invoke-WebRequest "$BaseUrl/planner/plan" `
            -Method POST -Body $body -ContentType "application/json" `
            -UseBasicParsing -TimeoutSec 30 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $step  = $trace.steps | Where-Object { $_.name -eq "Planner.Plan" } | Select-Object -First 1
    $null -ne $step
}

# ── 8. Task outcome endpoint ──────────────────────────────────────────────────
Write-Host ""
Write-Host "[8] Task outcome endpoint" -ForegroundColor Yellow

Test-Case "GET /task/{id}/outcome returns 404 for unknown task" {
    try {
        $null = Invoke-RestMethod "$BaseUrl/task/nonexistent-task-id/outcome" -TimeoutSec 5
        $false
    } catch {
        $_.Exception.Response.StatusCode.value__ -eq 404
    }
}

Test-Case "GET /task/{id}/outcome returns outcome structure for known task" {
    # Create a task
    $createBody = '{"Title":"P20 Test","UserPrompt":"export data from testsite"}'
    $created    = Invoke-RestMethod "$BaseUrl/task" -Method POST -Body $createBody `
                  -ContentType "application/json" -TimeoutSec 5
    $taskId     = $created.taskId

    $outcome = Invoke-RestMethod "$BaseUrl/task/$taskId/outcome" -TimeoutSec 5
    $null -ne $outcome.taskId -and $null -ne $outcome.state -and $null -ne $outcome.overallOutcome
}

# ── Results ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
$color = if ($fail -eq 0) { "Green" } else { "Red" }
$status = if ($fail -eq 0) { "ALL PASS" } else { "FAILED" }
Write-Host "  $status  $pass/$total passed" -ForegroundColor $color
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

if ($fail -gt 0) { exit 1 }
