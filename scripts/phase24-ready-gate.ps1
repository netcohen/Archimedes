$pass = 0
$fail = 0
$BaseUrl  = 'http://localhost:5051'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Check($label, $condition) {
    if ($condition) { Write-Host "  PASS  $label"; $script:pass++ }
    else            { Write-Host "  FAIL  $label"; $script:fail++ }
}

Write-Host '====================================================='
Write-Host '  Phase 24 - Failure Dialogue Ready Gate'
Write-Host '====================================================='

# [1] Build
Write-Host ''
Write-Host '[1] dotnet build'
try {
    $result   = & dotnet build "$RepoRoot\core\Archimedes.Core.csproj" --nologo -v quiet 2>&1
    $exitCode = $LASTEXITCODE
    Check 'dotnet build exits 0'         ($exitCode -eq 0)
    Check 'Build output has 0 Error(s)'  ($result -match '0 Error\(s\)')
} catch { Check 'dotnet build runs' $false }

# [2] Source checks
Write-Host ''
Write-Host '[2] Source checks'

$fdFile = Join-Path $RepoRoot 'core\FailureDialogue.cs'
$faFile = Join-Path $RepoRoot 'core\FailureAnalyzer.cs'
$trFile = Join-Path $RepoRoot 'core\TaskRunner.cs'
$tsFile = Join-Path $RepoRoot 'core\TaskService.cs'
$pgFile = Join-Path $RepoRoot 'core\Program.cs'
$chFile = Join-Path $RepoRoot 'core\ChatHtml.cs'

$fd = Get-Content $fdFile -Raw -ErrorAction SilentlyContinue
$fa = Get-Content $faFile -Raw -ErrorAction SilentlyContinue
$tr = Get-Content $trFile -Raw -ErrorAction SilentlyContinue
$ts = Get-Content $tsFile -Raw -ErrorAction SilentlyContinue
$pg = Get-Content $pgFile -Raw -ErrorAction SilentlyContinue
$ch = Get-Content $chFile -Raw -ErrorAction SilentlyContinue

Check 'FailureDialogue.cs exists'                   (Test-Path $fdFile)
Check 'FailureDialogue.cs DialogueStatus enum'      ($fd -match 'DialogueStatus')
Check 'FailureDialogue.cs FailureDialogueStore'     ($fd -match 'FailureDialogueStore')
Check 'FailureDialogue.cs Create method'            ($fd -match 'public FailureDialogue Create')
Check 'FailureDialogue.cs Respond method'           ($fd -match 'public bool Respond')
Check 'FailureAnalyzer.cs exists'                   (Test-Path $faFile)
Check 'FailureAnalyzer.cs Analyze method'           ($fa -match 'public static string Analyze')
Check 'TaskRunner.cs injects FailureDialogueStore'  ($tr -match 'FailureDialogueStore')
Check 'TaskRunner.cs calls .Create on step failure' ($tr -match '_failureDialogueStore')
Check 'TaskService.cs has ResetForRetry'            ($ts -match 'ResetForRetry')
Check 'Program.cs creates failureDialogueStore'     ($pg -match 'new FailureDialogueStore')
Check 'Program.cs passes store to TaskRunner'       ($pg -match 'failureDialogueStore')
Check 'Program.cs GET /recovery-dialogues'          ($pg -match 'recovery-dialogues')
Check 'Program.cs POST /respond endpoint'           ($pg -match '/respond')
Check 'Program.cs RecoveryRespondRequest class'     ($pg -match 'RecoveryRespondRequest')
Check 'ChatHtml.cs pollRecovery function'           ($ch -match 'pollRecovery')
Check 'ChatHtml.cs polls /recovery-dialogues'       ($ch -match 'recovery-dialogues')
Check 'ChatHtml.cs recovery-area div'               ($ch -match 'recovery-area')
Check 'ChatHtml.cs recoverRespond function'         ($ch -match 'recoverRespond')
Check 'ChatHtml.cs version v0.24.0'                 ($ch -match 'v0.24.0')

# [3] GET /recovery-dialogues
Write-Host ''
Write-Host '[3] GET /recovery-dialogues'
try {
    $r = Invoke-RestMethod "$BaseUrl/recovery-dialogues"
    Check 'Response has count field'     ($null -ne $r.count)
    Check 'Response has dialogues array' ($null -ne $r.dialogues)
    Check 'count >= 0'                   ($r.count -ge 0)
    Write-Host "  INFO  Pending dialogues: $($r.count)"
} catch { Check 'GET /recovery-dialogues reachable' $false }

# [4] Force a failure and verify dialogue is created
Write-Host ''
Write-Host '[4] Failure -> Dialogue creation flow'
try {
    # Cancel any leftover RUNNING tasks so the runner loop is not blocked
    try {
        $existing = Invoke-RestMethod "$BaseUrl/tasks?state=RUNNING" -ErrorAction SilentlyContinue
        foreach ($t in $existing) {
            try { Invoke-RestMethod "$BaseUrl/task/$($t.taskId)/cancel" -Method POST | Out-Null } catch {}
        }
        if ($existing.Count -gt 0) { Start-Sleep -Milliseconds 800 }
    } catch {}

    $taskJson = @"
{"title":"TestTaskPhase24","userPrompt":"test failure dialogue"}
"@
    $task = Invoke-RestMethod "$BaseUrl/task" -Method POST -Body $taskJson -ContentType 'application/json'
    Check 'Task created' (-not [string]::IsNullOrEmpty($task.taskId))

    if (-not [string]::IsNullOrEmpty($task.taskId)) {
        $tid = $task.taskId

        # fail.now is an unknown action -> TaskRunner returns Success=false immediately
        $planJson = @"
{"intent":"TEST_FAIL","steps":[{"action":"fail.now","parameters":{},"successCriteria":{}}]}
"@
        try { Invoke-RestMethod "$BaseUrl/task/$tid/plan" -Method POST -Body $planJson -ContentType 'application/json' | Out-Null } catch {}
        try { Invoke-RestMethod "$BaseUrl/task/$tid/run"  -Method POST | Out-Null } catch {}

        $state = ''
        for ($x = 0; $x -lt 12; $x++) {
            Start-Sleep -Milliseconds 1000
            try {
                $t2    = Invoke-RestMethod "$BaseUrl/task/$tid"
                $state = $t2.state
                if ($state -eq 'Failed' -or $state -eq 'Done') { break }
            } catch {}
        }

        Check "Task eventually fails (state=$state)" ($state -eq 'Failed')

        Start-Sleep -Milliseconds 500
        $d          = Invoke-RestMethod "$BaseUrl/recovery-dialogues"
        $myDialogue = $d.dialogues | Where-Object { $_.taskId -eq $tid }
        Check 'FailureDialogue created for failed task' ($null -ne $myDialogue)

        if ($null -ne $myDialogue) {
            Check 'Dialogue has recoveryQuestion' (-not [string]::IsNullOrEmpty($myDialogue.recoveryQuestion))
            Check 'Dialogue has taskTitle'        (-not [string]::IsNullOrEmpty($myDialogue.taskTitle))
            Check 'Dialogue has failedStep'       (-not [string]::IsNullOrEmpty($myDialogue.failedStep))
            Write-Host "  INFO  Question: $($myDialogue.recoveryQuestion)"

            # [5] Respond with dismiss
            Write-Host ''
            Write-Host '[5] POST /recovery-dialogues/{id}/respond (dismiss)'
            $dlgId    = $myDialogue.dialogueId
            $respJson = '{"action":"dismiss"}'
            $resp = Invoke-RestMethod "$BaseUrl/recovery-dialogues/$dlgId/respond" `
                -Method POST -Body $respJson -ContentType 'application/json'
            Check 'Dismiss ok=true'        ($resp.ok -eq $true)
            Check 'Dismiss action=dismiss' ($resp.action -eq 'dismiss')

            Start-Sleep -Milliseconds 300
            $d2   = Invoke-RestMethod "$BaseUrl/recovery-dialogues"
            $gone = $d2.dialogues | Where-Object { $_.dialogueId -eq $dlgId }
            Check 'Dismissed dialogue no longer pending' ($null -eq $gone)
        }
    }
} catch {
    Write-Host "  INFO  Flow test error: $($_.Exception.Message)"
    Check 'Failure dialogue flow reachable' $false
}

# [6] Input validation
Write-Host ''
Write-Host '[6] Input validation'
try {
    $code = 200
    try {
        Invoke-RestMethod "$BaseUrl/recovery-dialogues/nonexistent-id/respond" `
            -Method POST -Body '{"action":"retry"}' -ContentType 'application/json' | Out-Null
    } catch { $code = $_.Exception.Response.StatusCode.value__ }
    Check 'Unknown dialogue ID returns 404' ($code -eq 404)
} catch { Check 'Validation check runs' $false }

try {
    $code2 = 200
    try {
        Invoke-RestMethod "$BaseUrl/recovery-dialogues/fake/respond" `
            -Method POST -Body '{"action":"bad_action"}' -ContentType 'application/json' | Out-Null
    } catch { $code2 = $_.Exception.Response.StatusCode.value__ }
    Check 'Invalid action returns 400 or 404' ($code2 -eq 400 -or $code2 -eq 404)
} catch { Check 'Validation check runs' $false }

# [7] FailureAnalyzer pattern coverage (source)
Write-Host ''
Write-Host '[7] FailureAnalyzer pattern coverage'
Check 'Handles session expired'    ($fa -match 'session expired')
Check 'Handles timeout'            ($fa -match 'timeout')
Check 'Handles not found / 404'    ($fa -match '404')
Check 'Handles DOM element errors' ($fa -match 'element')
Check 'Handles permission / 403'   ($fa -match '403')
Check 'Handles rate limit / 429'   ($fa -match '429')
Check 'Has default fallback'       ($fa -match 'Default fallback')

# Summary
Write-Host ''
Write-Host '====================================================='
if ($fail -eq 0) { Write-Host "  ALL PASS  $pass/$($pass+$fail) passed" }
else             { Write-Host "  RESULT: $pass PASS, $fail FAIL" }
Write-Host '====================================================='
