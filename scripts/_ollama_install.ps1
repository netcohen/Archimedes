$installerUrl = "https://ollama.com/download/OllamaSetup.exe"
$installerPath = "$env:TEMP\OllamaSetup.exe"

Write-Host "[1/4] Downloading Ollama installer..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing
$sizeMB = [math]::Round((Get-Item $installerPath).Length / 1MB, 1)
Write-Host "  Downloaded: $sizeMB MB to $installerPath" -ForegroundColor Green
