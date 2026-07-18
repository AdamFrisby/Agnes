using System.Text.Json;

namespace Agnes.App.Desktop.Persistence;

/// <summary>A host the user can connect a tab to.</summary>
public sealed record KnownHost(string Name, string Url, string Token);

/// <summary>Persists the list of hosts the user has added (the simulated host is built-in).</summary>
public sealed class HostRegistryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public HostRegistryStore(string? path = null) => _path = path ?? DefaultPath();

    public static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agnes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "hosts.json");
    }

    public IReadOnlyList<KnownHost> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<KnownHost>>(File.ReadAllText(_path), Options) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<KnownHost> hosts)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(hosts, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
