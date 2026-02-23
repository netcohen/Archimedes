using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Runs the test gate inside an isolated sandbox. Never touches production DB/credentials.
/// </summary>
public class SandboxRunner
{
    private readonly string _sandboxRoot;
    private readonly string _repoRoot;
    private readonly SelfUpdateAudit _audit;
    private readonly int _sandboxCorePort;
    private readonly int _sandboxNetPort;

    public SandboxRunner(string sandboxRoot, string repoRoot, SelfUpdateAudit audit, int sandboxCorePort = 5053, int sandboxNetPort = 5054)
    {
        _sandboxRoot = Path.GetFullPath(sandboxRoot);
        _repoRoot = Path.GetFullPath(repoRoot);
        _audit = audit;
        _sandboxCorePort = sandboxCorePort;
        _sandboxNetPort = sandboxNetPort;
    }

    public SandboxRunResult Run(string commitOrRef, int soakHours = 0, bool dryRun = false)
    {
        var runId = $"sb-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var sandboxDir = Path.Combine(_sandboxRoot, runId);
        var dataPath = Path.Combine(sandboxDir, "archimedes-data");

        try
        {
            Directory.CreateDirectory(sandboxDir);
            Directory.CreateDirectory(dataPath);

            CopyRepoToSandbox(sandboxDir);

            var manifest = new CandidateManifest
            {
                RunId = runId,
                CommitOrRef = commitOrRef,
                SandboxPath = sandboxDir,
                CreatedAt = DateTime.UtcNow
            };

            var built = BuildInSandbox(sandboxDir, manifest, skipNetBuild: dryRun);
            if (!built)
            {
                _audit.Log("sandbox-run", runId, "Build failed", false);
                return new SandboxRunResult { Success = false, RunId = runId, Error = "Build failed" };
            }

            if (dryRun)
            {
                manifest.GatePassed = true;
                manifest.TestResultsSummary = "DRY_RUN (build only)";
                manifest.CandidateId = $"cand-dry-{HashString(runId).Substring(0, 8)}";
                _audit.Log("sandbox-run", runId, "Dry run: build passed, gate skipped", true);
                return new SandboxRunResult
                {
                    Success = true,
                    RunId = runId,
                    CandidateId = manifest.CandidateId,
                    Manifest = manifest
                };
            }

            Process? coreProc = null;
            Process? netProc = null;

            try
            {
                coreProc = StartCore(sandboxDir, dataPath);
                netProc = StartNet(sandboxDir);
                Thread.Sleep(5000);

                var gateResult = RunGate(sandboxDir, soakHours);

                manifest.GatePassed = gateResult;
                manifest.TestResultsSummary = gateResult ? "PASS" : "FAIL";

                if (gateResult)
                {
                    manifest.CandidateId = $"cand-{HashString(runId + commitOrRef).Substring(0, 8)}";
                    var manifestPath = Path.Combine(sandboxDir, "manifest.json");
                    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                    _audit.Log("sandbox-run", manifest.CandidateId, $"Gate passed, candidate ready", true);
                }
                else
                {
                    _audit.Log("sandbox-run", runId, "Gate failed", false);
                }

                return new SandboxRunResult
                {
                    Success = gateResult,
                    RunId = runId,
                    CandidateId = manifest.CandidateId,
                    Manifest = manifest,
                    Error = gateResult ? null : "Gate failed"
                };
            }
            finally
            {
                TryKill(coreProc);
                TryKill(netProc);
            }
        }
        catch (Exception ex)
        {
            _audit.Log("sandbox-run", runId, ex.Message, false);
            return new SandboxRunResult { Success = false, RunId = runId, Error = ex.Message };
        }
    }

    private void CopyRepoToSandbox(string sandboxDir)
    {
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", "dist", ".gradle", "build", ".cursor", "docs", "logs", "android", "core.tests"
        };

        foreach (var item in Directory.GetFileSystemEntries(_repoRoot))
        {
            var name = Path.GetFileName(item);
            if (name == null || exclude.Contains(name)) continue;
            var dest = Path.Combine(sandboxDir, name);
            if (Directory.Exists(item))
                CopyDirectory(item, dest, exclude);
            else
                File.Copy(item, dest, true);
        }
    }

    private static void CopyDirectory(string src, string dest, HashSet<string> exclude)
    {
        Directory.CreateDirectory(dest);
        foreach (var item in Directory.GetFileSystemEntries(src))
        {
            var name = Path.GetFileName(item);
            if (name == null || exclude.Contains(name)) continue;
            var d = Path.Combine(dest, name);
            if (Directory.Exists(item))
                CopyDirectory(item, d, exclude);
            else
                File.Copy(item, d, true);
        }
    }

    private bool BuildInSandbox(string sandboxDir, CandidateManifest manifest, bool skipNetBuild = false)
    {
        try
        {
            var coreDir = Path.Combine(sandboxDir, "core");
            var config = skipNetBuild ? "Debug" : "Release";
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build -c {config}",
                WorkingDirectory = coreDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(120000);
            if (p.ExitCode != 0) return false;

            if (skipNetBuild) return true;

            var netDir = Path.Combine(sandboxDir, "net");
            if (File.Exists(Path.Combine(netDir, "package.json")))
            {
                try
                {
                    var npm = Process.Start(new ProcessStartInfo
                    {
                        FileName = "npm",
                        Arguments = "run build",
                        WorkingDirectory = netDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    npm?.WaitForExit(90000);
                    if (npm != null && npm.ExitCode != 0) return false;
                }
                catch { return false; }
            }

            return true;
        }
        catch { return false; }
    }

    private Process? StartCore(string sandboxDir, string dataPath)
    {
        var coreDir = Path.Combine(sandboxDir, "core");
        var exe = Path.Combine(coreDir, "bin", "Release", "net8.0", "Archimedes.Core.dll");
        if (!File.Exists(exe))
            exe = Path.Combine(coreDir, "bin", "Debug", "net8.0", "Archimedes.Core.dll");
        if (!File.Exists(exe)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{exe}\"",
            WorkingDirectory = coreDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["ARCHIMEDES_DATA_PATH"] = dataPath;
        psi.Environment["ARCHIMEDES_PORT"] = _sandboxCorePort.ToString();
        return Process.Start(psi);
    }

    private Process? StartNet(string sandboxDir)
    {
        var netDir = Path.Combine(sandboxDir, "net");
        var indexPath = Path.Combine(netDir, "dist", "index.js");
        if (!File.Exists(indexPath)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{indexPath}\"",
            WorkingDirectory = netDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["PORT"] = _sandboxNetPort.ToString();
        return Process.Start(psi);
    }

    private bool RunGate(string sandboxDir, int soakHours)
    {
        var scriptsDir = Path.Combine(sandboxDir, "scripts");
        var gatePath = Path.Combine(scriptsDir, "phase14-ready-gate.ps1");
        if (!File.Exists(gatePath)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-ExecutionPolicy Bypass -File \"{gatePath}\" -SoakHours {soakHours}",
            WorkingDirectory = sandboxDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["ARCHIMEDES_CORE_URL"] = $"http://localhost:{_sandboxCorePort}";
        psi.Environment["ARCHIMEDES_NET_URL"] = $"http://localhost:{_sandboxNetPort}";

        using var p = Process.Start(psi);
        if (p == null) return false;
        p.WaitForExit(300000);
        return p.ExitCode == 0;
    }

    private static void TryKill(Process? proc)
    {
        try
        {
            proc?.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static string HashString(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public class SandboxRunResult
{
    public bool Success { get; set; }
    public string RunId { get; set; } = "";
    public string? CandidateId { get; set; }
    public CandidateManifest? Manifest { get; set; }
    public string? Error { get; set; }
}

public class CandidateManifest
{
    public string RunId { get; set; } = "";
    public string? CandidateId { get; set; }
    public string CommitOrRef { get; set; } = "";
    public string SandboxPath { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool GatePassed { get; set; }
    public string? TestResultsSummary { get; set; }
}
