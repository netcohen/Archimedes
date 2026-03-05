# Phase 32 — Android OTA Updater (ADB WiFi) Gate
# Usage: .\scripts\phase32-ota-gate.ps1
# Expected: All assertions PASS

param([string]$BaseUrl = "http://localhost:5051",
      [string]$NetUrl  = "http://localhost:5052")

$pass = 0; $fail = 0
$coreDir    = "$PSScriptRoot\..\core"
$netDir     = "$PSScriptRoot\..\net"
$androidDir = "$PSScriptRoot\..\android"
$scriptsDir = "$PSScriptRoot"

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

Write-Host "`n=== Phase 32 — Android OTA Updater (ADB WiFi) Gate ===" -ForegroundColor Cyan

# ── Section 1: Build ──────────────────────────────────────────────────────────
Write-Host "`n[Section 1] Build" -ForegroundColor Yellow

$buildOut = dotnet build "$coreDir\Archimedes.Core.csproj" --no-incremental -v q 2>&1
$buildOk  = ($buildOut -notmatch "error CS") -and (($LASTEXITCODE -eq 0) -or ($buildOut -match "MSB3021"))
Assert $buildOk "dotnet build - 0 code errors"

Push-Location $netDir
$tscOut = npm run build 2>&1
$tscOk  = ($LASTEXITCODE -eq 0)
Pop-Location
Assert $tscOk "tsc build — 0 errors"

# ── Section 2: Core endpoints ──────────────────────────────────────────────────
Write-Host "`n[Section 2] Core endpoints" -ForegroundColor Yellow

$health = Get-Json "$BaseUrl/health"
Assert ($health -ne $null) "/health returns 200"

# OTA update status (no ADB/Gradle needed - just checks state)
$otaStatus = Get-Json "$BaseUrl/android/update/status"
Assert ($otaStatus -ne $null)                  "GET /android/update/status returns 200"
Assert ($otaStatus.running -eq $false)         "OTA: running=false - no update in progress"
Assert ($null -ne $otaStatus.scriptPath)       "OTA: scriptPath reported"
Assert ($null -ne $otaStatus.adbAvailable)     "OTA: adbAvailable field present"

# POST /android/update — returns started (even if ADB not installed, it queues)
$updateResult = Post-Json "$BaseUrl/android/update" '{"phoneIp":"192.168.1.1"}'
Assert ($updateResult -ne $null)               "POST /android/update returns 200"
Assert ($updateResult.ok -eq $true)            "/android/update returns ok=true"
Assert ($updateResult.status -eq "started")    "/android/update returns status=started"

# Wait for update to fail (no real phone/ADB in CI) — just check it doesn't hang
Start-Sleep -Seconds 2
$otaStatusAfter = Get-Json "$BaseUrl/android/update/status"
Assert ($otaStatusAfter -ne $null)             "GET /android/update/status after start returns 200"

# ── Section 3: Net service endpoints ──────────────────────────────────────────
Write-Host "`n[Section 3] Net service endpoints" -ForegroundColor Yellow

$netHealth = Get-Json "$NetUrl/health"
Assert ($netHealth -ne $null)                  "Net GET /health returns 200"

# Register device with IP (Phase 32 extension)
$regResult = Post-Json "$NetUrl/fcm/register-token" '{"deviceId":"ota-test-device","token":"test-token-123","ip":"192.168.1.50"}'
Assert ($regResult -ne $null)                  "POST /fcm/register-token (with IP) returns 200"
Assert ($regResult.ok -eq $true)               "register-token returns ok=true"

# Fetch device info (includes IP)
$deviceInfo = Get-Json "$NetUrl/v1/android/device/ota-test-device"
Assert ($deviceInfo -ne $null)                 "GET /v1/android/device/:id returns 200"
Assert ($deviceInfo.deviceId -eq "ota-test-device") "deviceInfo has correct deviceId"
Assert ($deviceInfo.ip -eq "192.168.1.50")     "deviceInfo has stored IP"
Assert ($null -ne $deviceInfo.fcmToken)        "deviceInfo has fcmToken"

# List devices
$devices = Get-Json "$NetUrl/v1/android/devices"
Assert ([bool]$devices -or ($devices -ne $null)) "GET /v1/android/devices returns 200"
Assert ($devices.Count -ge 1)                    "devices list has at least 1 entry"

# ── Section 4: Source Code ────────────────────────────────────────────────────
Write-Host "`n[Section 4] Source Code" -ForegroundColor Yellow

# Script file
Assert (Test-Path "$scriptsDir\update-android.sh")                                "update-android.sh exists"
$sh = Get-Content "$scriptsDir\update-android.sh" -Raw
Assert ($sh -match "gradlew assembleDebug")                                        "script builds APK with Gradle"
Assert ($sh -match "adb connect")                                                  "script uses adb connect"
Assert ($sh -match "adb install -r")                                               "script installs APK"
Assert ($sh -match "IP_CACHE")                                                     "script caches phone IP"
Assert ($sh -match "PHONE_IP")                                                     "script accepts phone IP arg"

# AppUpdater.cs
Assert (Test-Path "$coreDir\AppUpdater.cs")                                        "AppUpdater.cs exists"
$updater = Get-Content "$coreDir\AppUpdater.cs" -Raw
Assert ($updater -match "StartUpdateAsync")                                        "AppUpdater has StartUpdateAsync"
Assert ($updater -match "GetStatus")                                               "AppUpdater has GetStatus"
Assert ($updater -match "FetchPhoneIpAsync")                                       "AppUpdater has FetchPhoneIpAsync"
Assert ($updater -match "RunUpdateAsync")                                          "AppUpdater has RunUpdateAsync"
Assert ($updater -match "IsAdbAvailable")                                          "AppUpdater has IsAdbAvailable"
Assert ($updater -match "update-android\.sh")                                      "AppUpdater references update-android.sh"
Assert ($updater -match "NotifyAsync")                                             "AppUpdater sends FCM on success"

# Program.cs
$prog = Get-Content "$coreDir\Program.cs" -Raw
Assert ($prog -match "new AppUpdater\(")                                           "Program.cs creates AppUpdater"
Assert ($prog -match "appUpdater\.StartUpdateAsync")                               "Program.cs has /android/update endpoint"
Assert ($prog -match "appUpdater\.GetStatus")                                      "Program.cs has /android/update/status endpoint"
Assert ($prog -match '"/android/update"')                                          "Program.cs maps /android/update"
Assert ($prog -match '"/android/update/status"')                                   "Program.cs maps /android/update/status"

# Net index.ts
$netIdx = Get-Content "$netDir\src\index.ts" -Raw
Assert ($netIdx -match "_deviceRegistry")                                          "index.ts has device registry"
Assert ($netIdx -match "registerDevice")                                           "index.ts has registerDevice"
Assert ($netIdx -match 'v1/android/device')                                        "index.ts has device lookup endpoint"
Assert ($netIdx -match 'v1/android/devices')                                       "index.ts has devices list endpoint"
Assert ($netIdx -match 'ip:.*ip\b|registerDevice.*ip')                             "index.ts stores IP field"

# Android ArchimedesApp.kt
$appKt = Get-Content "$androidDir\app\src\main\java\com\archimedes\app\ArchimedesApp.kt" -Raw
Assert ($appKt -match "getLocalIpAddress")                                         "ArchimedesApp has getLocalIpAddress"
Assert ($appKt -match "NetworkInterface")                                          "ArchimedesApp uses NetworkInterface"
Assert ($appKt -match "fcm/register-token")                                        "ArchimedesApp POSTs to Net register-token"
Assert ($appKt -match 'localIp')                                                   "ArchimedesApp includes IP in registration"

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host "`n$(('─' * 50))" -ForegroundColor DarkGray
$total = $pass + $fail
if ($fail -eq 0) {
    Write-Host "  Phase 32 Gate: $pass/$total PASS" -ForegroundColor Green
} else {
    Write-Host "  Phase 32 Gate: $pass/$total PASS" -ForegroundColor Yellow
    Write-Host "  $fail assertion(s) FAILED"        -ForegroundColor Red
}
Write-Host "$(('─' * 50))`n" -ForegroundColor DarkGray

exit $fail
