using System.Text.Json;

namespace Agnes.Cli;

/// <summary>A host the CLI can talk to: a friendly <see cref="Name"/> (the id used for prefix matching),
/// its <see cref="Url"/>, and the device <see cref="Token"/> (held in memory only — never persisted in the
/// clear; see <see cref="SecureTokenProtector"/>).</summary>
public sealed record HostEntry(string Name, string Url, string Token);

/// <summary>The set of paired hosts, addressable by name prefix. Injected so commands can run against an
/// in-memory registry in tests.</summary>
public interface IHostRegistry
{
    IReadOnlyList<HostEntry> Hosts { get; }

    void Upsert(HostEntry entry);

    bool Remove(string name);
}

/// <summary>An in-memory registry (tests, and the seed for the file-backed one).</summary>
public sealed class InMemoryHostRegistry : IHostRegistry
{
    private readonly List<HostEntry> _hosts;

    public InMemoryHostRegistry(IEnumerable<HostEntry>? seed = null) => _hosts = seed?.ToList() ?? [];

    public IReadOnlyList<HostEntry> Hosts => _hosts.ToArray();

    public void Upsert(HostEntry entry)
    {
        _hosts.RemoveAll(h => string.Equals(h.Name, entry.Name, StringComparison.Ordinal));
        _hosts.Add(entry);
    }

    public bool Remove(string name) => _hosts.RemoveAll(h => string.Equals(h.Name, name, StringComparison.Ordinal)) > 0;
}

/// <summary>
/// The persisted registry of paired hosts. Tokens are sealed at rest via <see cref="SecureTokenProtector"/>;
/// the on-disk file never contains a usable token.
/// </summary>
public sealed class FileHostRegistry : IHostRegistry
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly SecureTokenProtector _protector;
    private readonly List<HostEntry> _hosts = [];

    public FileHostRegistry(string path, SecureTokenProtector protector)
    {
        _path = path;
        _protector = protector;
        Load();
    }

    public IReadOnlyList<HostEntry> Hosts => _hosts.ToArray();

    public void Upsert(HostEntry entry)
    {
        _hosts.RemoveAll(h => string.Equals(h.Name, entry.Name, StringComparison.Ordinal));
        _hosts.Add(entry);
        Save();
    }

    public bool Remove(string name)
    {
        var removed = _hosts.RemoveAll(h => string.Equals(h.Name, name, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var stored = JsonSerializer.Deserialize<List<StoredHost>>(File.ReadAllText(_path), Options) ?? [];
        foreach (var s in stored)
        {
            // Skip any entry we can't unseal (e.g. copied from another machine) rather than crashing.
            if (TryUnseal(s, out var token))
            {
                _hosts.Add(new HostEntry(s.Name, s.Url, token));
            }
        }
    }

    private bool TryUnseal(StoredHost stored, out string token)
    {
        try
        {
            token = _protector.Unprotect(stored.SealedToken);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            token = string.Empty;
            return false;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var stored = _hosts.Select(h => new StoredHost(h.Name, h.Url, _protector.Protect(h.Token))).ToList();
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stored, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed record StoredHost(string Name, string Url, string SealedToken);
}
