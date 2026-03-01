# Phase 19 - Observability Ready Gate
# Tests: CorrelationId, Trace API, Typed Failures, Persistence, End-to-End
# Usage: .\scripts\phase19-ready-gate.ps1

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
Write-Host "  Phase 19 - Observability Ready Gate" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

# ── 1. Core health ────────────────────────────────────────────────────────────
Write-Host "[1] Core sanity" -ForegroundColor Yellow

Test-Case "Core is running" {
    $r = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

# ── 2. Correlation ID ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[2] Correlation ID" -ForegroundColor Yellow

Test-Case "Response contains X-Correlation-Id header" {
    $r = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5
    $r.Headers.ContainsKey("X-Correlation-Id")
}

Test-Case "Three requests produce three unique IDs" {
    $ids = 1..3 | ForEach-Object {
        $r = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5
        $r.Headers["X-Correlation-Id"]
    }
    ($ids | Select-Object -Unique).Count -eq 3
}

Test-Case "Custom X-Correlation-Id header is respected" {
    $customId = "test-custom-abc123"
    $r = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5 `
         -Headers @{ "X-Correlation-Id" = $customId }
    $r.Headers["X-Correlation-Id"] -eq $customId
}

# ── 3. Trace API - list ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "[3] Trace API - list" -ForegroundColor Yellow

Test-Case "GET /traces returns 200" {
    $r = Invoke-WebRequest "$BaseUrl/traces" -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

Test-Case "GET /traces response has count and traces array" {
    $r   = Invoke-RestMethod "$BaseUrl/traces" -TimeoutSec 5
    $null -ne $r.count -and $null -ne $r.traces
}

Test-Case "At least 1 trace recorded (from earlier health calls)" {
    $r = Invoke-RestMethod "$BaseUrl/traces" -TimeoutSec 5
    $r.count -ge 1
}

# ── 4. Trace API - by correlationId ──────────────────────────────────────────
Write-Host ""
Write-Host "[4] Trace API - by correlationId" -ForegroundColor Yellow

Test-Case "GET /traces/{id} returns full trace for known ID" {
    # Make a request with a known ID
    $knownId = "gate-test-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5 `
            -Headers @{ "X-Correlation-Id" = $knownId }

    Start-Sleep -Milliseconds 300  # let async persist complete

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $trace.correlationId -eq $knownId
}

Test-Case "GET /traces/{id} response has endpoint and method fields" {
    $knownId = "gate-fields-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $null -ne $trace.endpoint -and $null -ne $trace.method
}

Test-Case "GET /traces/{id} has startedAtUtc and totalDurationMs" {
    $knownId = "gate-timing-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $null -ne $trace.startedAtUtc -and $trace.totalDurationMs -ge 0
}

Test-Case "GET /traces/nonexistent returns 404" {
    try {
        $null = Invoke-RestMethod "$BaseUrl/traces/this-id-does-not-exist-xyz" -TimeoutSec 5
        $false  # should have thrown
    } catch {
        $_.Exception.Response.StatusCode.value__ -eq 404
    }
}

# ── 5. Step-level trace - LLM interpret ───────────────────────────────────────
Write-Host ""
Write-Host "[5] Step-level trace - LLM.Interpret" -ForegroundColor Yellow

Test-Case "LLM interpret trace has LLM.Interpret step" {
    $knownId = "gate-llm-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/llm/interpret" `
            -Method POST -Body "open YouTube and play music" `
            -ContentType "text/plain" -UseBasicParsing -TimeoutSec 30 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $trace.steps | Where-Object { $_.name -eq "LLM.Interpret" } | Measure-Object | ForEach-Object { $_.Count -ge 1 }
}

Test-Case "LLM interpret step has durationMs > 0" {
    $knownId = "gate-llmdur-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/llm/interpret" `
            -Method POST -Body "search for cat videos" `
            -ContentType "text/plain" -UseBasicParsing -TimeoutSec 30 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $step  = $trace.steps | Where-Object { $_.name -eq "LLM.Interpret" } | Select-Object -First 1
    $step.durationMs -gt 0
}

Test-Case "LLM interpret step has failureCode field" {
    $knownId = "gate-llmcode-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/llm/interpret" `
            -Method POST -Body "do something vague" `
            -ContentType "text/plain" -UseBasicParsing -TimeoutSec 30 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $step  = $trace.steps | Where-Object { $_.name -eq "LLM.Interpret" } | Select-Object -First 1
    $null -ne $step.failureCode
}

# ── 6. Persistence - disk ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "[6] Persistence" -ForegroundColor Yellow

Test-Case "Trace is persisted to disk as JSON" {
    $knownId = "gate-disk-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 500  # let async persist complete

    $traceDir = Join-Path $env:LOCALAPPDATA "Archimedes\traces"
    $filePath = Join-Path $traceDir "$knownId.json"
    Test-Path $filePath
}

Test-Case "Disk trace JSON is valid and contains correlationId" {
    $knownId = "gate-diskjson-" + (Get-Random)
    $null = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 500

    $traceDir = Join-Path $env:LOCALAPPDATA "Archimedes\traces"
    $filePath = Join-Path $traceDir "$knownId.json"
    if (-not (Test-Path $filePath)) { return "File not found" }

    $json  = Get-Content $filePath -Raw | ConvertFrom-Json
    $json.correlationId -eq $knownId
}

# ── 7. End-to-end - planner trace ─────────────────────────────────────────────
Write-Host ""
Write-Host "[7] End-to-end - Planner trace" -ForegroundColor Yellow

Test-Case "Planner plan trace has Planner.Plan step" {
    $knownId = "gate-plan-" + (Get-Random)
    $body    = '{"UserPrompt":"export data from testsite"}'
    $null = Invoke-WebRequest "$BaseUrl/planner/plan" `
            -Method POST -Body $body `
            -ContentType "application/json" -UseBasicParsing -TimeoutSec 30 `
            -Headers @{ "X-Correlation-Id" = $knownId }
    Start-Sleep -Milliseconds 300

    $trace = Invoke-RestMethod "$BaseUrl/traces/$knownId" -TimeoutSec 5
    $trace.steps | Where-Object { $_.name -eq "Planner.Plan" } | Measure-Object | ForEach-Object { $_.Count -ge 1 }
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
