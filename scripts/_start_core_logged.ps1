$logFile = "C:\Users\netanel\Desktop\Archimedes\logs\core_debug.log"
$coreDir = "C:\Users\netanel\Desktop\Archimedes\core"

# Stop existing Core
Stop-Process -Name "Archimedes.Core" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Create logs dir
$logDir = Split-Path $logFile
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# Start Core with stdout/stderr to log file
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "run --configuration Release"
$psi.WorkingDirectory = $coreDir
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi

# Async output capture
$proc.OutputDataReceived += { param($s, $e); if ($e.Data) { Add-Content -Path $logFile -Value $e.Data } }
$proc.ErrorDataReceived  += { param($s, $e); if ($e.Data) { Add-Content -Path $logFile -Value ("[ERR] " + $e.Data) } }

$proc.Start() | Out-Null
$proc.BeginOutputReadLine()
$proc.BeginErrorReadLine()

Write-Host "Core started (PID $($proc.Id)), logging to: $logFile"
Write-Host "Waiting for health..."
Start-Sleep -Seconds 10

$resp = Invoke-WebRequest -Uri "http://localhost:5051/health" -UseBasicParsing -TimeoutSec 10 -ErrorAction SilentlyContinue
if ($resp -and $resp.StatusCode -eq 200) {
    Write-Host "Core health OK"
} else {
    Write-Host "Core not ready yet (model may still be loading)"
}

Write-Host "Log file: $logFile"
