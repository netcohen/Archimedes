# שולח prompt שהeuristic לא יכול לזהות -> חייב לבוא מLLM אם עובד
$prompt = "I need to retrieve structured data from a remote resource"

Write-Host "Sending ambiguous prompt (no heuristic keywords)..." -ForegroundColor Cyan
Write-Host "Prompt: $prompt" -ForegroundColor Gray
Write-Host ""

$resp = Invoke-RestMethod "http://localhost:5051/llm/interpret" -Method POST -Body $prompt -ContentType "text/plain" -TimeoutSec 300

Write-Host "intent            : $($resp.intent)"            -ForegroundColor $(if ($resp.isHeuristicFallback) { "Yellow" } else { "Green" })
Write-Host "confidence        : $($resp.confidence)"
Write-Host "isHeuristicFallback: $($resp.isHeuristicFallback)" -ForegroundColor $(if ($resp.isHeuristicFallback) { "Red" } else { "Green" })
Write-Host ""

if ($resp.isHeuristicFallback) {
    Write-Host "RESULT: LLM inference NOT working - still using heuristic" -ForegroundColor Red
} else {
    Write-Host "RESULT: Real LLM response confirmed!" -ForegroundColor Green
}
