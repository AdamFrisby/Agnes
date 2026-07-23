using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host's library of skill bundles: a <c>SKILL.md</c> plus supporting files, stored and managed as ONE
/// unit. Mirrors <see cref="PromptLibrary"/> (single lock, atomic tmp-move index, load-tolerant of a
/// missing/corrupt file). The metadata index lives at <c>~/.agnes/skill-library.json</c>; each skill's files
/// are copied into a managed per-skill directory (<c>~/.agnes/skills/&lt;id&gt;/</c>) so Agnes owns the source
/// of truth. Installing a skill into an agent-visible workspace is a separate, explicit step
/// (<see cref="SkillInstaller"/>); this class just stores and loads the bundle.
/// </summary>
public sealed class SkillLibrary
{
    /// <summary>The index file name under the data directory.</summary>
    public const string FileName = "skill-library.json";

    /// <summary>The subdirectory (under the data directory) that holds managed skill bundles.</summary>
    public const string SkillsDirectoryName = "skills";

    /// <summary>The conventional primary instructions file name inside a skill bundle.</summary>
    public const string SkillFileName = "SKILL.md";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _root;
    private readonly string? _indexPath;
    private readonly ILogger<SkillLibrary>? _logger;
    private State _state = new();

    /// <param name="directory">
    /// Directory to manage skills under (production passes <see cref="PromptLibrary.DefaultDirectory"/>). When
    /// null or blank the library is disabled: <see cref="List"/> is empty and <see cref="Save"/> throws — skill
    /// bundles are inherently on-disk, so there is no in-memory mode.
    /// </param>
    public SkillLibrary(string? directory = null, ILogger<SkillLibrary>? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _root = Path.Combine(directory, SkillsDirectoryName);
            _indexPath = Path.Combine(directory, FileName);
        }

        _logger = logger;
        Load();
    }

    /// <summary>Saved skills, ordered by title (never null).</summary>
    public IReadOnlyList<LibrarySkill> List()
    {
        lock (_gate)
        {
            return _state.Skills.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>The skill with this id, or null.</summary>
    public LibrarySkill? Get(string id)
    {
        lock (_gate)
        {
            return _state.Skills.FirstOrDefault(s => s.Id == id);
        }
    }

    /// <summary>
    /// Imports a skill bundle: copies <paramref name="sourceSkillMdPath"/> and each of
    /// <paramref name="supportingFiles"/> into a managed per-skill directory, then upserts the record and
    /// persists the index. The stored <see cref="LibrarySkill"/> points at the managed copies. When
    /// <paramref name="title"/> is blank the <c>SKILL.md</c> frontmatter <c>name</c> is used (else the id).
    /// </summary>
    public LibrarySkill Save(string? id, string title, string sourceSkillMdPath, IReadOnlyList<string> supportingFiles)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("This skill library has no managed directory configured.");
        }

        var skillId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("n") : id.Trim();
        var skillDir = Path.Combine(_root, skillId);
        if (Directory.Exists(skillDir))
        {
            Directory.Delete(skillDir, recursive: true); // fresh copy — the bundle is loaded/managed as a unit.
        }

        Directory.CreateDirectory(skillDir);

        var managedSkillMd = Path.Combine(skillDir, SkillFileName);
        File.Copy(sourceSkillMdPath, managedSkillMd, overwrite: true);

        var frontmatter = SkillMarkdown.ParseFrontmatter(File.ReadAllText(managedSkillMd));
        var effectiveTitle = !string.IsNullOrWhiteSpace(title)
            ? title.Trim()
            : (frontmatter.Name is { Length: > 0 } name ? name : skillId);

        var managedSupporting = new List<string>();
        foreach (var file in supportingFiles)
        {
            var dest = Path.Combine(skillDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
            managedSupporting.Add(dest);
        }

        var skill = new LibrarySkill(skillId, effectiveTitle, managedSkillMd, managedSupporting);
        lock (_gate)
        {
            _state.Skills.RemoveAll(s => s.Id == skillId);
            _state.Skills.Add(skill);
            Persist();
        }

        return skill;
    }

    /// <summary>Deletes a skill by id, removing its managed files as a unit; returns true if one was removed.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_state.Skills.RemoveAll(s => s.Id == id) == 0)
            {
                return false;
            }

            Persist();
        }

        if (_root is not null)
        {
            var skillDir = Path.Combine(_root, id);
            if (Directory.Exists(skillDir))
            {
                Directory.Delete(skillDir, recursive: true);
            }
        }

        return true;
    }

    private void Load()
    {
        if (_indexPath is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_indexPath))
            {
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_indexPath), Options) ?? new State();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load skill library from {Path}; starting empty.", _indexPath);
            _state = new State();
        }
    }

    private void Persist()
    {
        if (_indexPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            var tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Options));
            File.Move(tmp, _indexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist skill library to {Path}.", _indexPath);
        }
    }

    private sealed class State
    {
        public List<LibrarySkill> Skills { get; init; } = [];
    }
}
