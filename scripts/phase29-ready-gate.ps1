# Phase 29 — App Self-Development / Autonomous Self-Improvement Gate
# Usage: .\scripts\phase29-ready-gate.ps1
# Expected: All assertions PASS

param([string]$BaseUrl = "http://localhost:5051")

$pass = 0; $fail = 0
$coreDir = "$PSScriptRoot\..\core"

function Assert([bool]$ok, [string]$label) {
    if ($ok) { Write-Host "  PASS  $label" -ForegroundColor Green; $script:pass++ }
    else      { Write-Host "  FAIL  $label" -ForegroundColor Red;   $script:fail++ }
}

function Get-Json([string]$url) {
    try { Invoke-RestMethod $url -TimeoutSec 10 } catch { $null }
}

function Post-Json([string]$url, [string]$body = "", [string]$ct = "application/json") {
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
        Invoke-RestMethod $url -Method POST -Body $bytes -ContentType $ct -TimeoutSec 15
    } catch { $null }
}

Write-Host "`n=== Phase 29 – Autonomous Self-Improvement Gate ===" -ForegroundColor Cyan

# ── Section 1: Build + Basic Health ────────────────────────────────────────
Write-Host "`n[Section 1] Build + Health" -ForegroundColor Yellow

$buildOut = dotnet build "$coreDir\Archimedes.Core.csproj" --no-incremental -v q 2>&1
$buildOk  = ($LASTEXITCODE -eq 0) -and ($buildOut -notmatch "error CS")
Assert $buildOk "dotnet build — 0 errors"

$health = Get-Json "$BaseUrl/health"
Assert ($health -ne $null)          "/health returns 200"
Assert ($health -match "OK|ok|true" -or $health.status -match "OK|ok")   "/health returns OK"

$chat = try { Invoke-WebRequest "$BaseUrl/chat" -UseBasicParsing -TimeoutSec 10 } catch { $null }
Assert ($chat -ne $null -and $chat.StatusCode -eq 200) "/chat returns 200"
Assert ($chat.Content -match "v0.29.0") "Chat UI version is v0.29.0"

# ── Section 2: Self-Improve Endpoints ──────────────────────────────────────
Write-Host "`n[Section 2] Self-Improve Endpoints" -ForegroundColor Yellow

$status = Get-Json "$BaseUrl/selfimprove/status"
Assert ($status -ne $null)                              "GET /selfimprove/status returns 200"
Assert ($null -ne $status.state)                        "status.state field exists"
Assert ($null -ne $status.cpuPercent -or $status.cpuPercent -eq 0) "status.cpuPercent field exists"
Assert ($null -ne $status.queueLength -or $status.queueLength -eq 0) "status.queueLength field exists"
Assert ($null -ne $status.totalCompleted -or $status.totalCompleted -eq 0) "status.totalCompleted field exists"
Assert ($null -ne $status.activeUserTasks -or $status.activeUserTasks -eq 0) "status.activeUserTasks field exists"

$history = Get-Json "$BaseUrl/selfimprove/history"
Assert ($history -ne $null)                             "GET /selfimprove/history returns 200"
Assert ($history -is [array])                           "history is array"

$insights = Get-Json "$BaseUrl/selfimprove/insights"
Assert ($insights -ne $null)                            "GET /selfimprove/insights returns 200"
Assert ($insights -is [array])                          "insights is array"

$gitLog = Get-Json "$BaseUrl/selfimprove/git-log"
Assert ($gitLog -ne $null)                              "GET /selfimprove/git-log returns 200"

$redirectOk = Post-Json "$BaseUrl/selfimprove/redirect" "CPU optimization techniques" "text/plain"
Assert ($redirectOk -ne $null -and $redirectOk.ok -eq $true) "POST /selfimprove/redirect returns ok=true"

$pauseOk = Post-Json "$BaseUrl/selfimprove/pause" ""
Assert ($pauseOk -ne $null -and $pauseOk.ok -eq $true) "POST /selfimprove/pause returns ok=true"

$resumeOk = Post-Json "$BaseUrl/selfimprove/resume" ""
Assert ($resumeOk -ne $null -and $resumeOk.ok -eq $true) "POST /selfimprove/resume returns ok=true"

# ── Section 3: /status/current includes selfDev ────────────────────────────
Write-Host "`n[Section 3] Status Current includes selfDev" -ForegroundColor Yellow

$current = Get-Json "$BaseUrl/status/current"
Assert ($current -ne $null)                             "GET /status/current returns 200"
# selfDev field exists (may be null or a string — both valid)
$hasSelfDev = $current.PSObject.Properties.Name -contains "selfDev"
Assert $hasSelfDev                                      "status/current has selfDev field"

# ── Section 4: Source Code Checks ──────────────────────────────────────────
Write-Host "`n[Section 4] Source Code Files" -ForegroundColor Yellow

Assert (Test-Path "$coreDir\SelfImprovementEngine.cs")  "SelfImprovementEngine.cs exists"
Assert (Test-Path "$coreDir\SelfImprovementModels.cs")  "SelfImprovementModels.cs exists"
Assert (Test-Path "$coreDir\SelfImprovementStore.cs")   "SelfImprovementStore.cs exists"
Assert (Test-Path "$coreDir\SelfAnalyzer.cs")           "SelfAnalyzer.cs exists"
Assert (Test-Path "$coreDir\ResourceGuard.cs")          "ResourceGuard.cs exists"
Assert (Test-Path "$coreDir\SelfGitManager.cs")         "SelfGitManager.cs exists"

$engineSrc = Get-Content "$coreDir\SelfImprovementEngine.cs" -Raw
Assert ($engineSrc -match "NotifyUserTaskStarted")      "Engine has NotifyUserTaskStarted"
Assert ($engineSrc -match "NotifyUserTaskCompleted")    "Engine has NotifyUserTaskCompleted"
Assert ($engineSrc -match "RedirectFocus")              "Engine has RedirectFocus"
Assert ($engineSrc -match "GetCurrentActivityDescription") "Engine has GetCurrentActivityDescription"
Assert ($engineSrc -match "GetStatus")                  "Engine has GetStatus"
Assert ($engineSrc -match "ResourceGuard")              "Engine uses ResourceGuard"
Assert ($engineSrc -match "SelfWorkCheckpoint")         "Engine uses SelfWorkCheckpoint (pause/resume)"

$modelsSrc = Get-Content "$coreDir\SelfImprovementModels.cs" -Raw
Assert ($modelsSrc -match "SelfWorkType")               "Models has SelfWorkType enum"
Assert ($modelsSrc -match "SelfEngineState")            "Models has SelfEngineState enum"
Assert ($modelsSrc -match "SelfWorkCheckpoint")         "Models has SelfWorkCheckpoint"
Assert ($modelsSrc -match "ANALYZE_PROCEDURES")         "Models has ANALYZE_PROCEDURES work type"
Assert ($modelsSrc -match "BENCHMARK_LLM")              "Models has BENCHMARK_LLM work type"
Assert ($modelsSrc -match "RESEARCH_WEB")               "Models has RESEARCH_WEB work type"
Assert ($modelsSrc -match "PATCH_CORE_CODE")            "Models has PATCH_CORE_CODE work type"

$guardSrc = Get-Content "$coreDir\ResourceGuard.cs" -Raw
Assert ($guardSrc -match "CpuThrottlePercent")          "ResourceGuard has throttle threshold"
Assert ($guardSrc -match "CpuPausePercent")             "ResourceGuard has pause threshold"
Assert ($guardSrc -match "OnThrottle")                  "ResourceGuard has OnThrottle event"
Assert ($guardSrc -match "OnPause")                     "ResourceGuard has OnPause event"
Assert ($guardSrc -match "OnResume")                    "ResourceGuard has OnResume event"

$gitSrc = Get-Content "$coreDir\SelfGitManager.cs" -Raw
Assert ($gitSrc -match "CommitCoreChange")              "SelfGitManager has CommitCoreChange"
Assert ($gitSrc -match '\.cs.*OrdinalIgnoreCase')       "SelfGitManager filters .cs files only"
Assert ($gitSrc -match "self-patch")                    "SelfGitManager commits with self-patch prefix"

$analyzerSrc = Get-Content "$coreDir\SelfAnalyzer.cs" -Raw
Assert ($analyzerSrc -match "GenerateWork")             "SelfAnalyzer has GenerateWork"
Assert ($analyzerSrc -match "ResearchTopics")           "SelfAnalyzer has research topic rotation"
Assert ($analyzerSrc -match "FindWeakProcedures")       "SelfAnalyzer has FindWeakProcedures"

# ── Section 5: Program.cs wiring ───────────────────────────────────────────
Write-Host "`n[Section 5] Program.cs Wiring" -ForegroundColor Yellow

$progSrc = Get-Content "$coreDir\Program.cs" -Raw
Assert ($progSrc -match "selfImprovementEngine")        "Program.cs creates selfImprovementEngine"
Assert ($progSrc -match "SelfAnalyzer")                 "Program.cs creates SelfAnalyzer"
Assert ($progSrc -match "ResourceGuard")                "Program.cs creates ResourceGuard"
Assert ($progSrc -match "SelfGitManager")               "Program.cs creates SelfGitManager"
Assert ($progSrc -match "/selfimprove/status")          "Program.cs has /selfimprove/status endpoint"
Assert ($progSrc -match "/selfimprove/history")         "Program.cs has /selfimprove/history endpoint"
Assert ($progSrc -match "/selfimprove/redirect")        "Program.cs has /selfimprove/redirect endpoint"
Assert ($progSrc -match "selfDev")                      "Program.cs /status/current includes selfDev"
Assert ($progSrc -match "selfImprovementEngine\.Stop")  "Program.cs stops engine on shutdown"

# ── Section 6: Chat UI ─────────────────────────────────────────────────────
Write-Host "`n[Section 6] Chat UI" -ForegroundColor Yellow

Assert ($chat.Content -match "selfdev-bar")             "Chat UI has selfdev-bar element"
Assert ($chat.Content -match "redirectSelfDev")         "Chat UI has redirectSelfDev function"
Assert ($chat.Content -match "selfimprove/redirect")    "Chat UI calls /selfimprove/redirect"
Assert ($chat.Content -match "d\.selfDev")              "Chat UI reads d.selfDev from status"

# ── Summary ────────────────────────────────────────────────────────────────
Write-Host "`n══════════════════════════════════════════" -ForegroundColor Cyan
$total = $pass + $fail
Write-Host "  Phase 29 Gate: $pass/$total PASS" -ForegroundColor $(if($fail -eq 0){"Green"}else{"Red"})
if ($fail -gt 0) { Write-Host "  $fail assertion(s) FAILED" -ForegroundColor Red }
Write-Host "══════════════════════════════════════════`n" -ForegroundColor Cyan
exit $(if($fail -eq 0){0}else{1})
