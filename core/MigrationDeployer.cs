using System.Net.Http.Headers;

namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Copies the migration package(s) to the target.
///
/// LOCAL_PATH → File.Copy all volume zips + write restore-archimedes.ps1 helper script
/// HTTP_URL   → POST each volume zip as multipart to {target}/migration/receive
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
        // Resolve the list of volumes (PackagePaths takes precedence over the
        // legacy single-file PackagePath for backward compatibility).
        var zips = ResolveZipPaths(plan);

        if (zips.Count == 0)
        {
            plan.Error = "No package files found — cannot deploy";
            ArchLogger.LogWarn($"[Deployer] {plan.Error}");
            return false;
        }

        ArchLogger.LogInfo(
            $"[Deployer] Deploying {zips.Count} volume(s) → '{plan.TargetPath}' " +
            $"(type={plan.TargetType})");

        try
        {
            return plan.TargetType == MigrationTargetType.HTTP_URL
                ? await DeployHttpAsync(plan, zips, ct)
                : DeployLocalPath(plan, zips);
        }
        catch (Exception ex)
        {
            plan.Error = $"Deploy failed: {ex.Message}";
            ArchLogger.LogWarn($"[Deployer] {plan.Error}");
            return false;
        }
    }

    // ── Local / UNC path ───────────────────────────────────────────────────

    private static bool DeployLocalPath(MigrationPlan plan, List<string> zips)
    {
        try { Directory.CreateDirectory(plan.TargetPath); } catch { }

        var destZips = new List<string>(zips.Count);
        foreach (var src in zips)
        {
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(plan.TargetPath, Path.GetFileName(src));
            File.Copy(src, dest, overwrite: true);
            destZips.Add(dest);
        }

        if (destZips.Count == 0)
        {
            plan.Error = "No package files could be copied";
            return false;
        }

        // Drop a PowerShell restore helper that handles single or multi-volume
        var scriptPath = Path.Combine(plan.TargetPath, "restore-archimedes.ps1");
        File.WriteAllText(scriptPath, BuildRestoreScript(destZips));

        ArchLogger.LogInfo(
            $"[Deployer] Copied {destZips.Count} volume(s) to {plan.TargetPath} " +
            $"+ restore script at {scriptPath}");
        return true;
    }

    // ── HTTP target ────────────────────────────────────────────────────────

    private async Task<bool> DeployHttpAsync(
        MigrationPlan    plan,
        List<string>     zips,
        CancellationToken ct)
    {
        var url = $"{plan.TargetPath.TrimEnd('/')}/migration/receive";

        for (int i = 0; i < zips.Count; i++)
        {
            var zipPath = zips[i];
            if (!File.Exists(zipPath)) continue;

            using var content   = new MultipartFormDataContent();
            var fileBytes       = await File.ReadAllBytesAsync(zipPath, ct);
            var byteContent     = new ByteArrayContent(fileBytes);
            byteContent.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
            content.Add(byteContent, "package", Path.GetFileName(zipPath));

            using var resp = await _http.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();

            ArchLogger.LogInfo(
                $"[Deployer] Volume {i + 1}/{zips.Count} delivered via HTTP: " +
                $"{Path.GetFileName(zipPath)}");
        }

        ArchLogger.LogInfo($"[Deployer] All {zips.Count} volume(s) delivered to {url}");
        return true;
    }

    // ── Restore script (handles single or multi-volume) ────────────────────

    private static string BuildRestoreScript(List<string> destZips)
    {
        // Build a PowerShell array literal of quoted paths
        var zipArrayLiteral = string.Join(",\n    ",
            destZips.Select(p => $"\"{p.Replace("\\", "\\\\")}\""));

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        // Use $"""...""" (double-dollar raw string interpolation):
        //   {{expr}} = C# interpolation   { and } alone = literal PowerShell braces
        return
$$"""
# Archimedes Migration Restore Script
# Generated : {{ts}} UTC
# Run this on the target machine BEFORE starting Archimedes.
# Handles both single-zip and multi-volume split packages.

param(
    [string[]]$ZipPaths = @(
    {{zipArrayLiteral}}
    )
)

$dataRoot   = "$env:LOCALAPPDATA\Archimedes"
$backupRoot = "$env:LOCALAPPDATA\Archimedes_bak_$(Get-Date -Format yyyyMMddHHmmss)"
$tempDir    = "$env:TEMP\arch_restore_$(New-Guid)"

Write-Host "Archimedes migration restore starting ($($ZipPaths.Count) volume(s))..."

# 1. Back up existing data
if (Test-Path $dataRoot) {
    Write-Host "Backing up existing data to $backupRoot"
    Copy-Item -Recurse $dataRoot $backupRoot -Force
}

# 2. Extract all volumes (merged into single temp dir)
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
foreach ($zip in $ZipPaths) {
    if (-not (Test-Path $zip)) {
        Write-Warning "Volume not found: $zip — skipping"
        continue
    }
    Write-Host "Extracting $zip ..."
    Expand-Archive -Path $zip -DestinationPath $tempDir -Force
}

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

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<string> ResolveZipPaths(MigrationPlan plan)
    {
        if (plan.PackagePaths.Count > 0)
            return plan.PackagePaths.Where(File.Exists).ToList();

        if (plan.PackagePath != null && File.Exists(plan.PackagePath))
            return new List<string> { plan.PackagePath };

        return new List<string>();
    }
}
