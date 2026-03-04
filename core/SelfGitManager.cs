using System.Diagnostics;

namespace Archimedes.Core;

/// <summary>
/// Phase 29 – Self Git Manager.
///
/// When Archimedes modifies its own Core source code (.cs files),
/// those changes are committed and pushed to git for auditability
/// and user review (via git log / git diff).
///
/// SEPARATION RULE:
///   Core source changes (.cs)        → git commit + push  (this class)
///   Acquired feature scripts/tools   → local only, NOT committed to git
///
/// Git operations are fire-and-forget with logging — a git failure never
/// blocks the self-improvement engine.
/// </summary>
public sealed class SelfGitManager
{
    private string? _repoRoot;

    public SelfGitManager()
    {
        _repoRoot = FindRepoRoot();
        if (_repoRoot != null)
            ArchLogger.LogInfo($"[SelfGitManager] Repo root: {_repoRoot}");
        else
            ArchLogger.LogWarn("[SelfGitManager] Git repo root not found — git operations disabled");
    }

    // ── Public API ────────────────────────────────────────────────────────

    public bool IsAvailable => _repoRoot != null && IsGitAvailable();

    /// <summary>
    /// Commits and pushes changes to specific Core .cs files.
    /// Only .cs files in the core/ directory are committed.
    /// Returns true if the commit succeeded.
    /// </summary>
    public bool CommitCoreChange(IEnumerable<string> changedFiles, string description)
    {
        if (_repoRoot == null)
        {
            ArchLogger.LogWarn("[SelfGitManager] Skipping git commit — no repo root");
            return false;
        }

        // Only commit .cs files (Core source) — never scripts, tools, data
        var coreFiles = changedFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(_repoRoot, f).Replace('\\', '/'))
            .Where(f => f.StartsWith("core/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (coreFiles.Count == 0)
        {
            ArchLogger.LogInfo("[SelfGitManager] No Core .cs files to commit");
            return false;
        }

        try
        {
            // Stage only the specific core files
            foreach (var file in coreFiles)
            {
                var (addOk, _, addErr) = RunGit($"add \"{file}\"");
                if (!addOk)
                {
                    ArchLogger.LogWarn($"[SelfGitManager] git add failed for {file}: {addErr}");
                    return false;
                }
            }

            // Commit with self-patch message
            var msg = $"self-patch: {description}\n\nAutonomous improvement by Archimedes Phase 29.";
            var (commitOk, commitOut, commitErr) = RunGit($"commit -m \"{EscapeForShell(msg)}\"");

            if (!commitOk)
            {
                // Nothing staged / nothing to commit is not an error
                if (commitErr.Contains("nothing to commit") || commitOut.Contains("nothing to commit"))
                {
                    ArchLogger.LogInfo("[SelfGitManager] Nothing to commit — no changes detected");
                    return true;
                }
                ArchLogger.LogWarn($"[SelfGitManager] git commit failed: {commitErr}");
                return false;
            }

            ArchLogger.LogInfo($"[SelfGitManager] Committed self-patch: {description}");

            // Push (non-blocking — failure is logged but not fatal)
            _ = Task.Run(() =>
            {
                var (pushOk, _, pushErr) = RunGit("push");
                if (!pushOk)
                    ArchLogger.LogWarn($"[SelfGitManager] git push failed: {pushErr}");
                else
                    ArchLogger.LogInfo("[SelfGitManager] Pushed self-patch to remote");
            });

            return true;
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[SelfGitManager] CommitCoreChange exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns the last N self-patch commit subjects.</summary>
    public List<string> GetSelfPatchHistory(int limit = 20)
    {
        if (_repoRoot == null) return new();
        try
        {
            var (ok, output, _) = RunGit(
                $"log --oneline --grep=\"self-patch:\" -n {limit}");
            if (!ok || string.IsNullOrWhiteSpace(output)) return new();
            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();
        }
        catch { return new(); }
    }

    // ── Internal ──────────────────────────────────────────────────────────

    private (bool ok, string stdout, string stderr) RunGit(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory       = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "", "Process.Start returned null");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);

            return (proc.ExitCode == 0, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private bool IsGitAvailable()
    {
        try
        {
            var (ok, _, _) = RunGit("--version");
            return ok;
        }
        catch { return false; }
    }

    private string? FindRepoRoot()
    {
        // Walk up from AppContext.BaseDirectory looking for .git folder
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string EscapeForShell(string s) =>
        s.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
}
