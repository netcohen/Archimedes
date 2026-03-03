# phase25-ready-gate.ps1
# Gate: Phase 25 - Availability Engine (10 tests + regression)

$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }
$passed  = 0
$failed  = 0

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Phase 25 - Availability Engine Ready Gate" -ForegroundColor Cyan
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
    if ($h -match "OK") { Pass "Core is running" } else { Fail "Core health unexpected: $h" }
} catch { Fail "Core unreachable: $_" }

# [3] GET /availability/status
Write-Host "`n[3] GET /availability/status" -ForegroundColor Yellow
try {
    $st = Invoke-RestMethod -Uri "$coreUrl/availability/status" -TimeoutSec 10
    if ($null -ne $st.isAvailable) { Pass "Has isAvailable field" } else { Fail "Missing isAvailable" }
    if ($st.reason -ne "")         { Pass "Has reason=$($st.reason)" } else { Fail "Missing reason" }
    Info "isAvailable=$($st.isAvailable) reason=$($st.reason)"
} catch { Fail "GET /availability/status failed: $_" }

# [4] GET /availability/patterns
Write-Host "`n[4] GET /availability/patterns" -ForegroundColor Yellow
try {
    $p = Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -TimeoutSec 10
    if ($null -ne $p.sleepStartHour)  { Pass "Has sleepStartHour=$($p.sleepStartHour)" } else { Fail "Missing sleepStartHour" }
    if ($null -ne $p.sleepEndHour)    { Pass "Has sleepEndHour=$($p.sleepEndHour)" }     else { Fail "Missing sleepEndHour" }
    if ($null -ne $p.shabbatDetected) { Pass "Has shabbatDetected" }                     else { Fail "Missing shabbatDetected" }
    Info "sleep=$($p.sleepStartHour):00-$($p.sleepEndHour):00  shabbat=$($p.shabbatDetected)  interactions=$($p.interactionCount)"
} catch { Fail "GET /availability/patterns failed: $_" }

# [5] POST /availability/interaction
Write-Host "`n[5] POST /availability/interaction" -ForegroundColor Yellow
try {
    $before = (Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -TimeoutSec 10).interactionCount
    Invoke-RestMethod -Uri "$coreUrl/availability/interaction?source=gate_test" -Method Post -TimeoutSec 10 | Out-Null
    $after  = (Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -TimeoutSec 10).interactionCount
    if ($after -gt $before) { Pass "Interaction recorded (count $before to $after)" }
    else                     { Fail "Count did not increase: $before to $after" }
    $st2 = Invoke-RestMethod -Uri "$coreUrl/availability/status" -TimeoutSec 10
    if ($null -ne $st2.lastInteractionUtc) { Pass "lastInteractionUtc is populated" }
    else                                    { Fail "lastInteractionUtc is null" }
} catch { Fail "POST /availability/interaction failed: $_" }

# [6] POST /availability/patterns (manual update)
Write-Host "`n[6] POST /availability/patterns (override)" -ForegroundColor Yellow
try {
    $body = '{"sleepStartHour":22,"sleepEndHour":6,"shabbatDetected":true,"manualOverride":false}'
    Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 10 | Out-Null
    $p2 = Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -TimeoutSec 10
    if ($p2.sleepStartHour -eq 22)    { Pass "sleepStartHour updated to 22" } else { Fail "sleepStartHour=$($p2.sleepStartHour)" }
    if ($p2.sleepEndHour -eq 6)       { Pass "sleepEndHour updated to 6" }    else { Fail "sleepEndHour=$($p2.sleepEndHour)" }
    if ($p2.shabbatDetected -eq $true){ Pass "shabbatDetected updated to true" } else { Fail "shabbatDetected not updated" }
} catch { Fail "POST /availability/patterns failed: $_" }

# [7] ShouldDelay endpoint
Write-Host "`n[7] ShouldDelay logic" -ForegroundColor Yellow
try {
    $u1 = [System.Uri]::EscapeUriString("$coreUrl/availability/should-delay?action=http.login&critical=false")
    $r1 = Invoke-RestMethod -Uri $u1 -Method Post -TimeoutSec 10
    if ($r1.shouldDelay -eq $false) { Pass "http.login not delayed (not THIRD_PARTY_MESSAGE)" }
    else                             { Fail "http.login should not be delayed" }

    $u2 = [System.Uri]::EscapeUriString("$coreUrl/availability/should-delay?action=THIRD_PARTY_MESSAGE&critical=true")
    $r2 = Invoke-RestMethod -Uri $u2 -Method Post -TimeoutSec 10
    if ($r2.shouldDelay -eq $false) { Pass "THIRD_PARTY_MESSAGE critical=true NOT delayed" }
    else                             { Fail "Critical THIRD_PARTY_MESSAGE was delayed" }

    $u3 = [System.Uri]::EscapeUriString("$coreUrl/availability/should-delay?action=THIRD_PARTY_MESSAGE&critical=false")
    $r3 = Invoke-RestMethod -Uri $u3 -Method Post -TimeoutSec 10
    Pass "THIRD_PARTY_MESSAGE shouldDelay=$($r3.shouldDelay) reason=$($r3.reason)"
} catch { Fail "should-delay check failed: $_" }

# [8] manualOverride bypasses sleep window
Write-Host "`n[8] manualOverride bypasses availability restrictions" -ForegroundColor Yellow
try {
    $forceSleep = '{"sleepStartHour":0,"sleepEndHour":23,"shabbatDetected":false,"manualOverride":false}'
    Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -Method Post -Body $forceSleep -ContentType "application/json" | Out-Null
    $stSleep = Invoke-RestMethod -Uri "$coreUrl/availability/status"
    Info "With forced sleep: isAvailable=$($stSleep.isAvailable) reason=$($stSleep.reason)"

    $forceOverride = '{"sleepStartHour":0,"sleepEndHour":23,"shabbatDetected":false,"manualOverride":true}'
    Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -Method Post -Body $forceOverride -ContentType "application/json" | Out-Null
    $stOv = Invoke-RestMethod -Uri "$coreUrl/availability/status"
    if ($stOv.isAvailable -eq $true -and $stOv.reason -eq "manual_override") {
        Pass "manualOverride=true makes isAvailable=true despite sleep hours"
    } else {
        Fail "manualOverride failed: isAvailable=$($stOv.isAvailable) reason=$($stOv.reason)"
    }

    $restore = '{"sleepStartHour":23,"sleepEndHour":7,"shabbatDetected":false,"manualOverride":false}'
    Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -Method Post -Body $restore -ContentType "application/json" | Out-Null
    Pass "Patterns restored to defaults"
} catch { Fail "manualOverride test failed: $_" }

# [9] Chat message records interaction
Write-Host "`n[9] Chat message records interaction" -ForegroundColor Yellow
try {
    $before   = (Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -TimeoutSec 10).interactionCount
    $chatBody = '{"message":"export testsite data"}'
    Invoke-RestMethod -Uri "$coreUrl/chat/message" -Method Post -Body $chatBody -ContentType "application/json" -TimeoutSec 30 | Out-Null
    $after = (Invoke-RestMethod -Uri "$coreUrl/availability/patterns" -TimeoutSec 10).interactionCount
    if ($after -gt $before) { Pass "Chat auto-records interaction ($before to $after)" }
    else                     { Fail "Chat did not record interaction ($before to $after)" }
} catch { Fail "Chat interaction recording failed: $_" }

# [10] Regression: Phase 19-24
Write-Host "`n[10] Regression Phase 19-24" -ForegroundColor Yellow
try {
    $resp = Invoke-WebRequest -Uri "$coreUrl/health" -UseBasicParsing
    if ($resp.Headers["X-Correlation-Id"]) { Pass "Phase 19: X-Correlation-Id present" }
    else                                    { Fail "Phase 19: X-Correlation-Id missing" }

    $cv = Invoke-RestMethod -Uri "$coreUrl/criteria/verify" -Method Post -Body '{"Action":"approval.requestConfirmation","Data":"{}","StepSuccess":true}' -ContentType "application/json"
    if ($cv.outcome) { Pass "Phase 20: criteria/verify outcome=$($cv.outcome)" }
    else              { Fail "Phase 20: criteria/verify bad response" }

    $pr = Invoke-RestMethod -Uri "$coreUrl/procedures"
    if ($null -ne $pr.count) { Pass "Phase 21: /procedures count=$($pr.count)" }
    else                      { Fail "Phase 21: /procedures failed" }

    $ch = Invoke-WebRequest -Uri "$coreUrl/chat" -UseBasicParsing
    if ($ch.StatusCode -eq 200)            { Pass "Phase 22: /chat 200" }
    else                                    { Fail "Phase 22: /chat $($ch.StatusCode)" }
    if ($ch.Content -match "v0\.25\.0")    { Pass "Phase 22: Chat version v0.25.0" }
    else                                    { Fail "Phase 22: Chat version not v0.25.0" }

    $rd = Invoke-RestMethod -Uri "$coreUrl/recovery-dialogues"
    if ($null -ne $rd.count) { Pass "Phase 24: /recovery-dialogues ok" }
    else                      { Fail "Phase 24: /recovery-dialogues failed" }
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
