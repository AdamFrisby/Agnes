using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>A persisted record of a sandbox VM Agnes created — kept so the user can see, resume, or
/// delete it even across daemon restarts (when the in-memory <c>_sandboxBySession</c> handle is gone).</summary>
public sealed record SandboxRecord(
    string SessionId,
    string VmName,
    string Provider,
    string AdapterId,
    string WorkingDirectory,
    string? ProjectName,
    string Title,
    string State,            // "running" | "stopped"
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    // The session's open-time options, persisted so a resume restores it faithfully.
    bool SkipPermissions = false,
    string McpApproval = "Ask",
    string GitCredentialMode = "Ask");

/// <summary>
/// Persists the sandboxes Agnes owns (<c>~/.agnes/sandboxes.json</c>) so closed/stopped VMs stay visible
/// and manageable (resume / delete) — including after a host restart, where the in-memory handles are
/// gone. Mirrors the other host stores (lock + atomic tmp-move).
/// </summary>
public sealed class SandboxRegistry
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<SandboxRegistry>? _logger;
    private List<SandboxRecord> _records = new();

    public SandboxRegistry(string path, ILogger<SandboxRegistry>? logger = null)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<SandboxRecord> List()
    {
        lock (_gate)
        {
            return _records.OrderByDescending(r => r.LastUsedAt).ToArray();
        }
    }

    public SandboxRecord? Get(string sessionId)
    {
        lock (_gate)
        {
            return _records.FirstOrDefault(r => r.SessionId == sessionId);
        }
    }

    /// <summary>The VM names Agnes currently tracks (for orphan detection).</summary>
    public IReadOnlyCollection<string> TrackedVmNames()
    {
        lock (_gate)
        {
            return _records.Select(r => r.VmName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Upsert(SandboxRecord record)
    {
        lock (_gate)
        {
            var i = _records.FindIndex(r => r.SessionId == record.SessionId);
            if (i >= 0)
            {
                _records[i] = record;
            }
            else
            {
                _records.Add(record);
            }

            Save();
        }
    }

    public void SetState(string sessionId, string state, DateTimeOffset lastUsed)
    {
        lock (_gate)
        {
            var i = _records.FindIndex(r => r.SessionId == sessionId);
            if (i >= 0)
            {
                _records[i] = _records[i] with { State = state, LastUsedAt = lastUsed };
                Save();
            }
        }
    }

    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            if (_records.RemoveAll(r => r.SessionId == sessionId) > 0)
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _records = JsonSerializer.Deserialize<List<SandboxRecord>>(File.ReadAllText(_path), Options) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load the sandbox registry");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_records, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save the sandbox registry");
        }
    }
}
