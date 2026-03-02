$pass = 0
$fail = 0
$BaseUrl = 'http://localhost:5051'

function Check($label, $condition) {
    if ($condition) { Write-Host "  PASS  $label"; $script:pass++ }
    else            { Write-Host "  FAIL  $label"; $script:fail++ }
}

Write-Host '====================================================='
Write-Host '  Phase 22 - Chat UI Ready Gate'
Write-Host '====================================================='

# ── [1] Core sanity ───────────────────────────────────────────────────────────
Write-Host ''
Write-Host '[1] Core sanity'
try {
    $h = Invoke-RestMethod "$BaseUrl/health"
    Check 'Core is running' ($h -eq 'OK' -or $h -match 'OK')
} catch { Check 'Core is running' $false }

# ── [2] GET /chat ─────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '[2] GET /chat - HTML page'
try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/chat" -UseBasicParsing
    Check 'GET /chat returns 200'                     ($r.StatusCode -eq 200)
    Check 'Content-Type is text/html'                 ($r.Headers['Content-Type'] -match 'text/html')
    Check 'Page has RTL direction (dir="rtl")'        ($r.Content -match 'dir="rtl"')
    Check 'Page includes tasks panel'                 ($r.Content -match 'tasks-panel')
    Check 'Page polls /system/metrics'                ($r.Content -match 'system/metrics')
    Check 'Page polls /status/current'                ($r.Content -match 'status/current')
    Check 'Page polls /tasks'                         ($r.Content -match '/tasks')
    Check 'Page posts to /chat/message'               ($r.Content -match '/chat/message')
    Check 'Page size > 5000 chars (full UI present)'  ($r.Content.Length -gt 5000)
} catch { Check 'GET /chat reachable' $false }

# ── [3] GET /system/metrics ───────────────────────────────────────────────────
Write-Host ''
Write-Host '[3] GET /system/metrics'
try {
    $m = Invoke-RestMethod "$BaseUrl/system/metrics"
    Check 'Response has cpuPercent field'           ($null -ne $m.cpuPercent)
    Check 'Response has ramUsedMb field'            ($null -ne $m.ramUsedMb)
    Check 'Response has ramTotalMb field'           ($null -ne $m.ramTotalMb)
    Check 'Response has uptimeSeconds field'        ($null -ne $m.uptimeSeconds)
    Check 'ramUsedMb > 0 (process is using memory)' ($m.ramUsedMb -gt 0)
    Check 'ramTotalMb > 0 (system RAM detected)'    ($m.ramTotalMb -gt 0)
    Check 'uptimeSeconds >= 0'                      ($m.uptimeSeconds -ge 0)
    Check 'cpuPercent >= 0'                         ($m.cpuPercent -ge 0)
    Write-Host "  INFO  CPU: $($m.cpuPercent)%  RAM: $($m.ramUsedMb)/$($m.ramTotalMb) MB  Uptime: $($m.uptimeSeconds)s"
} catch { Check 'GET /system/metrics reachable' $false }

# ── [4] GET /status/current ───────────────────────────────────────────────────
Write-Host ''
Write-Host '[4] GET /status/current'
try {
    $s = Invoke-RestMethod "$BaseUrl/status/current"
    Check 'Response has active field'      ($null -ne $s.active)
    Check 'Response has description field' ($s.PSObject.Properties['description'])
    Check 'Response has step field'        ($s.PSObject.Properties['step'])
    Write-Host "  INFO  active=$($s.active) description='$($s.description)'"
} catch { Check 'GET /status/current reachable' $false }

# ── [5] POST /chat/message - supported intent ─────────────────────────────────
Write-Host ''
Write-Host '[5] POST /chat/message - supported intent'
try {
    $b  = '{"message":"export data from testsite dashboard"}'
    $r  = Invoke-RestMethod "$BaseUrl/chat/message" -Method POST -Body $b -ContentType 'application/json'
    Check 'Response has reply field'             (-not [string]::IsNullOrEmpty($r.reply))
    Check 'Response has intent field'            (-not [string]::IsNullOrEmpty($r.intent))
    Check 'Recognized TESTSITE_EXPORT intent'    ($r.intent -eq 'TESTSITE_EXPORT')
    Check 'Task was created (taskId present)'    (-not [string]::IsNullOrEmpty($r.taskId))
    Write-Host "  INFO  intent=$($r.intent) taskId=$($r.taskId)"
} catch { Check 'POST /chat/message (supported intent) reachable' $false }

# ── [6] POST /chat/message - unsupported intent ───────────────────────────────
Write-Host ''
Write-Host '[6] POST /chat/message - unsupported intent'
try {
    $b = '{"message":"what is the weather today"}'
    $r = Invoke-RestMethod "$BaseUrl/chat/message" -Method POST -Body $b -ContentType 'application/json'
    Check 'Response has reply field'           (-not [string]::IsNullOrEmpty($r.reply))
    Check 'No task created for unknown intent' ([string]::IsNullOrEmpty($r.taskId))
    Write-Host "  INFO  intent=$($r.intent)"
} catch { Check 'POST /chat/message (unsupported intent) reachable' $false }

# ── [7] POST /chat/message - missing body ─────────────────────────────────────
Write-Host ''
Write-Host '[7] POST /chat/message - validation'
try {
    $code = 200
    try {
        Invoke-RestMethod "$BaseUrl/chat/message" -Method POST -Body '{}' -ContentType 'application/json'
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
    }
    Check 'Empty message returns 400' ($code -eq 400)
} catch { Check 'Validation check runnable' $false }

# ── [8] Metrics consistency ───────────────────────────────────────────────────
Write-Host ''
Write-Host '[8] Metrics - two calls show uptime increasing'
try {
    $m1 = Invoke-RestMethod "$BaseUrl/system/metrics"
    Start-Sleep -Seconds 2
    $m2 = Invoke-RestMethod "$BaseUrl/system/metrics"
    Check 'Uptime increases between calls' ($m2.uptimeSeconds -gt $m1.uptimeSeconds)
} catch { Check 'Metrics uptime check' $false }

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '====================================================='
if ($fail -eq 0) { Write-Host "  ALL PASS  $pass/$($pass+$fail) passed" }
else             { Write-Host "  RESULT: $pass PASS, $fail FAIL" }
Write-Host '====================================================='
