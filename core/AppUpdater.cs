using System.Diagnostics;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 32 — Android App OTA Updater via ADB WiFi.
///
/// UPDATE FLOW:
///   1. Fetch phone IP from Net service: GET /v1/android/device/{deviceId}
///   2. Run scripts/update-android.sh [phone-ip]
///      a. ./gradlew assembleDebug   — builds APK
///      b. adb connect {ip}:5555     — WiFi debug connect
///      c. adb install -r app.apk   — installs on device
///   3. Send FCM push via AndroidBridge: "App updated ✓"
///
/// REQUIREMENTS ON UBUNTU:
///   sudo apt install android-tools-adb
///   ADB WiFi enabled on phone (Developer Options → Wireless Debugging)
///   Android Studio / SDK for Gradle
///
/// PHONE IP DISCOVERY:
///   Android app reports its local WiFi IP on startup → Net stores it →
///   Core fetches via GET /v1/android/device/{deviceId}
/// </summary>
public class AppUpdater
{
    private readonly HttpClient     _http;
    private readonly AndroidBridge  _bridge;
    private readonly string         _netUrl;
    private readonly string         _scriptPath;

    // Update state
    private volatile bool     _isRunning;
    private string?           _lastStatus;
    private DateTime          _lastUpdated = DateTime.MinValue;
    private readonly object   _lock = new();

    public AppUpdater(HttpClient http, AndroidBridge bridge)
    {
        _http       = http;
        _bridge     = bridge;
        _netUrl     = Environment.GetEnvironmentVariable("ARCHIMEDES_NET_URL") ?? "http://localhost:5052";
        _scriptPath = FindScript();
    }

    // ── Script path resolution ────────────────────────────────────────────────

    private static string FindScript()
    {
        var candidates = new[]
        {
            // Dev: running from core/bin/Debug/net8.0/
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "scripts", "update-android.sh")),
            // Prod: scripts/ alongside the executable
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "update-android.sh"),
            // Running from repo root
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "update-android.sh"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public object GetStatus() => new
    {
        running      = _isRunning,
        lastStatus   = _lastStatus,
        lastUpdated  = _lastUpdated == DateTime.MinValue ? null : (DateTime?)_lastUpdated,
        scriptPath   = _scriptPath,
        scriptExists = File.Exists(_scriptPath),
        adbAvailable = IsAdbAvailable()
    };

    /// <summary>
    /// Start an async OTA update. Returns immediately — tracks progress via GetStatus().
    /// </summary>
    /// <param name="deviceId">Android device ID (to resolve IP from Net service)</param>
    /// <param name="phoneIp">Override IP (skip Net lookup if provided)</param>
    public async Task<object> StartUpdateAsync(string? deviceId = null, string? phoneIp = null)
    {
        lock (_lock)
        {
            if (_isRunning) return new { ok = false, error = "Update already in progress" };
            _isRunning  = true;
            _lastStatus = "starting";
        }

        // Resolve phone IP from Net service if not explicitly provided
        if (phoneIp == null && deviceId != null)
            phoneIp = await FetchPhoneIpAsync(deviceId);

        // Fire-and-forget — update takes 1-3 minutes
        _ = Task.Run(() => RunUpdateAsync(phoneIp));

        return new { ok = true, status = "started", phoneIp, scriptPath = _scriptPath };
    }

    // ── Internal update pipeline ──────────────────────────────────────────────

    private async Task RunUpdateAsync(string? phoneIp)
    {
        _lastUpdated = DateTime.UtcNow;

        try
        {
            if (!File.Exists(_scriptPath))
            {
                SetStatus($"error: script not found at {_scriptPath}");
                return;
            }

            // Ensure executable bit on Linux/macOS
            if (!OperatingSystem.IsWindows())
                await RunProcessAsync("chmod", $"+x \"{_scriptPath}\"", timeoutMs: 5_000);

            // Run the update script
            var args   = phoneIp != null ? $"\"{phoneIp}\"" : "";
            var result = await RunProcessAsync("bash", $"\"{_scriptPath}\" {args}", timeoutMs: 300_000);

            if (result.ExitCode == 0)
            {
                var lastLine = result.Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(l => l.Trim().Length > 0) ?? "done";
                SetStatus($"done: {lastLine}");

                // Push FCM notification to phone
                await _bridge.NotifyAsync(
                    title: "ארכימדס — עדכון אפליקציה",
                    body:  "האפליקציה עודכנה בהצלחה ✓  אנא הפעל מחדש",
                    data:  new Dictionary<string, string> { ["type"] = "update" }
                );
            }
            else
            {
                SetStatus($"error (exit {result.ExitCode}): {result.Stderr.Split('\n').FirstOrDefault() ?? "unknown"}");
                ArchLogger.LogWarn($"[AppUpdater] Update failed: {result.Stderr}");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"exception: {ex.Message}");
            ArchLogger.LogWarn($"[AppUpdater] Exception: {ex}");
        }
        finally
        {
            _isRunning   = false;
            _lastUpdated = DateTime.UtcNow;
        }
    }

    // ── Phone IP resolution ───────────────────────────────────────────────────

    private async Task<string?> FetchPhoneIpAsync(string deviceId)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_netUrl}/v1/android/device/{deviceId}");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ip", out var ip) && ip.ValueKind == JsonValueKind.String)
                return ip.GetString();
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[AppUpdater] Could not fetch phone IP from Net: {ex.Message}");
        }
        return null;
    }

    // ── ADB availability check ────────────────────────────────────────────────

    private static bool IsAdbAvailable()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("adb", "version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                }
            };
            proc.Start();
            proc.WaitForExit(3_000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Process runner ────────────────────────────────────────────────────────

    private record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<ProcessResult> RunProcessAsync(string cmd, string args, int timeoutMs = 60_000)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            }
        };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(timeoutMs);
        return new ProcessResult(proc.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private void SetStatus(string status)
    {
        _lastStatus  = status;
        _lastUpdated = DateTime.UtcNow;
        ArchLogger.LogInfo($"[AppUpdater] {status}");
    }
}
