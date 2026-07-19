using System.Text.Json;

namespace Agnes.Ui.Core;

/// <summary>
/// Persists per-session prompt drafts and a bounded prompt history to a JSON file, so an
/// unsent draft survives closing the app and Up/Down recalls past prompts after relaunch.
/// Writes are best-effort and debounced through an in-memory model that is flushed on change.
/// </summary>
public sealed class FilePromptStore : IPromptStore
{
    private const int MaxHistory = 200;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Model _model;

    public FilePromptStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _model = Load();
    }

    public static string DefaultPath()
    {
        // Resilient to sandboxed/virtual file systems (e.g. WASM), where ApplicationData may be
        // empty and directory creation can throw — fall back to temp, and let Load/Flush no-op if
        // even that isn't writable.
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(string.IsNullOrEmpty(appData) ? Path.GetTempPath() : appData, "Agnes");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "prompts.json");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "agnes-prompts.json");
        }
    }

    public string LoadDraft(string sessionId)
    {
        lock (_gate)
        {
            return _model.Drafts.TryGetValue(sessionId, out var d) ? d : string.Empty;
        }
    }

    public void SaveDraft(string sessionId, string draft)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(draft))
            {
                _model.Drafts.Remove(sessionId);
            }
            else
            {
                _model.Drafts[sessionId] = draft;
            }

            Flush();
        }
    }

    public IReadOnlyList<string> LoadHistory(string sessionId)
    {
        lock (_gate)
        {
            return _model.History.TryGetValue(sessionId, out var h) ? h.ToArray() : [];
        }
    }

    public void AppendHistory(string sessionId, string prompt)
    {
        lock (_gate)
        {
            if (!_model.History.TryGetValue(sessionId, out var h))
            {
                _model.History[sessionId] = h = [];
            }

            // Collapse an immediate duplicate of the most recent entry.
            if (h.Count == 0 || h[^1] != prompt)
            {
                h.Add(prompt);
            }

            if (h.Count > MaxHistory)
            {
                h.RemoveRange(0, h.Count - MaxHistory);
            }

            Flush();
        }
    }

    private Model Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Model>(File.ReadAllText(_path), Options) ?? new Model();
            }
        }
        catch
        {
            // Corrupt/unreadable file: start clean rather than crash.
        }

        return new Model();
    }

    private void Flush()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_model, Options));
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    private sealed class Model
    {
        public Dictionary<string, string> Drafts { get; init; } = new();
        public Dictionary<string, List<string>> History { get; init; } = new();
    }
}
