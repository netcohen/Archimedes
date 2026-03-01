$installerPath = "$env:TEMP\OllamaSetup.exe"

Write-Host "[2/4] Installing Ollama (silent)..." -ForegroundColor Cyan
$proc = Start-Process -FilePath $installerPath -ArgumentList "/S" -Wait -PassThru
Write-Host "  Installer exit code: $($proc.ExitCode)"

# Give it a moment to finish setting up
Start-Sleep -Seconds 5

# Find ollama.exe after install
$possiblePaths = @(
    "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe",
    "$env:LOCALAPPDATA\Ollama\ollama.exe",
    "C:\Program Files\Ollama\ollama.exe",
    "$env:ProgramFiles\Ollama\ollama.exe"
)

$ollamaExe = $null
foreach ($p in $possiblePaths) {
    if (Test-Path $p) {
        $ollamaExe = $p
        Write-Host "  Found ollama at: $p" -ForegroundColor Green
        break
    }
}

if (-not $ollamaExe) {
    # Try to find it
    $found = Get-ChildItem -Path "$env:LOCALAPPDATA" -Recurse -Filter "ollama.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        $ollamaExe = $found.FullName
        Write-Host "  Found ollama at: $ollamaExe" -ForegroundColor Green
    } else {
        Write-Host "  ollama.exe not found after install" -ForegroundColor Yellow
    }
}

# Output for next step
if ($ollamaExe) {
    Write-Host "OLLAMA_PATH=$ollamaExe"
}
