$searchPaths = @(
    "$env:LOCALAPPDATA\Archimedes",
    "$env:TEMP",
    "C:\Users\netanel\Desktop\Archimedes"
)
foreach ($p in $searchPaths) {
    if (Test-Path $p) {
        $logs = Get-ChildItem -Path $p -Recurse -Filter "*.log" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 3
        foreach ($l in $logs) {
            Write-Host $l.FullName "  (" $l.LastWriteTime ")"
        }
    }
}
