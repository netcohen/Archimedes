# Phase 2: Mock messaging - Send message -> receive -> print
# Prereq: Start net and core first (see phase1-test.ps1 or run manually)

$ErrorActionPreference = "Stop"

Write-Host "Sending envelope via Core to Net..."
$r = Invoke-WebRequest -Uri "http://localhost:5051/send-envelope" -Method POST -Body "Test envelope from script" -ContentType "text/plain" -UseBasicParsing
if ($r.Content.Trim() -ne "OK") { throw "Send failed: $($r.Content)" }
Write-Host "  Send OK"

Write-Host "Receiving envelope from Net queue..."
$received = (Invoke-WebRequest -Uri "http://localhost:5052/envelope" -UseBasicParsing).Content.Trim()
if ($received -ne "Test envelope from script") { throw "Receive failed: got '$received'" }
Write-Host "  Received: $received"

Write-Host ""
Write-Host "Phase 2 self-test: PASS (Send -> Receive -> Print)"
