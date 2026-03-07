using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 36 — Persistent command learning database.
///
/// Maps an IntentKey (intent + canonical slots) → bash command.
/// Archimedes starts knowing nothing. Every time it successfully
/// executes a new command it saves it here — and next time the same
/// intent arrives it resolves instantly without calling the LLM.
///
/// Storage: {dataDir}/command-store.json
/// </summary>
public class CommandStore
{
    private readonly string      _path;
    private Dictionary<string, string> _map = new();
    private readonly object      _lock = new();

    public CommandStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "command-store.json");
        Load();
    }

    // ── Key construction ────────────────────────────────────────────────────

    /// <summary>
    /// Build a canonical key from an InterpretResult.
    /// e.g., INSTALL_PACKAGE + {tool=vim} → "install_package:tool=vim"
    ///       SHOW_IP (no slots)           → "show_ip"
    /// </summary>
    public static string MakeKey(InterpretResult r)
    {
        var intent = (r.Intent ?? "unknown").ToLowerInvariant();
        if (r.Slots.Count == 0) return intent;
        var slots = string.Join(",",
            r.Slots
             .OrderBy(k => k.Key)
             .Select(k => $"{k.Key.ToLower()}={k.Value.ToLower()}"));
        return $"{intent}:{slots}";
    }

    // ── Public API ──────────────────────────────────────────────────────────

    public string? Lookup(string key)
    {
        lock (_lock)
            return _map.TryGetValue(key, out var cmd) ? cmd : null;
    }

    public void Save(string key, string command)
    {
        lock (_lock)
        {
            _map[key] = command;
            Persist();
        }
        ArchLogger.LogInfo($"[CommandStore] Learned: {key} → {command}");
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public IReadOnlyDictionary<string, string> All()
    {
        lock (_lock) return new Dictionary<string, string>(_map);
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            ArchLogger.LogInfo($"[CommandStore] Loaded {_map.Count} entries from {_path}");
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[CommandStore] Load failed: {ex.Message}");
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_map,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[CommandStore] Persist failed: {ex.Message}");
        }
    }
}
