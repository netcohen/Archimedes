#!/usr/bin/env pwsh
# Phase 14.2 Soak Test
# Long-running stability test for Archimedes Core + Net
# Default: 12 hours, configurable via -DurationHours parameter

param(
    [int]$DurationHours = 12,
    [int]$TaskIntervalMinutes = 2,
    [int]$HealthCheckMinutes = 5,
    [int]$StuckTaskDeadlineMinutes = 15,
    [switch]$SimulateFailures = $false,
    [string]$LogDir = "$PSScriptRoot/../logs/soak"
)

$ErrorActionPreference = "Continue"
$coreUrl = if ($env:ARCHIMEDES_CORE_URL) { $env:ARCHIMEDES_CORE_URL } else { "http://localhost:5051" }
$netUrl = if ($env:ARCHIMEDES_NET_URL) { $env:ARCHIMEDES_NET_URL } else { "http://localhost:5052" }

# Create log directory
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$logFile = "$LogDir/soak-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
$summaryFile = "$LogDir/soak-summary-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"

function Log {
    param($Message, $Level = "INFO")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$timestamp] [$Level] $Message"
    Write-Host $line -ForegroundColor $(switch ($Level) {
        "ERROR" { "Red" }
        "WARN" { "Yellow" }
        "SUCCESS" { "Green" }
        default { "White" }
    })
    Add-Content -Path $logFile -Value $line
}

function Check-Health {
    try {
        $deep = Invoke-RestMethod -Uri "$coreUrl/health/deep" -TimeoutSec 10
        $running = Invoke-RestMethod -Uri "$coreUrl/tasks/running" -TimeoutSec 10
        
        return @{
            Ok = $true
            RunnerRunning = $deep.runner.running
            WatchdogEnabled = $deep.runner.watchdogEnabled
            HeartbeatAge = $deep.runner.heartbeatAgeSeconds
            TotalTicks = $deep.runner.totalTicksProcessed
            TotalSteps = $deep.runner.totalStepsExecuted
            RunningTasks = $running.count
            NetHealthy = $deep.net.healthy
        }
    } catch {
        return @{
            Ok = $false
            Error = $_.Exception.Message
        }
    }
}

function Dump-Diagnostics {
    param([string]$Reason)
    Log "=== DIAGNOSTICS: $Reason ===" "ERROR"
    try {
        $deep = Invoke-RestMethod -Uri "$coreUrl/health/deep" -TimeoutSec 10
        Log "/health/deep: $($deep | ConvertTo-Json -Depth 5 -Compress)" "ERROR"
    } catch {
        Log "/health/deep failed: $($_.Exception.Message)" "ERROR"
    }
    try {
        $running = Invoke-RestMethod -Uri "$coreUrl/tasks/running" -TimeoutSec 10
        Log "/tasks/running: $($running | ConvertTo-Json -Depth 5 -Compress)" "ERROR"
        foreach ($t in $running.tasks) {
            try {
                $trace = Invoke-RestMethod -Uri "$coreUrl/task/$($t.taskId)/trace" -TimeoutSec 5
                Log "/task/$($t.taskId)/trace: $($trace | ConvertTo-Json -Depth 3 -Compress)" "ERROR"
            } catch {
                Log "/task/$($t.taskId)/trace failed: $($_.Exception.Message)" "ERROR"
            }
        }
    } catch {
        Log "/tasks/running failed: $($_.Exception.Message)" "ERROR"
    }
}

function Create-TestTask {
    param($Type = "quick")
    
    $prompts = @{
        "quick" = "Login to the local testsite and download the CSV, then summarize the first 3 rows."
        "monitor" = "Monitor the local testsite dashboard every 30 seconds and report changes."
    }
    
    $body = @{
        Title = "Soak Test - $Type - $(Get-Date -Format 'HH:mm:ss')"
        UserPrompt = $prompts[$Type]
        Type = if ($Type -eq "monitor") { "MONITORING" } else { "ONE_SHOT" }
    } | ConvertTo-Json
    
    try {
        $task = Invoke-RestMethod -Uri "$coreUrl/task" -Method POST -Body $body -ContentType "application/json" -TimeoutSec 10
        Invoke-RestMethod -Uri "$coreUrl/task/$($task.taskId)/run" -Method POST -TimeoutSec 10 | Out-Null
        return @{ Success = $true; TaskId = $task.taskId }
    } catch {
        return @{ Success = $false; Error = $_.Exception.Message }
    }
}

# ========== Main ==========
Log "=== Archimedes Soak Test Started ===" "INFO"
Log "Duration: $DurationHours hours"
Log "Task interval: $TaskIntervalMinutes minutes"
Log "Health check interval: $HealthCheckMinutes minutes"
Log "Log file: $logFile"
Log ""

$startTime = Get-Date
$endTime = $startTime.AddHours($DurationHours)

# Baseline: capture task counts at start (for delta accounting)
$baseline = @{ created = 0; completed = 0; failed = 0 }
try {
    $allTasks = @(Invoke-RestMethod -Uri "$coreUrl/tasks" -Method Get -TimeoutSec 10)
    $baseline.created = $allTasks.Count
    $baseline.completed = ($allTasks | Where-Object { $_.state -eq "DONE" }).Count
    $baseline.failed = ($allTasks | Where-Object { $_.state -eq "FAILED" }).Count
} catch {
    Log "Could not capture baseline task counts: $($_.Exception.Message)" "WARN"
}

$stats = @{
    HealthChecks = 0
    HealthChecksFailed = 0
    MaxRunningTasks = 0
    Errors = @()
}

$lastTaskTime = [DateTime]::MinValue
$lastHealthTime = [DateTime]::MinValue
$taskIds = @()

Log "Soak test running until $(Get-Date $endTime -Format 'yyyy-MM-dd HH:mm:ss')..." "INFO"

while ((Get-Date) -lt $endTime) {
    $now = Get-Date
    
    # Create task every N minutes
    if (($now - $lastTaskTime).TotalMinutes -ge $TaskIntervalMinutes) {
        $type = if ($taskIds.Count % 5 -eq 0) { "monitor" } else { "quick" }
        $result = Create-TestTask -Type $type
        
        if ($result.Success) {
            $taskIds += $result.TaskId
            Log "Task created: $($result.TaskId) ($type)" "SUCCESS"
        } else {
            $stats.Errors += "Task creation failed: $($result.Error)"
            Log "Task creation failed: $($result.Error)" "ERROR"
        }
        
        $lastTaskTime = $now
    }
    
    # Health check every N minutes
    if (($now - $lastHealthTime).TotalMinutes -ge $HealthCheckMinutes) {
        $health = Check-Health
        $stats.HealthChecks++
        
        if ($health.Ok) {
            if ($health.RunningTasks -gt $stats.MaxRunningTasks) {
                $stats.MaxRunningTasks = $health.RunningTasks
            }
            
            Log "Health OK: runner=$($health.RunnerRunning), running=$($health.RunningTasks), ticks=$($health.TotalTicks), steps=$($health.TotalSteps)"
            
            # Fail fast: check for stuck RUNNING tasks
            $stuckDeadlineSeconds = $StuckTaskDeadlineMinutes * 60
            try {
                $runningData = Invoke-RestMethod -Uri "$coreUrl/tasks/running" -TimeoutSec 10
                $stuckTasks = @($runningData.tasks | Where-Object { $_.lastUpdateSeconds -gt $stuckDeadlineSeconds })
                if ($stuckTasks.Count -gt 0) {
                    Log "FAIL: $($stuckTasks.Count) stuck task(s) (no progress for >$StuckTaskDeadlineMinutes min)" "ERROR"
                    Dump-Diagnostics -Reason "Stuck RUNNING tasks: $($stuckTasks.taskId -join ', ')"
                    exit 1
                }
            } catch {
                Log "WARNING: Could not check for stuck tasks: $($_.Exception.Message)" "WARN"
            }
            
            # Check for many running tasks (possible backlog)
            if ($health.RunningTasks -gt 10) {
                Log "WARNING: $($health.RunningTasks) tasks running - possible stuck tasks" "WARN"
            }
            
            # Check heartbeat age
            if ($health.HeartbeatAge -gt 60) {
                Log "WARNING: Runner heartbeat age $($health.HeartbeatAge)s" "WARN"
            }
        } else {
            $stats.HealthChecksFailed++
            $stats.Errors += "Health check failed: $($health.Error)"
            Log "Health check FAILED: $($health.Error)" "ERROR"
        }
        
        $lastHealthTime = $now
    }
    
    # Simulate failure (optional)
    if ($SimulateFailures -and (Get-Random -Maximum 100) -lt 1) {
        Log "Simulating transient Net failure..." "WARN"
        # This would stop Net for 30s - disabled by default
    }
    
    Start-Sleep -Seconds 30
}

# ========== Summary ==========
$duration = (Get-Date) - $startTime

# Compute delta from our created tasks only (internally consistent)
$deltaCreated = $taskIds.Count
$deltaCompleted = 0
$deltaFailed = 0
foreach ($tid in $taskIds) {
    try {
        $task = Invoke-RestMethod -Uri "$coreUrl/task/$tid" -TimeoutSec 5
        if ($task.state -eq "DONE") { $deltaCompleted++ }
        elseif ($task.state -eq "FAILED") { $deltaFailed++ }
    } catch { }
}

# Invariants: deltaCompleted + deltaFailed <= deltaCreated; deltaFailed == 0 for PASS
$deltaStillPending = $deltaCreated - $deltaCompleted - $deltaFailed

Log ""
Log "=== Soak Test Complete ===" "INFO"
Log "Duration: $($duration.TotalHours.ToString('F1')) hours"
Log "Delta (this run): created=$deltaCreated, completed=$deltaCompleted, failed=$deltaFailed, pending=$deltaStillPending"
Log "Health checks: $($stats.HealthChecks) (failed: $($stats.HealthChecksFailed))"
Log "Max running tasks: $($stats.MaxRunningTasks)"
Log "Errors: $($stats.Errors.Count)"

# Write summary JSON with clear delta/absolute labels
$summaryData = @{
    StartTime = $startTime.ToString("o")
    EndTime = (Get-Date).ToString("o")
    DurationHours = $duration.TotalHours
    Baseline = @{
        type = "absolute"
        totalTasks = $baseline.created
        completedTasks = $baseline.completed
        failedTasks = $baseline.failed
    }
    Delta = @{
        type = "delta"
        created = $deltaCreated
        completed = $deltaCompleted
        failed = $deltaFailed
        pending = $deltaStillPending
    }
    HealthChecks = $stats.HealthChecks
    HealthChecksFailed = $stats.HealthChecksFailed
    MaxRunningTasks = $stats.MaxRunningTasks
    Errors = $stats.Errors
} | ConvertTo-Json -Depth 5
Set-Content -Path $summaryFile -Value $summaryData

Log ""
Log "Summary written to: $summaryFile"

# PASS requires: deltaFailed == 0, and no health check failures
if ($stats.HealthChecksFailed -eq 0 -and $deltaFailed -eq 0) {
    Log "PASS: Soak test passed" "SUCCESS"
    exit 0
} else {
    Log "FAIL: Soak test had issues (deltaFailed=$deltaFailed, healthChecksFailed=$($stats.HealthChecksFailed))" "ERROR"
    exit 1
}
