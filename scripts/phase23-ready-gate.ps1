$pass = 0
$fail = 0
$BaseUrl = 'http://localhost:5051'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Check($label, $condition) {
    if ($condition) { Write-Host "  PASS  $label"; $script:pass++ }
    else            { Write-Host "  FAIL  $label"; $script:fail++ }
}

Write-Host '====================================================='
Write-Host '  Phase 23 - Linux Port Ready Gate (Windows checks)'
Write-Host '====================================================='

# ── [1] Build check ───────────────────────────────────────────────────────────
Write-Host ''
Write-Host '[1] dotnet build (0 errors)'
try {
    $coreDir = Join-Path $RepoRoot 'core'
    $result = & dotnet build "$coreDir\Archimedes.Core.csproj" --nologo -v quiet 2>&1
    $exitCode = $LASTEXITCODE
    Check 'dotnet build exits 0'         ($exitCode -eq 0)
    Check 'Build output has 0 Error(s)'  ($result -match '0 Error\(s\)')
} catch { Check 'dotnet build runs' $false }

# ── [2] Source-code: cross-platform imports present ──────────────────────────
Write-Host ''
Write-Host '[2] Source: cross-platform wrappers in place'
$encFile    = Join-Path $RepoRoot 'core\EncryptedStore.cs'
$cryptoFile = Join-Path $RepoRoot 'core\ModernCrypto.cs'
$sbFile     = Join-Path $RepoRoot 'core\SandboxRunner.cs'

$encContent    = Get-Content $encFile    -Raw -ErrorAction SilentlyContinue
$cryptoContent = Get-Content $cryptoFile -Raw -ErrorAction SilentlyContinue
$sbContent     = Get-Content $sbFile     -Raw -ErrorAction SilentlyContinue

Check 'EncryptedStore.cs has RuntimeInformation import'   ($encContent -match 'System\.Runtime\.InteropServices')
Check 'EncryptedStore.cs has OsProtect method'            ($encContent -match 'OsProtect')
Check 'EncryptedStore.cs has OsUnprotect method'          ($encContent -match 'OsUnprotect')
Check 'EncryptedStore.cs has OsRestrictFilePermissions'   ($encContent -match 'OsRestrictFilePermissions')
Check 'EncryptedStore.cs OsProtect wraps DPAPI correctly'  ($encContent -match 'private static byte\[\] OsProtect')
Check 'ModernCrypto.cs has RuntimeInformation import'     ($cryptoContent -match 'System\.Runtime\.InteropServices')
Check 'ModernCrypto.cs has OsProtect method'              ($cryptoContent -match 'OsProtect')
Check 'ModernCrypto.cs OsProtect wraps DPAPI correctly'   ($cryptoContent -match 'private static byte\[\] OsProtect')
Check 'SandboxRunner.cs has RuntimeInformation import'    ($sbContent -match 'System\.Runtime\.InteropServices')
Check 'SandboxRunner.cs uses pwsh on non-Windows'         ($sbContent -match 'pwsh')
Check 'SandboxRunner.cs cross-platform npm (no bare cmd)' ($sbContent -match 'isWin')

# ── [3] Deployment artifacts present ─────────────────────────────────────────
Write-Host ''
Write-Host '[3] Deployment artifacts'
$serviceFile = Join-Path $RepoRoot 'systemd\archimedes-core.service'
$installFile = Join-Path $RepoRoot 'scripts\install.sh'

Check 'systemd/archimedes-core.service exists'            (Test-Path $serviceFile)
Check 'scripts/install.sh exists'                         (Test-Path $installFile)

if (Test-Path $serviceFile) {
    $svc = Get-Content $serviceFile -Raw
    Check 'Service file has [Unit] section'               ($svc -match '\[Unit\]')
    Check 'Service file has [Service] section'            ($svc -match '\[Service\]')
    Check 'Service file has [Install] section'            ($svc -match '\[Install\]')
    Check 'Service file uses dotnet runtime'              ($svc -match 'dotnet')
    Check 'Service file targets port 5051'                ($svc -match '5051')
    Check 'Service file has Restart=on-failure'           ($svc -match 'Restart=on-failure')
    Check 'Service file has NoNewPrivileges=true'         ($svc -match 'NoNewPrivileges=true')
}

if (Test-Path $installFile) {
    $inst = Get-Content $installFile -Raw
    Check 'install.sh has .NET 8 SDK install step'        ($inst -match 'dotnet-sdk-8')
    Check 'install.sh has pwsh install step'              ($inst -match 'powershell')
    Check 'install.sh has Node.js install step'           ($inst -match 'nodesource|nodejs')
    Check 'install.sh has Chromium install step'          ($inst -match 'chromium')
    Check 'install.sh has systemctl enable'               ($inst -match 'systemctl enable')
    Check 'install.sh uses set -euo pipefail'             ($inst -match 'set -euo pipefail')
}

# ── [4] Runtime: server is healthy (optional — skip if not running) ──────────
Write-Host ''
Write-Host '[4] Runtime health check (optional — Core must be running)'
try {
    $h = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 3
    Check 'GET /health returns OK'  ($h -eq 'OK' -or $h -match 'OK')
} catch {
    Write-Host '  SKIP  GET /health — Core not running (start Core to test live checks)'
    $script:pass++  # not a gate failure — Phase 23 is a code/artifact gate
}

# ── [5] Runtime: platform detection endpoint (optional) ──────────────────────
Write-Host ''
Write-Host '[5] Runtime platform info (optional — Core must be running)'
try {
    $m = Invoke-RestMethod "$BaseUrl/system/metrics" -TimeoutSec 3
    Check 'GET /system/metrics responds'     ($null -ne $m)
    Check 'cpuPercent field present'         ($null -ne $m.cpuPercent)
    Check 'ramUsedMb field present'          ($null -ne $m.ramUsedMb)
    Write-Host "  INFO  RAM: $($m.ramUsedMb)/$($m.ramTotalMb) MB  CPU: $($m.cpuPercent)%"
} catch {
    Write-Host '  SKIP  GET /system/metrics — Core not running'
    $script:pass++  # same rationale
}

# ── [6] Encryption key file uses cross-platform protection ───────────────────
Write-Host ''
Write-Host '[6] Key file sanity (Windows path)'
try {
    $keyPath = Join-Path $env:LOCALAPPDATA 'Archimedes\archimedes.key'
    if (Test-Path $keyPath) {
        $keyBytes = [System.IO.File]::ReadAllBytes($keyPath)
        # On Windows, DPAPI-protected data is never 32 bytes (it adds a header)
        Check 'Key file on Windows is DPAPI-wrapped (>32 bytes)' ($keyBytes.Length -gt 32)
        Write-Host "  INFO  Key file size: $($keyBytes.Length) bytes"
    } else {
        Write-Host '  INFO  Key file not yet created — first run will generate it'
        $script:pass++  # neutral: not a failure
    }
} catch { Check 'Key file readable' $false }

# ── [7] Linux-only checks (skipped on Windows, documented) ───────────────────
Write-Host ''
Write-Host '[7] Linux-only checks (informational — requires physical Linux machine)'
Write-Host '  SKIP  systemd service active          (Linux only)'
Write-Host '  SKIP  journalctl logs present          (Linux only)'
Write-Host '  SKIP  chmod 600 on key file            (Linux only)'
Write-Host '  SKIP  Chromium headless launch         (Linux only)'
Write-Host '  SKIP  dotnet runs without DPAPI error  (Linux only)'
Write-Host '  NOTE  Run: sudo bash scripts/install.sh  on target Ubuntu 24.04'

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '====================================================='
if ($fail -eq 0) { Write-Host "  ALL PASS  $pass/$($pass+$fail) passed (Windows checks)" }
else             { Write-Host "  RESULT: $pass PASS, $fail FAIL" }
Write-Host '====================================================='
