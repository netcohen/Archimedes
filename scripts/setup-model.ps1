#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the LLamaSharp GGUF model for Archimedes (one-time setup).
.DESCRIPTION
    Downloads Llama-3.2-3B-Instruct-Q4_K_M.gguf (~2GB) from HuggingFace
    and saves it to %LOCALAPPDATA%\Archimedes\models\llama3.2-3b.gguf
    Idempotent: skips download if file already exists and is valid size.
#>

$modelDir  = Join-Path $env:LOCALAPPDATA "Archimedes\models"
$modelPath = Join-Path $modelDir "llama3.2-3b.gguf"
$modelUrl  = "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf"
$minSizeGB = 1.5

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Archimedes -- LLM Model Setup            " -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Target : $modelPath" -ForegroundColor Gray
Write-Host "  Model  : Llama-3.2-3B-Instruct Q4_K_M" -ForegroundColor Gray
Write-Host "  Size   : ~2 GB" -ForegroundColor Gray
Write-Host ""

if (Test-Path $modelPath) {
    $existingSize = (Get-Item $modelPath).Length
    $existingGB   = [math]::Round($existingSize / 1GB, 2)
    if ($existingGB -ge $minSizeGB) {
        Write-Host "  Model already exists ($existingGB GB) -- skipping download." -ForegroundColor Green
        Write-Host ""
        Write-Host "  ARCHIMEDES_MODEL_PATH=$modelPath" -ForegroundColor Yellow
        Write-Host ""
        exit 0
    } else {
        Write-Host "  Existing file too small ($existingGB GB) -- re-downloading." -ForegroundColor Yellow
        Remove-Item $modelPath -Force
    }
}

if (-not (Test-Path $modelDir)) {
    New-Item -ItemType Directory -Path $modelDir -Force | Out-Null
    Write-Host "  Created directory: $modelDir" -ForegroundColor Gray
}

Write-Host "  Downloading... (this may take 5-20 minutes)" -ForegroundColor Cyan
$startTime = Get-Date

try {
    $wc = New-Object System.Net.WebClient
    $wc.Headers.Add("User-Agent", "Archimedes/1.0")

    $lastPct = -1
    Register-ObjectEvent -InputObject $wc -EventName DownloadProgressChanged -Action {
        $pct    = $Event.SourceEventArgs.ProgressPercentage
        $mbDown = [math]::Round($Event.SourceEventArgs.BytesReceived / 1MB, 0)
        $mbTot  = [math]::Round($Event.SourceEventArgs.TotalBytesToReceive / 1MB, 0)
        if ($pct % 10 -eq 0 -and $pct -ne $script:lastPct) {
            $script:lastPct = $pct
            Write-Host ("    " + $pct + "%  (" + $mbDown + " / " + $mbTot + " MB)") -ForegroundColor Gray
        }
    } | Out-Null

    $wc.DownloadFile($modelUrl, $modelPath)

    $elapsed = (Get-Date) - $startTime
    $mins    = [math]::Round($elapsed.TotalMinutes, 1)
    $sizeGB  = [math]::Round((Get-Item $modelPath).Length / 1GB, 2)
    Write-Host ""
    Write-Host ("  Downloaded: " + $sizeGB + " GB in " + $mins + " minutes") -ForegroundColor Green
}
catch {
    Write-Host ("  Download failed: " + $_.Exception.Message) -ForegroundColor Red
    if (Test-Path $modelPath) { Remove-Item $modelPath -Force }
    exit 1
}

$finalSize = (Get-Item $modelPath).Length
$finalGB   = [math]::Round($finalSize / 1GB, 2)
if ($finalGB -lt $minSizeGB) {
    Write-Host ("  ERROR: File too small (" + $finalGB + " GB). Corrupt download?") -ForegroundColor Red
    Remove-Item $modelPath -Force
    exit 1
}

Write-Host ""
Write-Host ("  Model ready: " + $modelPath) -ForegroundColor Green
Write-Host ""
Write-Host "  SETUP COMPLETE" -ForegroundColor Green
Write-Host ""
