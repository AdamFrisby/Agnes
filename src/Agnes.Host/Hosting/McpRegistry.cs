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

    /// <summary>
    /// The effective merged set of servers that WOULD be active for a given workspace on this host — a pure
    /// read (no side effects) exposed for the "preview" view and reused by session open. Enabled + in-scope
    /// only, across both run-locations. Not strict: it never throws (an unresolvable enabled server is
    /// simply included as configured — <see cref="Resolve"/> is where the strict/lenient policy lives).
    /// </summary>
    public IReadOnlyList<McpServerInfo> EffectiveFor(string? workspaceId)
        => _servers.Values.Where(s => s.Enabled && InScope(s, workspaceId))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(ToInfo).ToArray();

    /// <summary>
    /// Resolves the enabled, in-scope servers for a workspace at a given run-location, applying the
    /// strict/lenient policy to any that can't be resolved (a stdio server with no command, or an http
    /// server with no url). Lenient (<paramref name="strict"/> false, the default): each unresolvable
    /// enabled server is dropped and named in <see cref="McpResolution.Warnings"/> so the session still
    /// starts with the rest. Strict: the first unresolvable enabled server throws
    /// <see cref="McpResolutionException"/> naming it, so session start fails loudly. This is the single
    /// authority both preview (via <see cref="EffectiveFor"/>) and session open call.
    /// </summary>
    public McpResolution Resolve(McpRunAt runAt, string? workspaceId, bool strict = false)
    {
        var inScope = _servers.Values
            .Where(s => s.Enabled && ParseRunAt(s.RunAt) == runAt && InScope(s, workspaceId))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToInfo)
            .ToArray();

        var servers = new List<McpServerInfo>(inScope.Length);
        var warnings = new List<string>();
        foreach (var s in inScope)
        {
            if (IsResolvable(s))
            {
                servers.Add(s);
                continue;
            }

            var message = $"MCP server '{s.Name}' ({s.Id}) is enabled but can't be resolved: {UnresolvableReason(s)}.";
            if (strict)
            {
                throw new McpResolutionException(message);
            }

            _logger?.LogWarning("Skipping MCP server {Name} ({Id}) at start: {Reason}", s.Name, s.Id, UnresolvableReason(s));
            warnings.Add(message);
        }

        return new McpResolution(servers, warnings);
    }

    // A server is resolvable when it carries enough config to actually launch: a command (stdio) or a url (http).
    private static bool IsResolvable(McpServerInfo s)
        => string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(s.Url)
            : !string.IsNullOrWhiteSpace(s.Command);

    private static string UnresolvableReason(McpServerInfo s)
        => string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase)
            ? "http transport with no url"
            : "stdio transport with no command";

    // AllHosts/ThisHost both apply on the host that stores the entry; ThisWorkspace applies only when the
    // session's workspace matches the entry's recorded WorkspaceId (and never when the workspace is unknown).
    private static bool InScope(McpRecord s, string? workspaceId) => s.ApplyScope switch
    {
        McpApplyScope.ThisWorkspace => workspaceId is not null
            && string.Equals(s.WorkspaceId, workspaceId, StringComparison.Ordinal),
        _ => true,
    };

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
        r.Args ?? [], r.Env ?? new Dictionary<string, string>(), r.Url, r.BearerTokenEnv,
        r.ApplyScope, r.WorkspaceId);

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
        ApplyScope = req.ApplyScope,
        // A ThisWorkspace scope is meaningless without a workspace id; drop the id otherwise so it can't leak.
        WorkspaceId = req.ApplyScope == McpApplyScope.ThisWorkspace ? req.WorkspaceId : null,
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

        // Defaults to AllHosts (enum zero), so an entry persisted before scopes existed loads as "applies everywhere".
        public McpApplyScope ApplyScope { get; set; } = McpApplyScope.AllHosts;
        public string? WorkspaceId { get; set; }
    }
}

/// <summary>The outcome of resolving MCP servers for a session start: the servers to launch, plus any
/// lenient-mode warnings (an enabled-but-unresolvable server that was skipped). Immutable, pure result.</summary>
public sealed record McpResolution(IReadOnlyList<McpServerInfo> Servers, IReadOnlyList<string> Warnings);

/// <summary>Thrown by <see cref="McpRegistry.Resolve"/> in strict mode when an enabled server can't be
/// resolved — the message names the offending server so session start fails with a clear cause.</summary>
public sealed class McpResolutionException : Exception
{
    public McpResolutionException(string message) : base(message) { }
}

/// <summary>Host-wide MCP options (from <c>Agnes:Mcp:*</c>). <see cref="Strict"/> controls session-open
/// resolution: false (default) skips an unresolvable enabled server with a warning and starts anyway;
/// true fails the session start naming the offending server.</summary>
public sealed record McpOptions(bool Strict = false);

/// <summary>Where an MCP server runs.</summary>
public enum McpRunAt
{
    /// <summary>On the Agnes host — used by host sessions, forwarded into sandboxes.</summary>
    Host,

    /// <summary>Inside the sandbox VM.</summary>
    Sandbox,
}
