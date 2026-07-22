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
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Persisted installed-plugin state: id, version, source, enabled flag, granted capabilities, install
/// date — host state, exactly like paired-device records, not something that lives only in memory. JSON
/// file-backed, mirroring <c>DeviceRegistry</c>'s pattern (whole-file read on load, atomic write-then-move
/// on save, warnings — not throws — on I/O failure).
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
        lock (_gate) { _byPluginId[record.PluginId] = record; }
        Save();
    }

    public void Remove(string pluginId)
    {
        lock (_gate) { _byPluginId.Remove(pluginId); }
        Save();
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

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            List<PluginRecord> snapshot;
            lock (_gate) { snapshot = _byPluginId.Values.ToList(); }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not persist plugin state to {Path}", _path);
        }
    }
}
