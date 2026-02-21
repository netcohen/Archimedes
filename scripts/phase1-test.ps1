# Phase 1: Core <-> Net communication test
# Run: 1) Start net server (background), 2) Start core server (background), 3) Call cross-call endpoint

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$netProc = $null
$coreProc = $null

try {
    Write-Host "Starting Net server..."
    $netProc = Start-Process -FilePath "node" -ArgumentList "dist/index.js" -WorkingDirectory "$root/net" -PassThru -NoNewWindow
    
    Start-Sleep -Seconds 2
    
    Write-Host "Starting Core server..."
    $coreProc = Start-Process -FilePath "dotnet" -ArgumentList "run" -WorkingDirectory "$root/core" -PassThru -NoNewWindow
    
    Start-Sleep -Seconds 4
    
    Write-Host "Testing Net /health..."
    $netHealth = (Invoke-WebRequest -Uri "http://localhost:5052/health" -UseBasicParsing).Content.Trim()
    if ($netHealth -ne "OK") { throw "Net /health returned: $netHealth" }
    Write-Host "  OK"
    
    Write-Host "Testing Core /health..."
    $coreHealth = (Invoke-WebRequest -Uri "http://localhost:5051/health" -UseBasicParsing).Content.Trim()
    if ($coreHealth -ne "OK") { throw "Core /health returned: $coreHealth" }
    Write-Host "  OK"
    
    Write-Host "Testing Core->Net cross-call (/ping-net)..."
    $ping = (Invoke-WebRequest -Uri "http://localhost:5051/ping-net" -UseBasicParsing).Content.Trim()
    if ($ping -ne "OK") { throw "Core /ping-net returned: $ping" }
    Write-Host "  OK"
    
    Write-Host ""
    Write-Host "Phase 1 self-test: PASS"
}
finally {
    if ($coreProc) { Stop-Process -Id $coreProc.Id -Force -ErrorAction SilentlyContinue }
    if ($netProc) { Stop-Process -Id $netProc.Id -Force -ErrorAction SilentlyContinue }
}
