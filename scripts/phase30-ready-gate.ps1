# Phase 30 — Ubuntu OS Autonomy Gate
# Usage: .\scripts\phase30-ready-gate.ps1
# Expected: All assertions PASS

param([string]$BaseUrl = "http://localhost:5051")

$pass = 0; $fail = 0
$coreDir    = "$PSScriptRoot\..\core"
$scriptsDir = "$PSScriptRoot"

function Assert([bool]$ok, [string]$label) {
    if ($ok) { Write-Host "  PASS  $label" -ForegroundColor Green; $script:pass++ }
    else      { Write-Host "  FAIL  $label" -ForegroundColor Red;   $script:fail++ }
}

function Get-Json([string]$url) {
    try { Invoke-RestMethod $url -TimeoutSec 10 } catch { $null }
}

function Post-Json([string]$url, [string]$body = "", [string]$ct = "application/json") {
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
        Invoke-RestMethod $url -Method POST -Body $bytes -ContentType $ct -TimeoutSec 15
    } catch { $null }
}

function Delete-Url([string]$url) {
    try { Invoke-RestMethod $url -Method DELETE -TimeoutSec 10 } catch { $null }
}

Write-Host "`n=== Phase 30 — Ubuntu OS Autonomy Gate ===" -ForegroundColor Cyan

# ── Section 1: Build + Health ─────────────────────────────────────────────────
Write-Host "`n[Section 1] Build + Health" -ForegroundColor Yellow

$buildOut = dotnet build "$coreDir\Archimedes.Core.csproj" --no-incremental -v q 2>&1
$buildOk  = ($LASTEXITCODE -eq 0) -and ($buildOut -notmatch "error CS")
Assert $buildOk "dotnet build — 0 errors"

$health = Get-Json "$BaseUrl/health"
Assert ($health -ne $null)                                                  "/health returns 200"
Assert ($health -match "OK|ok|true" -or $health.status -match "OK|ok")     "/health returns OK"

# ── Section 2: OS Status Endpoints ───────────────────────────────────────────
Write-Host "`n[Section 2] OS Status Endpoints" -ForegroundColor Yellow

$osStatus = Get-Json "$BaseUrl/os/status"
Assert ($osStatus -ne $null)                            "GET /os/status returns 200"
Assert ($null -ne $osStatus.state)                      "os/status has state field"
Assert ($null -ne $osStatus.isLinux)                    "os/status has isLinux field"
Assert ($null -ne $osStatus.rebootRequired)             "os/status has rebootRequired field"
Assert ($null -ne $osStatus.apt)                        "os/status has apt field"
Assert ($null -ne $osStatus.hardware)                   "os/status has hardware field"
Assert ($null -ne $osStatus.firewall)                   "os/status has firewall field"

$hw = Get-Json "$BaseUrl/os/hardware"
Assert ($hw -ne $null)                                  "GET /os/hardware returns 200"
Assert ($null -ne $hw.cpuTempCelsius)                   "hardware has cpuTempCelsius"
Assert ($null -ne $hw.ramUsedMb)                        "hardware has ramUsedMb"
Assert ($null -ne $hw.disks)                            "hardware has disks array"

$rebootReq = Get-Json "$BaseUrl/os/reboot/required"
Assert ($rebootReq -ne $null)                           "GET /os/reboot/required returns 200"
Assert ($null -ne $rebootReq.required)                  "reboot/required has required field"

$rebootSched = Get-Json "$BaseUrl/os/reboot/scheduled"
Assert ($rebootSched -ne $null)                         "GET /os/reboot/scheduled returns 200"

$schedResult = Post-Json "$BaseUrl/os/reboot/schedule" '{"reason":"gate test"}'
Assert ($schedResult -ne $null)                         "POST /os/reboot/schedule returns 200"
Assert ($schedResult.ok -eq $true)                      "reboot/schedule returns ok=true"
Assert ($null -ne $schedResult.scheduledAt)             "reboot/schedule returns scheduledAt"

$cancelResult = Delete-Url "$BaseUrl/os/reboot/schedule"
Assert ($cancelResult -ne $null)                        "DELETE /os/reboot/schedule returns 200"
Assert ($cancelResult.ok -eq $true)                     "reboot/schedule cancel returns ok=true"

$mw = Get-Json "$BaseUrl/os/maintenance-window"
Assert ($mw -ne $null)                                  "GET /os/maintenance-window returns 200"
Assert ($null -ne $mw.startHour)                        "maintenance-window has startHour"
Assert ($null -ne $mw.endHour)                          "maintenance-window has endHour"
Assert ($null -ne $mw.activeDays)                       "maintenance-window has activeDays"

$mwSet = Post-Json "$BaseUrl/os/maintenance-window" '{"startHour":3,"startMinute":0,"endHour":4,"endMinute":0,"activeDays":[true,true,true,true,true,true,false]}'
Assert ($mwSet -ne $null)                               "POST /os/maintenance-window returns 200"
Assert ($mwSet.ok -eq $true)                            "maintenance-window set returns ok=true"

$fwRules = Get-Json "$BaseUrl/os/firewall/rules"
Assert ($fwRules -ne $null)                             "GET /os/firewall/rules returns 200"
Assert ($null -ne $fwRules.enabled)                     "firewall/rules has enabled field"
Assert ($null -ne $fwRules.rules)                       "firewall/rules has rules array"

$fwAdd = Post-Json "$BaseUrl/os/firewall/rule" '{"port":"5051","protocol":"tcp","action":"allow","comment":"archimedes-api"}'
Assert ($fwAdd -ne $null)                               "POST /os/firewall/rule returns 200"
Assert ($fwAdd.ok -eq $true)                            "firewall/rule add returns ok=true"

$aptUpdate = Post-Json "$BaseUrl/os/apt/update"
Assert ($aptUpdate -ne $null)                           "POST /os/apt/update returns 200"
Assert ($null -ne $aptUpdate.success)                   "apt/update has success field"
Assert ($null -ne $aptUpdate.output)                    "apt/update has output field"
Assert ($aptUpdate.success -eq $true)                   "apt/update succeeds (or dry-run on Windows)"

$aptUpgrade = Post-Json "$BaseUrl/os/apt/upgrade"
Assert ($aptUpgrade -ne $null)                          "POST /os/apt/upgrade returns 200"
Assert ($aptUpgrade.success -eq $true)                  "apt/upgrade succeeds (or dry-run on Windows)"

$aptAutoremove = Post-Json "$BaseUrl/os/apt/autoremove"
Assert ($aptAutoremove -ne $null)                       "POST /os/apt/autoremove returns 200"
Assert ($aptAutoremove.success -eq $true)               "apt/autoremove succeeds (or dry-run on Windows)"

$cleanup = Post-Json "$BaseUrl/os/logs/cleanup" '{"keepDays":30}'
Assert ($cleanup -ne $null)                             "POST /os/logs/cleanup returns 200"
Assert ($null -ne $cleanup.deletedFiles)                "logs/cleanup has deletedFiles field"
Assert ($null -ne $cleanup.freedMb)                     "logs/cleanup has freedMb field"

# ── Section 3: /status/current has osHealth ───────────────────────────────────
Write-Host "`n[Section 3] /status/current includes osHealth" -ForegroundColor Yellow

$statusCurrent = Get-Json "$BaseUrl/status/current"
Assert ($statusCurrent -ne $null)                       "GET /status/current returns 200"
Assert ($null -ne $statusCurrent.osHealth)              "status/current has osHealth field"
Assert ($null -ne $statusCurrent.osHealth.isLinux)      "osHealth has isLinux"
Assert ($null -ne $statusCurrent.osHealth.rebootRequired) "osHealth has rebootRequired"
Assert ($null -ne $statusCurrent.osHealth.state)        "osHealth has state"

# ── Section 4: Source Code Files ─────────────────────────────────────────────
Write-Host "`n[Section 4] Source Code" -ForegroundColor Yellow

Assert (Test-Path "$coreDir\OsManager.cs")              "OsManager.cs exists"
Assert (Test-Path "$coreDir\OsModels.cs")               "OsModels.cs exists"
Assert (Test-Path "$coreDir\HardwareMonitor.cs")        "HardwareMonitor.cs exists"
Assert (Test-Path "$coreDir\AptManager.cs")             "AptManager.cs exists"
Assert (Test-Path "$scriptsDir\archimedes.service")     "archimedes.service exists"
Assert (Test-Path "$scriptsDir\install-service.sh")     "install-service.sh exists"

$eng = Get-Content "$coreDir\OsManager.cs" -Raw
Assert ($eng -match "public void Start\(\)")            "OsManager has Start()"
Assert ($eng -match "public void Stop\(\)")             "OsManager has Stop()"
Assert ($eng -match "MaintenanceWindow")                "OsManager uses MaintenanceWindow"
Assert ($eng -match "IsNow\(\)")                        "OsManager calls IsNow()"
Assert ($eng -match "NextWindow\(\)")                   "OsManager calls NextWindow()"
Assert ($eng -match "IsRebootRequired")                 "OsManager has IsRebootRequired"
Assert ($eng -match "ScheduleReboot")                   "OsManager has ScheduleReboot"
Assert ($eng -match "CleanLogs")                        "OsManager has CleanLogs"
Assert ($eng -match "GetFirewallStatus")                "OsManager has GetFirewallStatus"
Assert ($eng -match "AddFirewallRule")                  "OsManager has AddFirewallRule"
Assert ($eng -match "RebootNowAsync")                   "OsManager has RebootNowAsync"

$models = Get-Content "$coreDir\OsModels.cs" -Raw
Assert ($models -match "OsManagerState")                "Models has OsManagerState enum"
Assert ($models -match "MaintenanceWindow")             "Models has MaintenanceWindow"
Assert ($models -match "RebootSchedule")                "Models has RebootSchedule"
Assert ($models -match "HardwareMetrics")               "Models has HardwareMetrics"
Assert ($models -match "AptStatus")                     "Models has AptStatus"
Assert ($models -match "FirewallStatus")                "Models has FirewallStatus"
Assert ($models -match "DiskMetric")                    "Models has DiskMetric"

$hw2 = Get-Content "$coreDir\HardwareMonitor.cs" -Raw
Assert ($hw2 -match "GetCpuTemperature")                "HardwareMonitor has GetCpuTemperature"
Assert ($hw2 -match "thermal_zone")                     "HardwareMonitor reads thermal_zone (Linux)"
Assert ($hw2 -match "/proc/meminfo")                    "HardwareMonitor reads /proc/meminfo"
Assert ($hw2 -match "DriveInfo")                        "HardwareMonitor reads disk info"

$apt = Get-Content "$coreDir\AptManager.cs" -Raw
Assert ($apt -match "UpdateAsync")                      "AptManager has UpdateAsync"
Assert ($apt -match "UpgradeAsync")                     "AptManager has UpgradeAsync"
Assert ($apt -match "AutoremoveAsync")                  "AptManager has AutoremoveAsync"
Assert ($apt -match "non-Linux dry-run")                "AptManager has dry-run mode for non-Linux"

$svc = Get-Content "$scriptsDir\archimedes.service" -Raw
Assert ($svc -match "\[Unit\]")                         "archimedes.service has [Unit]"
Assert ($svc -match "\[Service\]")                      "archimedes.service has [Service]"
Assert ($svc -match "\[Install\]")                      "archimedes.service has [Install]"
Assert ($svc -match "Restart=always")                   "archimedes.service has Restart=always"
Assert ($svc -match "RestartSec=")                      "archimedes.service has RestartSec"
Assert ($svc -match "ExecStart.*dotnet")                "archimedes.service ExecStart uses dotnet"
Assert ($svc -match "WantedBy=multi-user.target")       "archimedes.service WantedBy=multi-user.target"
Assert ($svc -match "StartLimitBurst")                  "archimedes.service has StartLimitBurst"

$install = Get-Content "$scriptsDir\install-service.sh" -Raw
Assert ($install -match "sudoers")                      "install-service.sh configures sudoers"
Assert ($install -match "apt-get")                      "install-service.sh grants apt-get sudo"
Assert ($install -match "ufw")                          "install-service.sh grants ufw sudo"
Assert ($install -match "reboot")                       "install-service.sh grants reboot sudo"
Assert ($install -match "systemctl enable")             "install-service.sh enables service"
Assert ($install -match "useradd")                      "install-service.sh creates system user"

# ── Section 5: Program.cs Wiring ─────────────────────────────────────────────
Write-Host "`n[Section 5] Program.cs Wiring" -ForegroundColor Yellow

$prog = Get-Content "$coreDir\Program.cs" -Raw
Assert ($prog -match "new OsManager\(")                 "Program.cs creates OsManager"
Assert ($prog -match "new HardwareMonitor\(")           "Program.cs creates HardwareMonitor"
Assert ($prog -match "new AptManager\(")                "Program.cs creates AptManager"
Assert ($prog -match "osManager\.Start\(\)")            "Program.cs starts OsManager"
Assert ($prog -match "osManager\.Stop\(\)")             "Program.cs stops OsManager on shutdown"
Assert ($prog -match '"/os/status"')                    "Program.cs has /os/status endpoint"
Assert ($prog -match '"/os/hardware"')                  "Program.cs has /os/hardware endpoint"
Assert ($prog -match '"/os/apt/update"')                "Program.cs has /os/apt/update endpoint"
Assert ($prog -match '"/os/apt/upgrade"')               "Program.cs has /os/apt/upgrade endpoint"
Assert ($prog -match '"/os/reboot/schedule"')           "Program.cs has /os/reboot/schedule endpoint"
Assert ($prog -match '"/os/maintenance-window"')        "Program.cs has /os/maintenance-window endpoint"
Assert ($prog -match '"/os/firewall/rule"')             "Program.cs has /os/firewall/rule endpoint"
Assert ($prog -match '"/os/logs/cleanup"')              "Program.cs has /os/logs/cleanup endpoint"
Assert ($prog -match "osHealth")                        "Program.cs includes osHealth in /status/current"

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host "`n$(('─' * 50))" -ForegroundColor DarkGray
$total = $pass + $fail
if ($fail -eq 0) {
    Write-Host "  Phase 30 Gate: $pass/$total PASS" -ForegroundColor Green
} else {
    Write-Host "  Phase 30 Gate: $pass/$total PASS" -ForegroundColor Yellow
    Write-Host "  $fail assertion(s) FAILED"        -ForegroundColor Red
}
Write-Host "$(('─' * 50))`n" -ForegroundColor DarkGray

exit $fail
