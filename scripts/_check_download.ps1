$path = "$env:TEMP\OllamaSetup.exe"
if (Test-Path $path) {
    $sizeMB = [math]::Round((Get-Item $path).Length / 1MB, 1)
    Write-Host "Downloaded so far: $sizeMB MB"
} else {
    Write-Host "File not found yet"
}
