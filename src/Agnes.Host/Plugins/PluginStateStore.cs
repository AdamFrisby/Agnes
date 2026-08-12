using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Plugins;

/// <summary>Everything <see cref="PluginInstaller"/> needs to reload a plugin from disk without a fresh
/// download: where its extracted files live, which capabilities it was granted, and its settings.</summary>
public sealed record PluginRecord(
    string PluginId,
    string PackageId,
    string Version,
    bool Enabled,
    IReadOnlyList<string> GrantedCapabilities,
    string ExtractedPath,
    string MainAssemblyPath,
    DateTimeOffset InstalledAt,
    IReadOnlyDictionary<string, string> Settings,
    string? Source = null,
    string? Sha512 = null);

/// <summary>
/// Persisted installed-plugin state: id, version, source, enabled flag, granted capabilities, install
/// date — host state, exactly like paired-device records, not something that lives only in memory. JSON
/// file-backed, mirroring <c>DeviceRegistry</c>'s pattern (whole-file read on load and atomic
/// write-then-move on save). Persistence failures are propagated so callers cannot report an install
/// as successful when it will disappear on restart.
/// </summary>
public sealed class PluginStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginRecord> _byPluginId = new();
    private readonly string _path;
    private readonly ILogger<PluginStateStore>? _logger;

    public PluginStateStore(string dataFilePath, ILogger<PluginStateStore>? logger = null)
    {
        _path = dataFilePath;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<PluginRecord> All()
    {
        lock (_gate) { return _byPluginId.Values.ToArray(); }
    }

    public PluginRecord? Find(string pluginId)
    {
        lock (_gate) { return _byPluginId.GetValueOrDefault(pluginId); }
    }

    public void Set(PluginRecord record)
    {
        lock (_gate)
        {
            var previous = _byPluginId.GetValueOrDefault(record.PluginId);
            _byPluginId[record.PluginId] = record;
            try
            {
                SaveLocked();
            }
            catch
            {
                if (previous is null) _byPluginId.Remove(record.PluginId);
                else _byPluginId[record.PluginId] = previous;
                throw;
            }
        }
    }

    public void Remove(string pluginId)
    {
        lock (_gate)
        {
            var previous = _byPluginId.GetValueOrDefault(pluginId);
            _byPluginId.Remove(pluginId);
            try { SaveLocked(); }
            catch
            {
                if (previous is not null) _byPluginId[pluginId] = previous;
                throw;
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var records = JsonSerializer.Deserialize<List<PluginRecord>>(File.ReadAllText(_path));
            foreach (var r in records ?? [])
            {
                _byPluginId[r.PluginId] = r;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load plugin state from {Path}", _path);
        }
    }

    private void SaveLocked()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var snapshot = _byPluginId.Values.ToList();
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
        File.Move(tmp, _path, overwrite: true);
    }
}
