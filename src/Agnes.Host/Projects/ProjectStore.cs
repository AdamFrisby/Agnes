using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Projects;

/// <summary>
/// Persists the host's projects (<c>~/.agnes/projects.json</c>) and resolves a session's project from
/// its repo. "Hybrid" identification: an unseen repo auto-creates a project (seeded from the default,
/// named after the repo) that the user can then edit — so the common case is zero-config, but each
/// project is a real, editable, savable entity. Mirrors the other host stores (atomic tmp-move).
/// </summary>
public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<ProjectStore>? _logger;
    private List<Project> _projects = new();

    public ProjectStore(string path, ILogger<ProjectStore>? logger = null)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<Project> List()
    {
        lock (_gate)
        {
            return _projects.ToArray();
        }
    }

    public Project? Get(string id)
    {
        lock (_gate)
        {
            return _projects.FirstOrDefault(p => p.Id == id);
        }
    }

    /// <summary>Inserts or updates a project by id.</summary>
    public Project Save(Project project)
    {
        lock (_gate)
        {
            var index = _projects.FindIndex(p => p.Id == project.Id);
            if (index >= 0)
            {
                _projects[index] = project;
            }
            else
            {
                _projects.Add(project);
            }

            Persist();
            return project;
        }
    }

    /// <summary>Removes a project (the default project can't be removed).</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var project = _projects.FirstOrDefault(p => p.Id == id);
            if (project is null || project.IsDefault)
            {
                return false;
            }

            _projects.Remove(project);
            Persist();
            return true;
        }
    }

    /// <summary>The catch-all project (created if missing).</summary>
    public Project Default()
    {
        lock (_gate)
        {
            var existing = _projects.FirstOrDefault(p => p.IsDefault);
            if (existing is null)
            {
                existing = new Project { Name = "Default", RepoKey = string.Empty };
                _projects.Insert(0, existing);
                Persist();
            }

            return existing;
        }
    }

    /// <summary>
    /// The project for a repo key ("host/owner/repo"). Exact match wins; an unseen repo is auto-created
    /// (seeded from the default) and persisted; an empty key resolves to the default project.
    /// </summary>
    public Project Resolve(string? repoKey)
    {
        var key = (repoKey ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return Default();
        }

        lock (_gate)
        {
            var match = _projects.FirstOrDefault(p => string.Equals(p.RepoKey, key, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            var seed = Default();
            var created = new Project
            {
                Name = NameFromKey(key),
                RepoKey = key,
                Sandbox = seed.Sandbox,
                McpServers = seed.McpServers,
                CredentialAccount = seed.CredentialAccount,
                Defaults = seed.Defaults,
            };
            _projects.Add(created);
            Persist();
            _logger?.LogInformation("Auto-created project '{Name}' for {RepoKey} (seeded from the default).", created.Name, key);
            return created;
        }
    }

    // "github.com/AdamFrisby/Agnes" -> "Agnes"
    private static string NameFromKey(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash >= 0 && slash < key.Length - 1 ? key[(slash + 1)..] : key;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _projects = JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(_path), Options) ?? new List<Project>();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load projects; starting empty.");
            _projects = new List<Project>();
        }

        Default(); // ensure the default project always exists
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_projects, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist projects.");
        }
    }
}
