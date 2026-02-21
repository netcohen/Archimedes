$job = Start-Job { Invoke-WebRequest -Uri "http://localhost:5051/task/run-with-approval" -Method POST -Body "Proceed?" -ContentType "text/plain" -UseBasicParsing }
Start-Sleep -Seconds 2
$approvals = (Invoke-WebRequest -Uri "http://localhost:5051/approvals" -UseBasicParsing).Content
$arr = $approvals | ConvertFrom-Json
if ($arr.Count -eq 0) { Write-Host "No approvals"; exit 1 }
$taskId = $arr[0].taskId
if (-not $taskId) { $taskId = $arr[0].TaskId }
Write-Host "taskId=$taskId"
Invoke-WebRequest -Uri "http://localhost:5051/approval-response" -Method POST -Body "{`"taskId`":`"$taskId`",`"approved`":true}" -ContentType "application/json" -UseBasicParsing
$r = Wait-Job $job | Receive-Job
$j = $r.Content | ConvertFrom-Json
if ($j.approved) { Write-Host "Phase 8 PASS" } else { exit 1 }
