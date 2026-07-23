using System.Text.Json;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host's catalogue of named <see cref="LaunchProfile"/> records — reusable new-session launch configs —
/// persisted to <c>~/.agnes/launch-profiles.json</c>. Mirrors the other host stores (single lock, atomic
/// tmp-move, load-tolerant of a missing/corrupt file). A profile bundles only launch <em>options</em> (agent,
/// permission posture, MCP/git/sandbox defaults, model) — it never holds a secret, so the whole store is safe
/// to serialise to disk and to list to any paired client.
/// </summary>
public sealed class LaunchProfileStore
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "launch-profiles.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger<LaunchProfileStore>? _logger;
    private readonly Dictionary<string, LaunchProfile> _byId = new(StringComparer.Ordinal);

    /// <param name="directory">
    /// Directory to persist the catalogue under (production passes <c>~/.agnes</c>). When null or blank the
    /// store is in-memory only and never touches disk — used by tests.
    /// </param>
    public LaunchProfileStore(string? directory = null, ILogger<LaunchProfileStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _logger = logger;
        Load();
    }

    /// <summary>All stored profiles, ordered by name (never null).</summary>
    public IReadOnlyList<LaunchProfile> List()
    {
        lock (_gate)
        {
            return _byId.Values
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>The profile with this id, or null.</summary>
    public LaunchProfile? Find(string id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    /// <summary>Upserts a profile (assigning an id when blank) keyed by <see cref="LaunchProfile.Id"/> and
    /// persists it; returns the stored profile.</summary>
    public LaunchProfile Save(LaunchProfile profile)
    {
        var stored = string.IsNullOrWhiteSpace(profile.Id)
            ? profile with { Id = Guid.NewGuid().ToString("n") }
            : profile;

        lock (_gate)
        {
            _byId[stored.Id] = stored;
            Persist();
        }

        return stored;
    }

    /// <summary>Deletes a profile by id; returns true if one was removed. Deleting a profile does not affect a
    /// session already launched from it — application happens once, at launch.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!_byId.Remove(id))
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var profiles = JsonSerializer.Deserialize<List<LaunchProfile>>(File.ReadAllText(_path), Options);
            foreach (var p in profiles ?? [])
            {
                if (!string.IsNullOrWhiteSpace(p.Id))
                {
                    _byId[p.Id] = p;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load launch profiles from {Path}; starting empty.", _path);
            _byId.Clear();
        }
    }

    // Caller holds _gate.
    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byId.Values.ToArray(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist launch profiles to {Path}.", _path);
        }
    }
}
