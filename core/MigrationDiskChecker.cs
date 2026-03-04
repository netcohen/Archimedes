using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Verifies that the migration target has enough free disk space.
///
/// LOCAL_PATH → DriveInfo on the target drive root (supports UNC paths too)
/// HTTP_URL   → GET {target}/storage/health → reads freeSpaceMB field
/// </summary>
public class MigrationDiskChecker
{
    private readonly HttpClient _http;

    public MigrationDiskChecker(HttpClient http) => _http = http;

    // ── Main entry ─────────────────────────────────────────────────────────

    public async Task<MigrationDiskCheckResult> CheckAsync(
        MigrationTargetType targetType,
        string              targetPath,
        long                requiredMB,
        CancellationToken   ct = default)
    {
        try
        {
            return targetType == MigrationTargetType.HTTP_URL
                ? await CheckHttpAsync(targetPath, requiredMB, ct)
                : CheckLocalPath(targetPath, requiredMB);
        }
        catch (Exception ex)
        {
            return new MigrationDiskCheckResult
            {
                IsEnough   = false,
                RequiredMB = requiredMB,
                Error      = $"Disk check failed: {ex.Message}"
            };
        }
    }

    // ── Local / UNC path ───────────────────────────────────────────────────

    private static MigrationDiskCheckResult CheckLocalPath(
        string targetPath, long requiredMB)
    {
        // Ensure directory exists (e.g. UNC share, new local folder)
        try { Directory.CreateDirectory(targetPath); } catch { }

        var fullPath = Path.GetFullPath(targetPath);
        var root     = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(root))
            return new MigrationDiskCheckResult
            {
                IsEnough   = false,
                RequiredMB = requiredMB,
                Error      = $"Cannot determine drive root for path: {targetPath}"
            };

        try
        {
            var drive     = new DriveInfo(root);
            var available = drive.AvailableFreeSpace / 1024 / 1024;

            return new MigrationDiskCheckResult
            {
                IsEnough    = available >= requiredMB,
                AvailableMB = available,
                RequiredMB  = requiredMB
            };
        }
        catch (Exception ex)
        {
            return new MigrationDiskCheckResult
            {
                IsEnough   = false,
                RequiredMB = requiredMB,
                Error      = $"DriveInfo error: {ex.Message}"
            };
        }
    }

    // ── HTTP target ────────────────────────────────────────────────────────

    private async Task<MigrationDiskCheckResult> CheckHttpAsync(
        string targetBaseUrl, long requiredMB, CancellationToken ct)
    {
        var url = $"{targetBaseUrl.TrimEnd('/')}/storage/health";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc  = JsonDocument.Parse(json);

        long available = 0;
        if (doc.RootElement.TryGetProperty("freeSpaceMB", out var el))
            available = el.GetInt64();

        return new MigrationDiskCheckResult
        {
            IsEnough    = available >= requiredMB,
            AvailableMB = available,
            RequiredMB  = requiredMB
        };
    }
}
