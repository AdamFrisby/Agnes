using System.Text.Json;

namespace Agnes.App.Desktop.Persistence;

/// <summary>A host the user can connect a tab to.</summary>
/// <param name="Fingerprint">
/// SHA-256 of the host's TLS certificate, learned when the host was added. Present for a self-signed host
/// paired from an <c>agnes://pair</c> link; null for a host with a CA-issued certificate, which is
/// validated by chain and name instead.
/// </param>
/// <param name="Role">
/// What this device last knew itself to be on that host, so a reconnecting tab can explain an empty session
/// list in the same breath as showing it rather than a round trip later. Null means "not asked yet" — which
/// is what every host saved before roles existed says, and is why it is nullable rather than defaulting to
/// Member: an owner must never be told, even for a moment, that it is a member. The host re-answers on every
/// connect and wins.
/// </param>
public sealed record KnownHost(
    string Name, string Url, string Token, string? Fingerprint = null,
    Agnes.Protocol.DeviceRole? Role = null);

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
