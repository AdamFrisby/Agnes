using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host's catalogue of named <see cref="ConnectedServiceProfile"/> records, persisted to
/// <c>~/.agnes/connected-services.json</c>. Mirrors the other host stores (single lock, atomic tmp-move,
/// load-tolerant of a missing/corrupt file). A profile is identity/routing only — which provider, which
/// account — so this store <em>never</em> holds a secret; the real credential stays with the matching
/// <see cref="IConnectedServiceProvider"/>, which materialises a short-lived one just-in-time. That split
/// is why the whole store is safe to list to a client (names/labels only) and to serialise to disk.
/// </summary>
public sealed class ConnectedServiceProfileStore
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "connected-services.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger<ConnectedServiceProfileStore>? _logger;
    private readonly Dictionary<string, ConnectedServiceProfile> _byId = new(StringComparer.Ordinal);

    /// <param name="directory">
    /// Directory to persist the catalogue under (production passes <c>~/.agnes</c>). When null or blank the
    /// store is in-memory only and never touches disk — used by tests.
    /// </param>
    public ConnectedServiceProfileStore(string? directory = null, ILogger<ConnectedServiceProfileStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _logger = logger;
        Load();
    }

    /// <summary>All stored profiles, ordered by display name then account label (never null).</summary>
    public IReadOnlyList<ConnectedServiceProfile> List()
    {
        lock (_gate)
        {
            return _byId.Values
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.AccountLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>The profile with this id, or null.</summary>
    public ConnectedServiceProfile? Find(string id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    /// <summary>Upserts a profile (assigning an id when blank) keyed by <see cref="ConnectedServiceProfile.Id"/>
    /// and persists it; returns the stored profile.</summary>
    public ConnectedServiceProfile Save(ConnectedServiceProfile profile)
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

    /// <summary>Deletes a profile by id; returns true if one was removed. Deleting a profile immediately
    /// removes the host's ability to route a session to it (its credential can no longer be materialised).</summary>
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
            var profiles = JsonSerializer.Deserialize<List<ConnectedServiceProfile>>(File.ReadAllText(_path), Options);
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
            _logger?.LogWarning(ex, "Failed to load connected-service profiles from {Path}; starting empty.", _path);
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
            _logger?.LogWarning(ex, "Failed to persist connected-service profiles to {Path}.", _path);
        }
    }
}
