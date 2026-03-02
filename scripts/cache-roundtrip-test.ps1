$pass = 0
$fail = 0
$base = 'http://localhost:5051'

function Check($label, $condition) {
    if ($condition) { Write-Host "  PASS  $label"; $script:pass++ }
    else            { Write-Host "  FAIL  $label"; $script:fail++ }
}

Write-Host '====================================================='
Write-Host '  Phase 21 - Cache Round-Trip Test'
Write-Host '====================================================='
Write-Host ''
Write-Host '[0] Clean state - delete any existing TESTSITE_EXPORT procedure'
$existing = Invoke-RestMethod -Uri "$base/procedures" -Method GET
$existing.procedures | Where-Object { $_.intent -eq 'TESTSITE_EXPORT' } | ForEach-Object {
    Invoke-RestMethod -Uri "$base/procedures/$($_.id)" -Method DELETE | Out-Null
    Write-Host "  INFO  Deleted existing procedure: $($_.id)"
}
Start-Sleep -Milliseconds 200

Write-Host ''
Write-Host '[1] First plan call (cache MISS expected)'
Write-Host '  INFO  Using TESTSITE_EXPORT intent (supported)'

$body1 = '{"UserPrompt":"export data from testsite dashboard to csv file"}'
$r1 = Invoke-RestMethod -Uri "$base/planner/plan" -Method POST -Body $body1 -ContentType 'application/json'

Write-Host "  INFO  success=$($r1.success) intent=$($r1.intent)"
Check 'First call success=true' ($r1.success -eq $true)
Check 'First call: plan has steps' ($r1.plan -and $r1.plan.steps -and $r1.plan.steps.Count -gt 0)
Check 'First call: fromProcedureCache is false' ($r1.fromProcedureCache -eq $false)
Check 'First call: procedureId is set' (-not [string]::IsNullOrEmpty($r1.procedureId))
$pid1 = $r1.procedureId
Write-Host "  INFO  procedureId = $pid1"

Write-Host ''
Write-Host '[2] GET /procedures/{id} - procedure was saved'
if ([string]::IsNullOrEmpty($pid1)) {
    Write-Host '  SKIP  (no procedureId to look up)'
} else {
    $proc = Invoke-RestMethod -Uri "$base/procedures/$pid1" -Method GET
    Check 'Procedure exists in store' ($proc.id -eq $pid1)
    Check 'Procedure has intent' (-not [string]::IsNullOrEmpty($proc.intent))
    Check 'Procedure has keywords' ($proc.keywords -and $proc.keywords.Count -gt 0)
    Write-Host "  INFO  intent = '$($proc.intent)'"
    Write-Host "  INFO  keywords = $($proc.keywords -join ', ')"
}

Write-Host ''
Write-Host '[3] Second plan call - identical prompt (cache HIT expected)'
$body2 = '{"UserPrompt":"export data from testsite dashboard to csv file"}'
$r2 = Invoke-RestMethod -Uri "$base/planner/plan" -Method POST -Body $body2 -ContentType 'application/json'

Write-Host "  INFO  success=$($r2.success) fromCache=$($r2.fromProcedureCache) procedureId=$($r2.procedureId)"
Check 'Second call success=true' ($r2.success -eq $true)
Check 'Second call: fromProcedureCache is TRUE' ($r2.fromProcedureCache -eq $true)
Check 'Second call: same procedureId as first' ($r2.procedureId -eq $pid1)
$steps1 = if ($r1.plan) { $r1.plan.steps.Count } else { 0 }
$steps2 = if ($r2.plan) { $r2.plan.steps.Count } else { 0 }
Check 'Both plans have same step count' ($steps1 -eq $steps2)

Write-Host ''
Write-Host '[4] Similar prompt - keyword overlap check'
$body3 = '{"UserPrompt":"export testsite csv data"}'
$r3 = Invoke-RestMethod -Uri "$base/planner/plan" -Method POST -Body $body3 -ContentType 'application/json'
$similarHit = $r3.fromProcedureCache -eq $true
Write-Host "  INFO  fromProcedureCache=$($r3.fromProcedureCache)"
Check 'Similar prompt hits cache (keyword overlap)' $similarHit
if (-not $similarHit) { Write-Host '  INFO  (may be acceptable if keyword overlap < 0.60 threshold)' }

Write-Host ''
Write-Host '[5] Different intent - should NOT hit this cache entry'
$body4 = '{"UserPrompt":"login to the system with credentials"}'
$r4 = Invoke-RestMethod -Uri "$base/planner/plan" -Method POST -Body $body4 -ContentType 'application/json'
Write-Host "  INFO  fromProcedureCache=$($r4.fromProcedureCache) intent=$($r4.intent)"
Check 'LOGIN_FLOW intent is cache MISS for TESTSITE_EXPORT entry' ($r4.procedureId -ne $pid1)

Write-Host ''
Write-Host '[6] GET /procedures - list includes our procedure'
$list = Invoke-RestMethod -Uri "$base/procedures" -Method GET
$listCount = $list.count
Write-Host "  INFO  total procedures in store: $listCount"
Check 'GET /procedures returns count >= 1' ($listCount -ge 1)
$found = $list.procedures | Where-Object { $_.id -eq $pid1 }
Check 'Our procedure appears in list' (-not [string]::IsNullOrEmpty($pid1) -and $found -ne $null)

Write-Host ''
Write-Host '====================================================='
if ($fail -eq 0) { Write-Host "  ALL PASS  $pass/$($pass+$fail) passed" }
else             { Write-Host "  RESULT: $pass PASS, $fail FAIL" }
Write-Host '====================================================='
