using System.Net.Http.Headers;

namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Copies the migration package to the target.
///
/// LOCAL_PATH → File.Copy zip + write restore-archimedes.ps1 helper script
/// HTTP_URL   → POST zip as multipart to {target}/migration/receive
/// </summary>
public class MigrationDeployer
{
    private readonly HttpClient _http;

    public MigrationDeployer(HttpClient http) => _http = http;

    // ── Main entry ─────────────────────────────────────────────────────────

    public async Task<bool> DeployAsync(
        MigrationPlan     plan,
        CancellationToken ct = default)
    {
        if (plan.PackagePath == null || !File.Exists(plan.PackagePath))
        {
            ArchLogger.LogWarn("[Deployer] Package file not found — cannot deploy");
            return false;
        }

        ArchLogger.LogInfo(
            $"[Deployer] Deploying {plan.PackagePath} → {plan.TargetPath} " +
            $"(type={plan.TargetType})");

        try
        {
            return plan.TargetType == MigrationTargetType.HTTP_URL
                ? await DeployHttpAsync(plan, ct)
                : DeployLocalPath(plan);
        }
        catch (Exception ex)
        {
            plan.Error = $"Deploy failed: {ex.Message}";
            ArchLogger.LogWarn($"[Deployer] {plan.Error}");
            return false;
        }
    }

    // ── Local / UNC path ───────────────────────────────────────────────────

    private static bool DeployLocalPath(MigrationPlan plan)
    {
        try { Directory.CreateDirectory(plan.TargetPath); } catch { }

        // Copy zip
        var destZip = Path.Combine(plan.TargetPath,
            Path.GetFileName(plan.PackagePath!));
        File.Copy(plan.PackagePath!, destZip, overwrite: true);

        // Drop a PowerShell restore helper
        var scriptPath = Path.Combine(plan.TargetPath, "restore-archimedes.ps1");
        File.WriteAllText(scriptPath, BuildRestoreScript(destZip));

        ArchLogger.LogInfo(
            $"[Deployer] Copied to {destZip} + restore script at {scriptPath}");
        return true;
    }

    // ── HTTP target ────────────────────────────────────────────────────────

    private async Task<bool> DeployHttpAsync(MigrationPlan plan, CancellationToken ct)
    {
        var url = $"{plan.TargetPath.TrimEnd('/')}/migration/receive";

        using var content = new MultipartFormDataContent();
        var fileBytes   = await File.ReadAllBytesAsync(plan.PackagePath!, ct);
        var byteContent = new ByteArrayContent(fileBytes);
        byteContent.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        content.Add(byteContent, "package",
            Path.GetFileName(plan.PackagePath!));

        using var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();

        ArchLogger.LogInfo($"[Deployer] Package delivered via HTTP to {url}");
        return true;
    }

    // ── Restore script ─────────────────────────────────────────────────────

    private static string BuildRestoreScript(string zipPath)
    {
        // Use $$"""...""" (double-dollar raw string):
        //   {{expr}} = C# interpolation,  { and } alone = literal PowerShell braces
        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        return
$$"""
# Archimedes Migration Restore Script
# Generated : {{ts}} UTC
# Run this on the target machine BEFORE starting Archimedes.

param(
    [string]$ZipPath = "{{zipPath}}"
)

$dataRoot   = "$env:LOCALAPPDATA\Archimedes"
$backupRoot = "$env:LOCALAPPDATA\Archimedes_bak_$(Get-Date -Format yyyyMMddHHmmss)"
$tempDir    = "$env:TEMP\arch_restore_$(New-Guid)"

Write-Host "Archimedes migration restore starting..."

# 1. Back up existing data
if (Test-Path $dataRoot) {
    Write-Host "Backing up existing data to $backupRoot"
    Copy-Item -Recurse $dataRoot $backupRoot -Force
}

# 2. Extract package
Expand-Archive -Path $ZipPath -DestinationPath $tempDir -Force

# 3. Restore data files
$srcData = "$tempDir\data"
if (Test-Path $srcData) {
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    Copy-Item -Recurse "$srcData\*" $dataRoot -Force
    Write-Host "Data files restored."
}

# 4. Copy continuation log (resume engine reads this on first startup)
$logSrc = "$tempDir\continuation_log.json"
if (Test-Path $logSrc) {
    Copy-Item $logSrc $dataRoot -Force
    Write-Host "Continuation log copied."
}

# 5. Cleanup
Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Restore complete.  Start Archimedes -- it will automatically"
Write-Host "re-protect encryption keys and resume paused tasks."
""";
    }
}
