using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Social;

/// <summary>
/// The host owner's collaborator directory — a set of <see cref="Collaborator"/> records keyed by canonical GitHub login
/// (case-insensitive), persisted to <c>~/.agnes/collaborators.json</c>. Mirrors the other host stores exactly:
/// single lock, atomic tmp-move persist, load-tolerant of a missing/corrupt file. A collaborator carries no secret
/// and being in the directory grants nothing on its own — it only makes a user <em>eligible</em> to be granted
/// access via a separate, explicit <see cref="AccessGrant"/>. So the whole store is safe to list to a client
/// and to serialise to disk.
/// </summary>
public sealed class CollaboratorStore
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "collaborators.json";

    /// <summary>What the file was called when this was the "friends" directory. Read once, on a host that
    /// predates the rename, so an existing directory survives the upgrade instead of silently emptying; the
    /// next write lands under <see cref="FileName"/> and the old file is left alone.</summary>
    private const string LegacyFileName = "friends.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger<CollaboratorStore>? _logger;
    private readonly Dictionary<string, Collaborator> _byLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="directory">
    /// Directory to persist the directory under (production passes <c>~/.agnes</c>). When null or blank the
    /// store is in-memory only and never touches disk — used by tests.
    /// </param>
    public CollaboratorStore(string? directory = null, ILogger<CollaboratorStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _logger = logger;
        Load();
    }

    /// <summary>All collaborators, ordered by GitHub login (never null).</summary>
    public IReadOnlyList<Collaborator> List()
    {
        lock (_gate)
        {
            return _byLogin.Values.OrderBy(f => f.GitHubLogin, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>The collaborator with this GitHub login (case-insensitive), or null.</summary>
    public Collaborator? Find(string gitHubLogin)
    {
        if (string.IsNullOrWhiteSpace(gitHubLogin))
        {
            return null;
        }

        lock (_gate)
        {
            return _byLogin.GetValueOrDefault(gitHubLogin);
        }
    }

    /// <summary>Whether this GitHub login is an explicit collaborator (case-insensitive).</summary>
    public bool Contains(string gitHubLogin) => Find(gitHubLogin) is not null;

    /// <summary>Upserts a collaborator keyed by <see cref="Collaborator.GitHubLogin"/> and persists it; returns the stored
    /// record.</summary>
    public Collaborator Add(Collaborator collaborator)
    {
        lock (_gate)
        {
            _byLogin[collaborator.GitHubLogin] = collaborator;
            Persist();
        }

        return collaborator;
    }

    /// <summary>Removes a collaborator by GitHub login (case-insensitive); returns true if one was removed. Removing a
    /// collaborator never revokes an already-issued <see cref="AccessGrant"/> — revocation is separate and explicit.</summary>
    public bool Remove(string gitHubLogin)
    {
        if (string.IsNullOrWhiteSpace(gitHubLogin))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_byLogin.Remove(gitHubLogin))
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    private void Load()
    {
        if (_path is null)
        {
            return;
        }

        var path = _path;
        if (!File.Exists(path))
        {
            // Upgrading a host that stored this under the old name: read it, so nobody's directory disappears
            // because a word changed. Nothing is migrated eagerly — the next Persist writes the new file.
            var legacy = Path.Combine(Path.GetDirectoryName(_path)!, LegacyFileName);
            if (!File.Exists(legacy))
            {
                return;
            }

            path = legacy;
        }

        try
        {
            var collaborators = JsonSerializer.Deserialize<List<Collaborator>>(File.ReadAllText(path), Options);
            foreach (var f in collaborators ?? [])
            {
                if (!string.IsNullOrWhiteSpace(f.GitHubLogin))
                {
                    _byLogin[f.GitHubLogin] = f;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load collaborator directory from {Path}; starting empty.", path);
            _byLogin.Clear();
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byLogin.Values.ToArray(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist collaborator directory to {Path}.", _path);
        }
    }
}
