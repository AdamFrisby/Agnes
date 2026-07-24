using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Projects;

/// <summary>
/// Persists this host's checkouts (<c>~/.agnes/checkouts.json</c>) — the on-disk clones/worktrees that make up
/// the host's side of the multi-machine workspace model (<c>connectivity/05</c>). A second, purpose-specific
/// store rather than an overload of <see cref="ProjectStore"/>: a project is a per-repo session-config bundle,
/// a checkout is a lifecycle-managed working copy — different lifetimes, different keys. Mirrors the other host
/// stores' atomic tmp-move persistence.
/// </summary>
public sealed class CheckoutStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<CheckoutStore>? _logger;
    private List<CheckoutRecord> _checkouts = new();

    public CheckoutStore(string path, ILogger<CheckoutStore>? logger = null)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<CheckoutRecord> List()
    {
        lock (_gate)
        {
            return _checkouts.ToArray();
        }
    }

    public CheckoutRecord? Get(string id)
    {
        lock (_gate)
        {
            return _checkouts.FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>Inserts or updates a checkout by id.</summary>
    public CheckoutRecord Save(CheckoutRecord checkout)
    {
        lock (_gate)
        {
            var index = _checkouts.FindIndex(c => c.Id == checkout.Id);
            if (index >= 0)
            {
                _checkouts[index] = checkout;
            }
            else
            {
                _checkouts.Add(checkout);
            }

            Persist();
            return checkout;
        }
    }

    /// <summary>Removes a checkout by id; returns whether one was present.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _checkouts.RemoveAll(c => c.Id == id) > 0;
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _checkouts = JsonSerializer.Deserialize<List<CheckoutRecord>>(File.ReadAllText(_path), Options) ?? new List<CheckoutRecord>();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load checkouts; starting empty.");
            _checkouts = new List<CheckoutRecord>();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_checkouts, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist checkouts.");
        }
    }
}
