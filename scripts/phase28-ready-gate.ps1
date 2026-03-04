# Phase 28 — Machine Migration (Octopus) — Ready Gate
# Asserts all new files exist, the server responds correctly, and endpoints work.
# Run: pwsh scripts/phase28-ready-gate.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$base    = "http://localhost:5051"
$pass    = 0
$fail    = 0
$results = @()

function Assert-True([bool]$cond, [string]$label) {
    if ($cond) {
        $script:pass++
        $script:results += "  [PASS] $label"
    } else {
        $script:fail++
        $script:results += "  [FAIL] $label"
    }
}

function Assert-Contains([string]$s, [string]$sub, [string]$label) {
    Assert-True ($s -match [regex]::Escape($sub)) $label
}

function Get-Json([string]$url) {
    $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
    return $r.Content | ConvertFrom-Json
}

function Post-Json([string]$url, [object]$body) {
    $json = $body | ConvertTo-Json -Compress
    $r = Invoke-WebRequest -Uri $url -Method POST -Body $json `
        -ContentType 'application/json' -UseBasicParsing -TimeoutSec 15
    return $r.Content | ConvertFrom-Json
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[1] New files exist" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$coreDir = Join-Path $PSScriptRoot '..\core'
$newFiles = @(
    'MigrationModels.cs',
    'MigrationStatePackager.cs',
    'MigrationDiskChecker.cs',
    'TaskSuspender.cs',
    'MigrationDeployer.cs',
    'MigrationResumeEngine.cs',
    'MigrationEngine.cs'
)
foreach ($f in $newFiles) {
    $p = Join-Path $coreDir $f
    Assert-True (Test-Path $p) "File exists: $f"
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[2] Build succeeds" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$buildOut = dotnet build "$coreDir" -c Release --nologo 2>&1
Assert-True ($LASTEXITCODE -eq 0) "dotnet build Release exit 0"
Assert-Contains ($buildOut -join "`n") 'Build succeeded' "Build output contains 'Build succeeded'"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[3] Server health" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$health = (Invoke-WebRequest -Uri "$base/health" -UseBasicParsing -TimeoutSec 5).Content
Assert-Contains $health 'OK' "GET /health returns OK"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[4] GET /migration/status - empty list" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$migList = Get-Json "$base/migration/status"
Assert-True ($null -ne $migList.count)   "GET /migration/status returns count field"
Assert-True ($null -ne $migList.migrations) "GET /migration/status returns migrations array"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[5] POST /migration/start - dry-run LOCAL_PATH" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$tmpTarget = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "arch_gate_target_$(Get-Random)")
$startResp = Post-Json "$base/migration/start" @{
    targetPath = $tmpTarget
    targetType = 'LOCAL_PATH'
    dryRun     = $true
}
Assert-True ($null -ne $startResp.migrationId) "POST /migration/start returns migrationId"
Assert-True ($startResp.dryRun -eq $true)       "POST /migration/start dryRun=true echoed"
Assert-True ($startResp.targetPath -eq $tmpTarget) "POST /migration/start targetPath echoed"

$migId = $startResp.migrationId

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[6] Poll GET /migration/status/{id} until COMPLETED (dry-run)" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$finalStatus = $null
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 500
    $planResp = Get-Json "$base/migration/status/$migId"
    if ($planResp.status -in @('COMPLETED','FAILED')) {
        $finalStatus = $planResp.status
        break
    }
}
Assert-True ($finalStatus -eq 'COMPLETED') "Dry-run migration reaches COMPLETED"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[7] GET /migration/status/{id} - structure check" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$plan = Get-Json "$base/migration/status/$migId"
Assert-True ($null -ne $plan.migrationId)    "Plan has migrationId"
Assert-True ($null -ne $plan.status)         "Plan has status"
Assert-True ($null -ne $plan.targetPath)     "Plan has targetPath"
Assert-True ($null -ne $plan.dryRun)         "Plan has dryRun"
Assert-True ($null -ne $plan.requiredDiskMB) "Plan has requiredDiskMB"
Assert-True ($null -ne $plan.startedAt)      "Plan has startedAt"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[8] POST /migration/start - missing targetPath returns 400" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

try {
    $bad = Invoke-WebRequest -Uri "$base/migration/start" -Method POST `
        -Body '{}' -ContentType 'application/json' `
        -UseBasicParsing -TimeoutSec 5
    Assert-True ($bad.StatusCode -eq 400) "Missing targetPath returns 400"
} catch {
    $sc = $_.Exception.Response.StatusCode.value__
    Assert-True ($sc -eq 400) "Missing targetPath returns 400 (caught)"
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[9] GET /migration/status/{unknown-id} returns 404" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

try {
    Invoke-WebRequest -Uri "$base/migration/status/doesnotexist" `
        -UseBasicParsing -TimeoutSec 5 | Out-Null
    Assert-True $false "404 expected for unknown migration id"
} catch {
    $sc = $_.Exception.Response.StatusCode.value__
    Assert-True ($sc -eq 404) "Unknown migration id returns 404"
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[10] POST /migration/receive endpoint exists (returns 400 without file)" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

try {
    $rcv = Invoke-WebRequest -Uri "$base/migration/receive" -Method POST `
        -Body '' -ContentType 'application/json' `
        -UseBasicParsing -TimeoutSec 5
    # 400 or 415 both prove the endpoint exists
    Assert-True ($rcv.StatusCode -in @(400,415)) "POST /migration/receive endpoint exists"
} catch {
    $sc = $_.Exception.Response.StatusCode.value__
    Assert-True ($sc -in @(400,415)) "POST /migration/receive endpoint exists (caught)"
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[11] MigrationModels - key fields present in source" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$modelsContent = Get-Content (Join-Path $coreDir 'MigrationModels.cs') -Raw
Assert-Contains $modelsContent 'RawDbKeyBase64'      "MigrationContinuationLog has RawDbKeyBase64"
Assert-Contains $modelsContent 'RawDeviceKeysBase64' "MigrationContinuationLog has RawDeviceKeysBase64"
Assert-Contains $modelsContent 'TaskMigrationAction' "TaskMigrationAction enum exists"
Assert-Contains $modelsContent 'MigrationStatus'     "MigrationStatus enum exists"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[12] EncryptedStore + DeviceKeyManager have migration helpers" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$esContent  = Get-Content (Join-Path $coreDir 'EncryptedStore.cs') -Raw
$dkmContent = Get-Content (Join-Path $coreDir 'ModernCrypto.cs')   -Raw
Assert-Contains $esContent  'GetRawKeyForMigration'   "EncryptedStore.GetRawKeyForMigration exists"
Assert-Contains $esContent  'RestoreKeyFromMigration' "EncryptedStore.RestoreKeyFromMigration exists"
Assert-Contains $dkmContent 'GetRawKeysForMigration'  "DeviceKeyManager.GetRawKeysForMigration exists"
Assert-Contains $dkmContent 'RestoreKeysFromMigration' "DeviceKeyManager.RestoreKeysFromMigration exists"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[13] Program.cs has Phase 28 wiring" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$progContent = Get-Content (Join-Path $coreDir 'Program.cs') -Raw
Assert-Contains $progContent 'MigrationEngine'       "Program.cs creates MigrationEngine"
Assert-Contains $progContent 'MigrationResumeEngine' "Program.cs creates MigrationResumeEngine"
Assert-Contains $progContent 'TryResume'             "Program.cs calls TryResume() at startup"
Assert-Contains $progContent '/migration/start'      "Program.cs has /migration/start endpoint"
Assert-Contains $progContent '/migration/receive'    "Program.cs has /migration/receive endpoint"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n[14] ChatHtml.cs version bump" -ForegroundColor Cyan
# ─────────────────────────────────────────────────────────────────────────────

$chatContent = Get-Content (Join-Path $coreDir 'ChatHtml.cs') -Raw
Assert-Contains $chatContent 'v0.28.0'            "ChatHtml.cs version is v0.28.0"
Assert-Contains $chatContent 'migration-panel'    "ChatHtml.cs has migration-panel element"
Assert-Contains $chatContent 'startMigration'     "ChatHtml.cs has startMigration() function"
Assert-Contains $chatContent 'pollMigration'      "ChatHtml.cs has pollMigration() function"

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────

Write-Host "`n══════════════════════════════════════════" -ForegroundColor DarkGray
foreach ($r in $results) {
    $color = if ($r -match '\[PASS\]') { 'Green' } else { 'Red' }
    Write-Host $r -ForegroundColor $color
}
Write-Host "══════════════════════════════════════════" -ForegroundColor DarkGray

$total = $pass + $fail
Write-Host "`nPhase 28 Gate: $pass/$total PASS" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Yellow' })

if ($fail -gt 0) {
    Write-Host "GATE FAILED — fix errors above before proceeding." -ForegroundColor Red
    exit 1
} else {
    Write-Host "GATE PASSED — Phase 28 Machine Migration ready." -ForegroundColor Green
    exit 0
}
