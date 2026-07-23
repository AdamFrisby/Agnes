using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Social;

/// <summary>
/// The host owner's friend directory — a set of <see cref="Friend"/> records keyed by canonical GitHub login
/// (case-insensitive), persisted to <c>~/.agnes/friends.json</c>. Mirrors the other host stores exactly:
/// single lock, atomic tmp-move persist, load-tolerant of a missing/corrupt file. A friend carries no secret
/// and being in the directory grants nothing on its own — it only makes a user <em>eligible</em> to be granted
/// access via a separate, explicit <see cref="AccessGrant"/>. So the whole store is safe to list to a client
/// and to serialise to disk.
/// </summary>
public sealed class FriendStore
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "friends.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger<FriendStore>? _logger;
    private readonly Dictionary<string, Friend> _byLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="directory">
    /// Directory to persist the directory under (production passes <c>~/.agnes</c>). When null or blank the
    /// store is in-memory only and never touches disk — used by tests.
    /// </param>
    public FriendStore(string? directory = null, ILogger<FriendStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _logger = logger;
        Load();
    }

    /// <summary>All friends, ordered by GitHub login (never null).</summary>
    public IReadOnlyList<Friend> List()
    {
        lock (_gate)
        {
            return _byLogin.Values.OrderBy(f => f.GitHubLogin, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>The friend with this GitHub login (case-insensitive), or null.</summary>
    public Friend? Find(string gitHubLogin)
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

    /// <summary>Whether this GitHub login is an explicit friend (case-insensitive).</summary>
    public bool Contains(string gitHubLogin) => Find(gitHubLogin) is not null;

    /// <summary>Upserts a friend keyed by <see cref="Friend.GitHubLogin"/> and persists it; returns the stored
    /// record.</summary>
    public Friend Add(Friend friend)
    {
        lock (_gate)
        {
            _byLogin[friend.GitHubLogin] = friend;
            Persist();
        }

        return friend;
    }

    /// <summary>Removes a friend by GitHub login (case-insensitive); returns true if one was removed. Removing a
    /// friend never revokes an already-issued <see cref="AccessGrant"/> — revocation is separate and explicit.</summary>
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
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var friends = JsonSerializer.Deserialize<List<Friend>>(File.ReadAllText(_path), Options);
            foreach (var f in friends ?? [])
            {
                if (!string.IsNullOrWhiteSpace(f.GitHubLogin))
                {
                    _byLogin[f.GitHubLogin] = f;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load friend directory from {Path}; starting empty.", _path);
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
            _logger?.LogWarning(ex, "Failed to persist friend directory to {Path}.", _path);
        }
    }
}
