# Phase 31 — Firebase Bidirectional + Android App Gate
# Usage: .\scripts\phase31-ready-gate.ps1
# Expected: All assertions PASS

param([string]$BaseUrl = "http://localhost:5051",
      [string]$NetUrl  = "http://localhost:5052")

$pass = 0; $fail = 0
$coreDir    = "$PSScriptRoot\..\core"
$netDir     = "$PSScriptRoot\..\net"
$androidDir = "$PSScriptRoot\..\android"

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

Write-Host "`n=== Phase 31 — Firebase Bidirectional + Android App Gate ===" -ForegroundColor Cyan

# ── Section 1: Build ──────────────────────────────────────────────────────────
Write-Host "`n[Section 1] Build" -ForegroundColor Yellow

$buildOut = dotnet build "$coreDir\Archimedes.Core.csproj" --no-incremental -v q 2>&1
$buildOk  = ($LASTEXITCODE -eq 0) -and ($buildOut -notmatch "error CS")
Assert $buildOk "dotnet build — 0 errors"

$tscOut = & cmd /c "cd /d `"$netDir`" && npm run build 2>&1"
$tscOk  = ($LASTEXITCODE -eq 0)
Assert $tscOk "tsc build — 0 errors"

# ── Section 2: Core endpoints ─────────────────────────────────────────────────
Write-Host "`n[Section 2] Core endpoints" -ForegroundColor Yellow

$health = Get-Json "$BaseUrl/health"
Assert ($health -ne $null) "/health returns 200"
Assert ($health -match "OK|ok|true" -or $health.status -match "OK|ok") "/health returns OK"

$status = Get-Json "$BaseUrl/status/current"
Assert ($status -ne $null)                    "GET /status/current returns 200"
Assert ($null -ne $status.androidBridge)      "status/current has androidBridge field"
Assert ($status.androidBridge.polling -eq $true) "androidBridge.polling = true"
Assert ($null -ne $status.osHealth)           "status/current still has osHealth"

$notifyResult = Post-Json "$BaseUrl/android/notify" '{"title":"Test","body":"Gate test notification"}'
Assert ($notifyResult -ne $null)              "POST /android/notify returns 200"
Assert ($notifyResult.ok -eq $true)           "/android/notify returns ok=true"

# ── Section 3: Net service endpoints ──────────────────────────────────────────
Write-Host "`n[Section 3] Net service endpoints" -ForegroundColor Yellow

$netHealth = Get-Json "$NetUrl/health"
Assert ($netHealth -ne $null)                 "Net GET /health returns 200"

# FCM token registration
$tokenResult = Post-Json "$NetUrl/fcm/register-token" '{"deviceId":"gate-test-device","token":"fake-fcm-token-123"}'
Assert ($tokenResult -ne $null)               "POST /fcm/register-token returns 200"
Assert ($tokenResult.ok -eq $true)            "/fcm/register-token returns ok=true"
Assert ($tokenResult.deviceId -eq "gate-test-device") "register-token echoes deviceId"

# FCM status
$fcmStatus = Get-Json "$NetUrl/v1/android/fcm/status"
Assert ($fcmStatus -ne $null)                 "GET /v1/android/fcm/status returns 200"
Assert ($null -ne $fcmStatus.devices)         "fcm/status has devices field"
Assert ($fcmStatus.devices -ge 1)             "fcm/status shows registered device"

# Android command — create
$cmdResult = Post-Json "$NetUrl/v1/android/command" '{"type":"STATUS","payload":{},"deviceId":"gate-test-device"}'
Assert ($cmdResult -ne $null)                 "POST /v1/android/command returns 201"
Assert (-not [string]::IsNullOrEmpty($cmdResult.id)) "command returns id"
Assert ($cmdResult.status -eq "PENDING")      "new command status is PENDING"
$cmdId = $cmdResult.id

# Pending commands
$pending = Get-Json "$NetUrl/v1/android/commands/pending"
Assert ($null -ne $pending)                   "GET /v1/android/commands/pending returns 200"
Assert ($pending.Count -ge 1)                 "pending commands contains our command"

# Update command result
$resultBody = '{"status":"DONE","result":{"isRunning":true}}'
$updateResult = Post-Json "$NetUrl/v1/android/commands/$cmdId/result" $resultBody
Assert ($updateResult -ne $null)              "POST /v1/android/commands/:id/result returns 200"
Assert ($updateResult.status -eq "DONE")      "command status updated to DONE"

# Notifications
$notifyNet = Post-Json "$NetUrl/v1/android/notify" '{"title":"Gate","body":"Test from gate","deviceId":"gate-test-device"}'
Assert ($notifyNet -ne $null)                 "POST /v1/android/notify (Net) returns 200"
Assert ($notifyNet.queued -eq $true)          "notification queued=true"

$notifications = Get-Json "$NetUrl/v1/android/notifications"
Assert ($null -ne $notifications)             "GET /v1/android/notifications returns 200"
Assert ($notifications.Count -ge 1)           "notifications queue has entries"

$readResult = Post-Json "$NetUrl/v1/android/notifications/read-all" '{}'
Assert ($readResult -ne $null)                "POST /v1/android/notifications/read-all returns 200"
Assert ($readResult.ok -eq $true)             "read-all returns ok=true"

# Android status proxy
$androidStatus = Get-Json "$NetUrl/v1/android/status"
Assert ($androidStatus -ne $null)             "GET /v1/android/status returns 200"
Assert ($null -ne $androidStatus.androidBridge) "android status includes androidBridge"

# ── Section 4: Source Code Files ──────────────────────────────────────────────
Write-Host "`n[Section 4] Source Code" -ForegroundColor Yellow

Assert (Test-Path "$coreDir\AndroidBridge.cs")                                    "AndroidBridge.cs exists"
Assert (Test-Path "$netDir\src\fcm.ts")                                           "fcm.ts exists"
Assert (Test-Path "$netDir\src\commands.ts")                                      "commands.ts exists"

# Android app files
Assert (Test-Path "$androidDir\app\src\main\java\com\archimedes\app\ServerConfig.kt")   "ServerConfig.kt exists"
Assert (Test-Path "$androidDir\app\src\main\java\com\archimedes\app\ArchimedesApi.kt")  "ArchimedesApi.kt exists"
Assert (Test-Path "$androidDir\app\src\main\java\com\archimedes\app\ArchimedesApp.kt")  "ArchimedesApp.kt exists"
Assert (Test-Path "$androidDir\app\src\main\java\com\archimedes\app\PollingWorker.kt")  "PollingWorker.kt exists"
Assert (Test-Path "$androidDir\app\src\main\java\com\archimedes\app\TaskActivity.kt")   "TaskActivity.kt exists"
Assert (Test-Path "$androidDir\app\src\main\java\com\archimedes\app\SettingsActivity.kt") "SettingsActivity.kt exists"

Assert (Test-Path "$androidDir\app\src\main\res\layout\activity_task.xml")        "activity_task.xml exists"
Assert (Test-Path "$androidDir\app\src\main\res\layout\activity_settings.xml")    "activity_settings.xml exists"

# Source code content checks
$bridge = Get-Content "$coreDir\AndroidBridge.cs" -Raw
Assert ($bridge -match "public void Start\(\)")                                   "AndroidBridge has Start()"
Assert ($bridge -match "public void Stop\(\)")                                    "AndroidBridge has Stop()"
Assert ($bridge -match "PollOnce")                                                "AndroidBridge has PollOnce"
Assert ($bridge -match "ExecuteCommand")                                          "AndroidBridge has ExecuteCommand"
Assert ($bridge -match "NotifyAsync")                                             "AndroidBridge has NotifyAsync"
Assert ($bridge -match "ExecuteTaskCommand" -and $bridge -match "ExecuteGoalCommand") "AndroidBridge handles TASK/GOAL/CHAT"
Assert ($bridge -match "v1/android/commands/pending")                             "AndroidBridge polls commands endpoint"

$fcm = Get-Content "$netDir\src\fcm.ts" -Raw
Assert ($fcm -match "registerToken")                                              "fcm.ts has registerToken"
Assert ($fcm -match "sendToDevice")                                               "fcm.ts has sendToDevice"
Assert ($fcm -match "sendToAll")                                                  "fcm.ts has sendToAll"
Assert ($fcm -match "getPendingNotifications")                                    "fcm.ts has getPendingNotifications"
Assert ($fcm -match "GOOGLE_APPLICATION_CREDENTIALS")                             "fcm.ts checks GOOGLE_APPLICATION_CREDENTIALS"
Assert ($fcm -match "polling")                                                    "fcm.ts supports polling fallback"

$cmds = Get-Content "$netDir\src\commands.ts" -Raw
Assert ($cmds -match "createCommand")                                             "commands.ts has createCommand"
Assert ($cmds -match "getPendingCommands")                                        "commands.ts has getPendingCommands"
Assert ($cmds -match "updateCommand")                                             "commands.ts has updateCommand"
Assert ($cmds -match "purgeOldCommands")                                          "commands.ts has purgeOldCommands"

$serverCfg = Get-Content "$androidDir\app\src\main\java\com\archimedes\app\ServerConfig.kt" -Raw
Assert ($serverCfg -match "getNetUrl")                                            "ServerConfig has getNetUrl"
Assert ($serverCfg -match "setNetUrl")                                            "ServerConfig has setNetUrl"
Assert ($serverCfg -match "getDeviceId")                                          "ServerConfig has getDeviceId"
Assert ($serverCfg -match "DEFAULT_NET_URL")                                      "ServerConfig has DEFAULT_NET_URL"

$apiFile = Get-Content "$androidDir\app\src\main\java\com\archimedes\app\ArchimedesApi.kt" -Raw
Assert ($apiFile -match "getStatus")                                              "ArchimedesApi has getStatus"
Assert ($apiFile -match "getApprovals")                                           "ArchimedesApi has getApprovals"
Assert ($apiFile -match "sendCommand")                                            "ArchimedesApi has sendCommand"
Assert ($apiFile -match "getPendingNotifications")                                "ArchimedesApi has getPendingNotifications"
Assert ($apiFile -match "isReachable")                                            "ArchimedesApi has isReachable"

$pollWorker = Get-Content "$androidDir\app\src\main\java\com\archimedes\app\PollingWorker.kt" -Raw
Assert ($pollWorker -match "CoroutineWorker")                                     "PollingWorker extends CoroutineWorker"
Assert ($pollWorker -match "WorkManager")                                         "PollingWorker uses WorkManager"
Assert ($pollWorker -match "schedule")                                            "PollingWorker has schedule()"

$manifest = Get-Content "$androidDir\app\src\main\AndroidManifest.xml" -Raw
Assert ($manifest -match "ArchimedesApp")                                         "Manifest references ArchimedesApp"
Assert ($manifest -match "TaskActivity")                                          "Manifest has TaskActivity"
Assert ($manifest -match "SettingsActivity")                                      "Manifest has SettingsActivity"
Assert ($manifest -match "POST_NOTIFICATIONS")                                    "Manifest has POST_NOTIFICATIONS permission"

$appBuild = Get-Content "$androidDir\app\build.gradle.kts" -Raw
Assert ($appBuild -notmatch 'firebase-bom')                                        "Firebase BOM removed from Android (server-side only)"
Assert ($appBuild -match "work-runtime")                                          "build.gradle has WorkManager"
Assert ($appBuild -match "viewBinding = true")                                    "build.gradle has viewBinding"

# ── Section 5: Program.cs Wiring ──────────────────────────────────────────────
Write-Host "`n[Section 5] Program.cs Wiring" -ForegroundColor Yellow

$prog = Get-Content "$coreDir\Program.cs" -Raw
Assert ($prog -match "new AndroidBridge\(")                                       "Program.cs creates AndroidBridge"
Assert ($prog -match "androidBridge\.Start\(\)")                                  "Program.cs starts AndroidBridge"
Assert ($prog -match "androidBridge\.Stop\(\)")                                   "Program.cs stops AndroidBridge on shutdown"
Assert ($prog -match '"/android/notify"')                                         "Program.cs has /android/notify endpoint"
Assert ($prog -match "androidBridge")                                             "Program.cs has androidBridge in /status/current"

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host "`n$(('─' * 50))" -ForegroundColor DarkGray
$total = $pass + $fail
if ($fail -eq 0) {
    Write-Host "  Phase 31 Gate: $pass/$total PASS" -ForegroundColor Green
} else {
    Write-Host "  Phase 31 Gate: $pass/$total PASS" -ForegroundColor Yellow
    Write-Host "  $fail assertion(s) FAILED"        -ForegroundColor Red
}
Write-Host "$(('─' * 50))`n" -ForegroundColor DarkGray

exit $fail
