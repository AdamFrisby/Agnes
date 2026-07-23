using System.Text.Json;

namespace Agnes.Cli;

/// <summary>A session the CLI has spawned (or otherwise been told about): its id, the host it lives on, and
/// the agent it runs. There is no server-side session enumeration, so — exactly as the interactive clients
/// track their own open sessions locally — the CLI remembers the sessions it created, which is what a
/// session-id <em>prefix</em> resolves against.</summary>
public sealed record SessionEntry(string SessionId, string HostUrl, string Adapter);

/// <summary>The set of known sessions, addressable by id prefix. Injected for testability.</summary>
public interface ISessionRegistry
{
    IReadOnlyList<SessionEntry> Sessions { get; }

    void Add(SessionEntry entry);
}

/// <summary>An in-memory session registry for tests and the seed of the file-backed one.</summary>
public sealed class InMemorySessionRegistry : ISessionRegistry
{
    private readonly List<SessionEntry> _sessions;

    public InMemorySessionRegistry(IEnumerable<SessionEntry>? seed = null) => _sessions = seed?.ToList() ?? [];

    public IReadOnlyList<SessionEntry> Sessions => _sessions.ToArray();

    public void Add(SessionEntry entry)
    {
        _sessions.RemoveAll(s => string.Equals(s.SessionId, entry.SessionId, StringComparison.Ordinal));
        _sessions.Add(entry);
    }
}

/// <summary>The persisted session registry (no secrets — just ids the CLI has seen).</summary>
public sealed class FileSessionRegistry : ISessionRegistry
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly List<SessionEntry> _sessions = [];

    public FileSessionRegistry(string path)
    {
        _path = path;
        if (File.Exists(_path))
        {
            _sessions = JsonSerializer.Deserialize<List<SessionEntry>>(File.ReadAllText(_path), Options) ?? [];
        }
    }

    public IReadOnlyList<SessionEntry> Sessions => _sessions.ToArray();

    public void Add(SessionEntry entry)
    {
        _sessions.RemoveAll(s => string.Equals(s.SessionId, entry.SessionId, StringComparison.Ordinal));
        _sessions.Add(entry);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_sessions, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
