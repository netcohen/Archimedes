$body = '{"steps":[{"action":"openUrl","params":{"url":"http://localhost:5052/testsite/dashboard"}},{"action":"extractTable","params":{"selector":"#dataTable"}}],"runId":"live-demo"}'
$r = Invoke-RestMethod http://localhost:5052/tool/browser/runStep -Method POST -Body $body -ContentType "application/json"

Write-Host ""
Write-Host "=== Live Browser Demo ===" -ForegroundColor Cyan
Write-Host ("Status     : " + $r.status) -ForegroundColor $(if ($r.status -eq "completed") { "Green" } else { "Red" })
Write-Host ("Total steps: " + $r.results.Count)
Write-Host ""

foreach ($step in $r.results) {
    $color = if ($step.success) { "Green" } else { "Red" }
    Write-Host (" [" + $step.action + "] success=" + $step.success + "  time=" + $step.durationMs + "ms") -ForegroundColor $color
}

Write-Host ""
Write-Host "Table rows extracted from real DOM:" -ForegroundColor Yellow
$tableStep = $r.results | Where-Object { $_.action -eq "extractTable" }
if ($null -ne $tableStep -and $null -ne $tableStep.data) {
    $rows = $tableStep.data
    foreach ($row in $rows) {
        Write-Host ("  " + ($row -join "  |  ")) -ForegroundColor White
    }
} else {
    Write-Host "  (no data)" -ForegroundColor DarkGray
}
Write-Host ""
