# llm-smoke.ps1
# LLM smoke test - passes even if LLM unavailable

$ErrorActionPreference = "Stop"

Write-Host "=== LLM Smoke Test ===" -ForegroundColor Cyan

$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }

# Test health
Write-Host "`nChecking LLM health..." -ForegroundColor Yellow
try {
    $health = Invoke-RestMethod -Uri "$coreUrl/llm/health" -Method Get -TimeoutSec 10
    Write-Host "  Runtime: $($health.runtime)"
    Write-Host "  Model: $($health.model)"
    
    if ($health.available) {
        Write-Host "  Status: AVAILABLE" -ForegroundColor Green
    } else {
        Write-Host "  Status: UNAVAILABLE (using fallback)" -ForegroundColor Yellow
        Write-Host "  Error: $($health.error)" -ForegroundColor Gray
    }
} catch {
    Write-Host "  Could not connect to Core at $coreUrl" -ForegroundColor Red
    Write-Host "  Make sure Core is running: cd core; dotnet run" -ForegroundColor Gray
    exit 1
}

# Test interpret (should work even without LLM)
Write-Host "`nTesting interpret endpoint..." -ForegroundColor Yellow
try {
    $interpretResult = Invoke-RestMethod -Uri "$coreUrl/llm/interpret" -Method Post -Body "Login to testsite and download the CSV" -ContentType "text/plain" -TimeoutSec 15
    Write-Host "  Intent: $($interpretResult.intent)"
    Write-Host "  Confidence: $($interpretResult.confidence)"
    Write-Host "  Heuristic fallback: $($interpretResult.isHeuristicFallback)"
    
    if ($interpretResult.intent -and $interpretResult.intent -ne "") {
        Write-Host "  Result: PASS" -ForegroundColor Green
    } else {
        Write-Host "  Result: FAIL (no intent)" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "  Error: $_" -ForegroundColor Red
    exit 1
}

# Test summarize (should work even without LLM)
Write-Host "`nTesting summarize endpoint..." -ForegroundColor Yellow
try {
    $testContent = "The dashboard shows 4 records. Alpha is active with value 100. Beta is pending with value 250. Gamma is active with value 175. Delta is inactive with value 50."
    $summarizeResult = Invoke-RestMethod -Uri "$coreUrl/llm/summarize" -Method Post -Body $testContent -ContentType "text/plain" -TimeoutSec 15
    Write-Host "  Summary: $($summarizeResult.shortSummary)"
    Write-Host "  Insights count: $($summarizeResult.bulletInsights.Count)"
    Write-Host "  Heuristic fallback: $($summarizeResult.isHeuristicFallback)"
    
    if ($summarizeResult.shortSummary -and $summarizeResult.shortSummary -ne "") {
        Write-Host "  Result: PASS" -ForegroundColor Green
    } else {
        Write-Host "  Result: FAIL (no summary)" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "  Error: $_" -ForegroundColor Red
    exit 1
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
if ($health.available) {
    Write-Host "LLM: AVAILABLE - using $($health.model)" -ForegroundColor Green
} else {
    Write-Host "LLM: UNAVAILABLE - using heuristic fallback" -ForegroundColor Yellow
}
Write-Host "Interpret endpoint: WORKING" -ForegroundColor Green
Write-Host "Summarize endpoint: WORKING" -ForegroundColor Green
Write-Host "`nPASS: LLM smoke test completed" -ForegroundColor Green
exit 0
