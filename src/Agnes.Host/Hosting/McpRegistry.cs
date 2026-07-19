using System.Collections.Concurrent;
using System.Text.Json;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host's registry of MCP servers, configured from the desktop UI and persisted to
/// <c>~/.agnes/mcp.json</c>. Each entry records where it runs (host or sandbox), its transport
/// (stdio or http), and whether it's enabled. Mirrors <see cref="DeviceRegistry"/> (dict + gate +
/// atomic tmp-move) but holds no secrets-at-rest concern beyond the config the user entered.
/// </summary>
public sealed class McpRegistry
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, McpRecord> _servers = new(); // by id
    private readonly string _path;
    private readonly ILogger<McpRegistry>? _logger;

    public McpRegistry(string dataFilePath, ILogger<McpRegistry>? logger = null)
    {
        _path = dataFilePath;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<McpServerInfo> List()
        => _servers.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(ToInfo).ToArray();

    /// <summary>Servers that apply to a session, filtered by where they run (enabled only).</summary>
    public IReadOnlyList<McpServerInfo> Applicable(McpRunAt runAt)
        => _servers.Values.Where(s => s.Enabled && ParseRunAt(s.RunAt) == runAt).Select(ToInfo).ToArray();

    public McpServerInfo? Get(string id) => _servers.TryGetValue(id, out var r) ? ToInfo(r) : null;

    public McpServerInfo Add(McpServerRequest request)
    {
        lock (_gate)
        {
            var record = FromRequest(Guid.NewGuid().ToString("n"), request);
            _servers[record.Id] = record;
            Save();
            _logger?.LogInformation("Added MCP server {Name} ({Id}), runAt={RunAt}", record.Name, record.Id, record.RunAt);
            return ToInfo(record);
        }
    }

    public McpServerInfo? Update(string id, McpServerRequest request)
    {
        lock (_gate)
        {
            if (!_servers.ContainsKey(id))
            {
                return null;
            }

            var record = FromRequest(id, request);
            _servers[id] = record;
            Save();
            return ToInfo(record);
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (!_servers.TryRemove(id, out _))
            {
                return false;
            }

            Save();
            _logger?.LogInformation("Removed MCP server {Id}", id);
            return true;
        }
    }

    // ---- mapping ----

    private static McpServerInfo ToInfo(McpRecord r) => new(
        r.Id, r.Name, r.RunAt, r.Enabled, r.Transport, r.Command,
        r.Args ?? [], r.Env ?? new Dictionary<string, string>(), r.Url, r.BearerTokenEnv);

    private static McpRecord FromRequest(string id, McpServerRequest req) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(req.Name) ? "mcp" : req.Name.Trim(),
        RunAt = ParseRunAt(req.RunAt) == McpRunAt.Sandbox ? "sandbox" : "host",
        Enabled = req.Enabled,
        Transport = string.Equals(req.Transport, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "stdio",
        Command = req.Command,
        Args = req.Args?.ToList() ?? [],
        Env = req.Env is null ? new Dictionary<string, string>() : new Dictionary<string, string>(req.Env),
        Url = req.Url,
        BearerTokenEnv = req.BearerTokenEnv,
    };

    private static McpRunAt ParseRunAt(string? value)
        => string.Equals(value, "sandbox", StringComparison.OrdinalIgnoreCase) ? McpRunAt.Sandbox : McpRunAt.Host;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var records = JsonSerializer.Deserialize<List<McpRecord>>(File.ReadAllText(_path));
            foreach (var r in records ?? [])
            {
                if (!string.IsNullOrEmpty(r.Id))
                {
                    _servers[r.Id] = r;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load MCP registry from {Path}", _path);
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

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_servers.Values.ToList()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not persist MCP registry to {Path}", _path);
        }
    }

    private sealed class McpRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string RunAt { get; set; } = "host";
        public bool Enabled { get; set; } = true;
        public string Transport { get; set; } = "stdio";
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? Url { get; set; }
        public string? BearerTokenEnv { get; set; }
    }
}

/// <summary>Where an MCP server runs.</summary>
public enum McpRunAt
{
    /// <summary>On the Agnes host — used by host sessions, forwarded into sandboxes.</summary>
    Host,

    /// <summary>Inside the sandbox VM.</summary>
    Sandbox,
}
