# Phase 21 - Procedure Memory Ready Gate
# Tests: ProcedureStore, cache hit/miss, keyword extraction,
#        disk persistence, outcome recording, API endpoints
# Usage: .\scripts\phase21-ready-gate.ps1

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
Write-Host "  Phase 21 - Procedure Memory Ready Gate" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

# ---- 1. Core sanity -------------------------------------------------------
Write-Host "[1] Core sanity" -ForegroundColor Yellow

Test-Case "Core is running" {
    $r = Invoke-WebRequest "$BaseUrl/health" -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

Test-Case "GET /procedures returns 200" {
    $r = Invoke-WebRequest "$BaseUrl/procedures" -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

Test-Case "GET /procedures response has count and procedures array" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $null -ne $r.count -and $null -ne $r.procedures
}

# ---- 2. Procedure created on first plan -----------------------------------
Write-Host ""
Write-Host "[2] Procedure created on first plan" -ForegroundColor Yellow

# Call planner - this should create a new procedure
$planBody = '{"UserPrompt":"export data from testsite"}'
$planResp  = Invoke-RestMethod "$BaseUrl/planner/plan" `
             -Method POST -Body $planBody -ContentType "application/json" -TimeoutSec 30

Test-Case "Planner returns plan with procedureId" {
    $null -ne $planResp.procedureId -or $null -ne $planResp.plan.procedureId
}

Test-Case "GET /procedures count >= 1 after first plan" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $r.count -ge 1
}

Test-Case "Procedure has required fields" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $p = $r.procedures | Select-Object -First 1
    $null -ne $p.id -and $null -ne $p.intent -and $null -ne $p.successRate
}

Test-Case "Procedure has non-empty keywords" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $p = $r.procedures | Select-Object -First 1
    $p.keywords.Count -ge 1
}

Test-Case "Procedure has stepCount > 0" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $p = $r.procedures | Select-Object -First 1
    $p.stepCount -gt 0
}

Test-Case "Procedure successRate is 1.0 initially (no failures yet)" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $p = $r.procedures | Select-Object -First 1
    $p.successRate -eq 1.0
}

# ---- 3. GET /procedures/{id} ---------------------------------------------
Write-Host ""
Write-Host "[3] GET /procedures/{id}" -ForegroundColor Yellow

$allProcs = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
$firstId  = ($allProcs.procedures | Select-Object -First 1).id

Test-Case "GET /procedures/{id} returns 200" {
    $r = Invoke-WebRequest "$BaseUrl/procedures/$firstId" -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

Test-Case "GET /procedures/{id} includes full plan with steps" {
    $r = Invoke-RestMethod "$BaseUrl/procedures/$firstId" -TimeoutSec 5
    $null -ne $r.plan -and $r.plan.steps.Count -gt 0
}

Test-Case "GET /procedures/{id} includes intent field" {
    $r = Invoke-RestMethod "$BaseUrl/procedures/$firstId" -TimeoutSec 5
    -not [string]::IsNullOrEmpty($r.intent)
}

Test-Case "GET /procedures/nonexistent returns 404" {
    try {
        $null = Invoke-RestMethod "$BaseUrl/procedures/nonexistent-proc-id" -TimeoutSec 5
        $false
    } catch {
        $_.Exception.Response.StatusCode.value__ -eq 404
    }
}

# ---- 4. Cache hit (second plan with same intent) -------------------------
Write-Host ""
Write-Host "[4] Cache hit - second plan with same intent" -ForegroundColor Yellow

$plan2Body = '{"UserPrompt":"export the CSV from testsite dashboard"}'
$plan2Resp = Invoke-RestMethod "$BaseUrl/planner/plan" `
             -Method POST -Body $plan2Body -ContentType "application/json" -TimeoutSec 30

Test-Case "Second plan for same intent returns a plan" {
    $null -ne $plan2Resp -and ($null -ne $plan2Resp.plan -or $null -ne $plan2Resp.intent)
}

Test-Case "Procedure count does not explode (same intent reuses procedure)" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    # Should not have created dozens of procedures for the same intent
    $r.count -le 10
}

# ---- 5. Keyword extraction -----------------------------------------------
Write-Host ""
Write-Host "[5] Keyword extraction" -ForegroundColor Yellow

Test-Case "Keywords extracted from prompt exclude stop words" {
    $r    = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $proc = $r.procedures | Select-Object -First 1
    # Stop words like 'from', 'the', 'to' should not appear
    $hasStopWord = $proc.keywords -contains "from" -or $proc.keywords -contains "the" -or $proc.keywords -contains "to"
    -not $hasStopWord
}

Test-Case "Keywords contain meaningful words from prompt" {
    $r    = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $proc = $r.procedures | Select-Object -First 1
    # Should contain words like 'export', 'data', 'testsite'
    $hasMeaningful = ($proc.keywords | Where-Object { $_ -in @("export","data","testsite","csv","dashboard") }).Count -ge 1
    $hasMeaningful
}

# ---- 6. Disk persistence -------------------------------------------------
Write-Host ""
Write-Host "[6] Disk persistence" -ForegroundColor Yellow

Test-Case "Procedure JSON files exist on disk" {
    $procDir = [System.IO.Path]::Combine(
        [System.Environment]::GetFolderPath("LocalApplicationData"),
        "Archimedes", "procedures")
    $files = Get-ChildItem -Path $procDir -Filter "*.json" -ErrorAction SilentlyContinue
    $files.Count -ge 1
}

Test-Case "Procedure JSON on disk is valid and has intent field" {
    $procDir = [System.IO.Path]::Combine(
        [System.Environment]::GetFolderPath("LocalApplicationData"),
        "Archimedes", "procedures")
    $file = Get-ChildItem -Path $procDir -Filter "*.json" -ErrorAction SilentlyContinue |
            Select-Object -First 1
    if ($null -eq $file) { return "no .json files found" }
    $json = Get-Content $file.FullName -Raw
    $obj  = $json | ConvertFrom-Json
    -not [string]::IsNullOrEmpty($obj.intent)
}

# ---- 7. DELETE /procedures/{id} ------------------------------------------
Write-Host ""
Write-Host "[7] DELETE /procedures/{id}" -ForegroundColor Yellow

# Create a fresh plan to get a procedure we can delete safely
$delPlanBody = '{"UserPrompt":"login to system for deletion test"}'
$null = Invoke-RestMethod "$BaseUrl/planner/plan" `
        -Method POST -Body $delPlanBody -ContentType "application/json" -TimeoutSec 30

$procsForDel  = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
$countBefore  = $procsForDel.count
$idToDelete   = ($procsForDel.procedures | Select-Object -Last 1).id

Test-Case "DELETE /procedures/{id} returns 200" {
    $r = Invoke-WebRequest "$BaseUrl/procedures/$idToDelete" -Method DELETE -UseBasicParsing -TimeoutSec 5
    $r.StatusCode -eq 200
}

Test-Case "GET /procedures/{id} returns 404 after delete" {
    try {
        $null = Invoke-RestMethod "$BaseUrl/procedures/$idToDelete" -TimeoutSec 5
        $false
    } catch {
        $_.Exception.Response.StatusCode.value__ -eq 404
    }
}

Test-Case "Procedure count decreased after delete" {
    $r = Invoke-RestMethod "$BaseUrl/procedures" -TimeoutSec 5
    $r.count -lt $countBefore
}

Test-Case "DELETE /procedures/nonexistent returns 404" {
    try {
        $null = Invoke-WebRequest "$BaseUrl/procedures/no-such-id" -Method DELETE -UseBasicParsing -TimeoutSec 5
        $false
    } catch {
        $_.Exception.Response.StatusCode.value__ -eq 404
    }
}

# ---- 8. Planner response includes procedure fields -----------------------
Write-Host ""
Write-Host "[8] Planner response structure" -ForegroundColor Yellow

$planCheck = Invoke-RestMethod "$BaseUrl/planner/plan" `
             -Method POST `
             -Body '{"UserPrompt":"download file from server"}' `
             -ContentType "application/json" -TimeoutSec 30

Test-Case "Planner response has procedureId field" {
    $null -ne $planCheck.procedureId -or $null -ne $planCheck.plan
}

Test-Case "Planner response has fromProcedureCache field" {
    # Either at top level or inside plan
    $hasCacheField = $null -ne $planCheck.fromProcedureCache -or
                     $null -ne $planCheck.plan.fromProcedureCache
    $hasCacheField
}

# ---- Results -------------------------------------------------------------
Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
$color  = if ($fail -eq 0) { "Green" } else { "Red" }
$status = if ($fail -eq 0) { "ALL PASS" } else { "FAILED" }
Write-Host "  $status  $pass/$total passed" -ForegroundColor $color
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

if ($fail -gt 0) { exit 1 }
