$logFile = "C:\Users\netanel\Desktop\Archimedes\logs\core_debug.log"
$coreDir  = "C:\Users\netanel\Desktop\Archimedes\core"

Stop-Process -Name "Archimedes.Core" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (Test-Path $logFile) { Remove-Item $logFile -Force }

# Start with simple redirect
$proc = Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/c dotnet run --configuration Release > `"$logFile`" 2>&1" `
    -WorkingDirectory $coreDir `
    -WindowStyle Hidden `
    -PassThru

Write-Host "Core started PID=$($proc.Id), waiting..."
Start-Sleep -Seconds 12

$resp = Invoke-WebRequest -Uri "http://localhost:5051/health" -UseBasicParsing -TimeoutSec 10 -ErrorAction SilentlyContinue
if ($resp -and $resp.StatusCode -eq 200) {
    Write-Host "Core health OK"
} else {
    Write-Host "Core not ready yet"
}
Write-Host "Log: $logFile"
