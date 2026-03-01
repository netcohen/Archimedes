$drive = Get-PSDrive C
$freeGB = [math]::Round($drive.Free / 1GB, 1)
$usedGB = [math]::Round($drive.Used / 1GB, 1)
Write-Host "Drive C:  Free = $freeGB GB  |  Used = $usedGB GB"
