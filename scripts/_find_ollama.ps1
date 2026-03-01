$found = Get-ChildItem -Path "$env:LOCALAPPDATA" -Recurse -Filter "ollama.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($found) {
    Write-Host "Found: $($found.FullName)"
} else {
    Write-Host "ollama.exe not found yet"
}

# Also check Program Files
$found2 = Get-ChildItem -Path "C:\Program Files" -Recurse -Filter "ollama.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($found2) {
    Write-Host "Found in PF: $($found2.FullName)"
}

# Check if process exists
$proc = Get-Process | Where-Object { $_.Name -like "*llama*" }
foreach ($p in $proc) {
    Write-Host "Process: $($p.Name) PID=$($p.Id)"
}
