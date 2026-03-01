$coreDir = "C:\Users\netanel\Desktop\Archimedes\core"
Start-Process -FilePath "dotnet" -ArgumentList "run --configuration Release" -WorkingDirectory $coreDir -WindowStyle Minimized
Write-Host "Core process started"
Start-Sleep -Seconds 8
$resp = Invoke-WebRequest -Uri "http://localhost:5051/health" -UseBasicParsing -TimeoutSec 10 -ErrorAction SilentlyContinue
if ($resp -and $resp.StatusCode -eq 200) {
    Write-Host "Core health OK"
} else {
    Write-Host "Core not yet ready, waiting more..."
    Start-Sleep -Seconds 10
    $resp2 = Invoke-WebRequest -Uri "http://localhost:5051/health" -UseBasicParsing -TimeoutSec 10 -ErrorAction SilentlyContinue
    if ($resp2 -and $resp2.StatusCode -eq 200) {
        Write-Host "Core health OK"
    } else {
        Write-Host "Core may still be loading model..."
    }
}
