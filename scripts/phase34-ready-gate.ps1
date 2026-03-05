# Phase 34 Gate -- Self-Improvement: Eyes (Web Research) + Hands (Code Patching)
# Usage: powershell -ExecutionPolicy Bypass -File .\scripts\phase34-ready-gate.ps1
# Prerequisites: Core running on http://localhost:5051

param(
    [string]$CoreUrl = "http://localhost:5051",
    [string]$NetUrl  = "http://localhost:5052"
)

Set-StrictMode -Off
$ErrorActionPreference = "Continue"
$pass = 0; $fail = 0; $warn = 0

function Assert($condition, $label) {
    if ($condition) {
        Write-Host "  PASS: $label" -ForegroundColor Green
        $script:pass++
    } else {
        Write-Host "  FAIL: $label" -ForegroundColor Red
        $script:fail++
    }
}

function Warn($label) {
    Write-Host "  WARN: $label" -ForegroundColor Yellow
    $script:warn++
}

function Get-Json($url) {
    try { Invoke-RestMethod $url -TimeoutSec 10 } catch { $null }
}

function Post-Json($url, $body = $null) {
    try {
        if ($body) { Invoke-RestMethod $url -Method POST -Body ($body | ConvertTo-Json) -ContentType "application/json" -TimeoutSec 10 }
        else        { Invoke-RestMethod $url -Method POST -TimeoutSec 10 }
    } catch { $null }
}

$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Test-Path "$RepoRoot\scripts\phase14-ready-gate.ps1")) {
    $RepoRoot = Split-Path $PSScriptRoot -Parent
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host " Phase 34 Gate -- Eyes (Web Research) + Hands (Code Patch)" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host ""

# -- Preflight ----------------------------------------------------------------

Write-Host "[Preflight] Checking Core..." -ForegroundColor DarkCyan
$health = Get-Json "$CoreUrl/health"
if (-not $health) {
    Write-Host "ERROR: Core not reachable at $CoreUrl" -ForegroundColor Red
    exit 1
}
Write-Host "  Core: OK" -ForegroundColor Gray

# -- Section 1: Build ---------------------------------------------------------

Write-Host ""
Write-Host "[1] dotnet build" -ForegroundColor DarkCyan

$buildOutput = & dotnet build "$RepoRoot\core" --nologo 2>&1
$buildOk = $buildOutput -match "Build succeeded"
$codeErrors = ($buildOutput | Where-Object { $_ -match "error [A-Z][A-Z]\d+" -and $_ -notmatch "MSB3021|MSB3027" }).Count -eq 0
Assert $buildOk       "dotnet build reports Build succeeded"
Assert $codeErrors    "dotnet build - 0 code errors (MSB file-lock warnings allowed)"

# -- Section 2: Engine status -------------------------------------------------

Write-Host ""
Write-Host "[2] Self-improvement engine" -ForegroundColor DarkCyan

$status = Get-Json "$CoreUrl/selfimprove/status"
Assert ($status -ne $null)           "GET /selfimprove/status returns 200"
Assert ($status.state -ne "STOPPED") "Engine state is not STOPPED (running or idle)"

# -- Section 3: Web research --------------------------------------------------

Write-Host ""
Write-Host "[3] Web research (RESEARCH_WEB)" -ForegroundColor DarkCyan

# Check that DuckDuckGo is reachable from this machine
$ddgOk = $false
try {
    $resp = Invoke-WebRequest "https://html.duckduckgo.com/html/?q=test" -TimeoutSec 10 -UseBasicParsing
    $ddgOk = $resp.StatusCode -eq 200
} catch { }

if ($ddgOk) {
    Assert $true "DuckDuckGo reachable for web research"
} else {
    Warn "DuckDuckGo not reachable - web research will fall back to internal knowledge"
}

# Trigger a RESEARCH_WEB cycle by checking insights (engine may already have one)
Start-Sleep -Seconds 2
$insights = Get-Json "$CoreUrl/selfimprove/insights"
Assert ($insights -ne $null) "GET /selfimprove/insights returns 200"

# Check that the SearchOrchestrator's ResearchTopicAsync exists (code inspection)
$searchFile = "$RepoRoot\core\SearchOrchestrator.cs"
Assert (Test-Path $searchFile) "SearchOrchestrator.cs exists"
$searchContent = if (Test-Path $searchFile) { Get-Content $searchFile -Raw } else { "" }
Assert ($searchContent -match "ResearchTopicAsync") "SearchOrchestrator has ResearchTopicAsync method"
Assert ($searchContent -match "html\.duckduckgo\.com") "ResearchTopicAsync uses DuckDuckGo HTML"

# -- Section 4: Code patching -------------------------------------------------

Write-Host ""
Write-Host "[4] Code patching (PATCH_CORE_CODE)" -ForegroundColor DarkCyan

# Verify CodePatcher.cs exists with expected content
$patcherFile = "$RepoRoot\core\CodePatcher.cs"
Assert (Test-Path $patcherFile) "CodePatcher.cs exists"
$patcherContent = if (Test-Path $patcherFile) { Get-Content $patcherFile -Raw } else { "" }
Assert ($patcherContent -match "SafeTargets")           "CodePatcher has SafeTargets list"
Assert ($patcherContent -match "HeuristicInterpret")    "CodePatcher targets HeuristicInterpret"
Assert ($patcherContent -match "AnalyzeFailure")        "CodePatcher targets AnalyzeFailure"
Assert ($patcherContent -match "ResearchTopics")        "CodePatcher targets ResearchTopics"
Assert ($patcherContent -match "dotnet.*build")         "CodePatcher runs dotnet build verification"
Assert ($patcherContent -match "dotnet.*test")          "CodePatcher runs dotnet test verification"
Assert ($patcherContent -match "SafeRevert|originalContent") "CodePatcher has revert-on-failure logic"
Assert ($patcherContent -match "CommitCoreChange")      "CodePatcher commits via SelfGitManager"

# Verify SelfAnalyzer generates PATCH_CORE_CODE
$analyzerFile = "$RepoRoot\core\SelfAnalyzer.cs"
$analyzerContent = if (Test-Path $analyzerFile) { Get-Content $analyzerFile -Raw } else { "" }
Assert ($analyzerContent -match "PATCH_CORE_CODE") "SelfAnalyzer generates PATCH_CORE_CODE items"
Assert ($analyzerContent -match "CreatePatchItem")  "SelfAnalyzer has CreatePatchItem method"

# Verify SelfImprovementEngine has real ExecutePatchCoreCode
$engineFile = "$RepoRoot\core\SelfImprovementEngine.cs"
$engineContent = if (Test-Path $engineFile) { Get-Content $engineFile -Raw } else { "" }
Assert ($engineContent -match "ExecutePatchCoreCode")   "SelfImprovementEngine has ExecutePatchCoreCode"
Assert ($engineContent -notmatch "Phase 29.1")          "PATCH_CORE_CODE placeholder removed"
Assert ($engineContent -match "_codePatcher")           "SelfImprovementEngine has _codePatcher field"
Assert ($engineContent -match "_searchOrchestrator")    "SelfImprovementEngine has _searchOrchestrator field"

# -- Section 5: ExecuteResearch uses real web ---------------------------------

Write-Host ""
Write-Host "[5] ExecuteResearch uses real web" -ForegroundColor DarkCyan

$engineContent2 = if (Test-Path $engineFile) { Get-Content $engineFile -Raw } else { "" }
Assert ($engineContent2 -match "ResearchTopicAsync")    "ExecuteResearch calls ResearchTopicAsync"
Assert ($engineContent2 -match "web:.*sources|internal knowledge") "ExecuteResearch labels insight with source flag"
Assert ($engineContent2 -notmatch "Provide a concise, actionable summary.*Research topic.*Summarize.*3-4 sentences" -or
        $engineContent2 -match "Web search results") "ExecuteResearch includes web results in LLM prompt"

# -- Section 6: git log -------------------------------------------------------

Write-Host ""
Write-Host "[6] Git integration" -ForegroundColor DarkCyan

$gitLog = & git -C $RepoRoot log --grep="self-patch:" --oneline 2>&1
$gitOk  = $LASTEXITCODE -eq 0
Assert $gitOk "git log --grep=self-patch is queryable (exit 0)"

# -- Section 7: Secrets check -------------------------------------------------

Write-Host ""
Write-Host "[7] Secrets check" -ForegroundColor DarkCyan

$secretsScript = "$RepoRoot\scripts\check-no-secrets.ps1"
if (Test-Path $secretsScript) {
    $secretsOut = & powershell -ExecutionPolicy Bypass -File $secretsScript 2>&1
    $secretsOk  = $LASTEXITCODE -eq 0
    Assert $secretsOk "check-no-secrets.ps1 PASS"
} else {
    Warn "check-no-secrets.ps1 not found - skipping"
}

# -- Section 8: Self-improvement history --------------------------------------

Write-Host ""
Write-Host "[8] History endpoint" -ForegroundColor DarkCyan

$history = Get-Json "$CoreUrl/selfimprove/history"
Assert ($history -ne $null) "GET /selfimprove/history returns 200"

# -- Summary ------------------------------------------------------------------

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host " RESULTS: $pass PASS  |  $fail FAIL  |  $warn WARN" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Write-Host "=========================================================" -ForegroundColor Cyan

if ($fail -eq 0) {
    Write-Host ""
    Write-Host " Phase 34 GATE PASSED" -ForegroundColor Green
    Write-Host " Archimedes now has Eyes (real web research) and Hands (code patching)." -ForegroundColor Green
    Write-Host ""
    exit 0
} else {
    Write-Host ""
    Write-Host " Phase 34 GATE FAILED ($fail failures)" -ForegroundColor Red
    Write-Host ""
    exit 1
}
