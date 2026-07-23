using Agnes.Abstractions;

namespace Agnes.Host.Hosting;

/// <summary>
/// Reference <see cref="IPromptRegistryProvider"/> that sources skills from a configured local directory (or a
/// local git checkout — Agnes reads the working tree, so a <c>git pull</c> is all "sync from git" needs). NO
/// network: every skill is an immediate child directory containing a <c>SKILL.md</c>; the directory name is
/// the entry id and the frontmatter supplies the title/description. A shared-catalog / HTTP provider is a
/// later drop-in — it implements this same interface and registers at the same plugin point with no core
/// change (see <c>.ideas/extensibility/02-prompts-skills-library.md</c>).
/// </summary>
public sealed class LocalDirectoryRegistryProvider : IPromptRegistryProvider
{
    private readonly string _root;

    /// <param name="rootDirectory">Directory whose child folders are skill bundles.</param>
    /// <param name="id">Stable registry id (defaults to <c>local-dir</c>).</param>
    public LocalDirectoryRegistryProvider(string rootDirectory, string? id = null)
    {
        _root = rootDirectory;
        Id = string.IsNullOrWhiteSpace(id) ? "local-dir" : id;
    }

    public string Id { get; }

    public Task<IReadOnlyList<RegistrySkillEntry>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<RegistrySkillEntry>();
        if (Directory.Exists(_root))
        {
            foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var skillMd = Path.Combine(dir, SkillLibrary.SkillFileName);
                if (!File.Exists(skillMd))
                {
                    continue; // a folder without a SKILL.md isn't a skill bundle.
                }

                var entryId = Path.GetFileName(dir);
                var frontmatter = SkillMarkdown.ParseFrontmatter(File.ReadAllText(skillMd));
                var title = frontmatter.Name is { Length: > 0 } name ? name : entryId;
                entries.Add(new RegistrySkillEntry(entryId, title, frontmatter.Description, dir));
            }
        }

        return Task.FromResult<IReadOnlyList<RegistrySkillEntry>>(entries);
    }

    public Task<LibrarySkill> FetchAsync(string entryId, string destinationDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sourceDir = Path.Combine(_root, entryId);
        var sourceSkillMd = Path.Combine(sourceDir, SkillLibrary.SkillFileName);
        if (!Directory.Exists(sourceDir) || !File.Exists(sourceSkillMd))
        {
            throw new InvalidOperationException($"Registry '{Id}' has no skill '{entryId}'.");
        }

        Directory.CreateDirectory(destinationDir);
        var destSkillMd = Path.Combine(destinationDir, SkillLibrary.SkillFileName);
        var supporting = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceDir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(destinationDir, name);
            File.Copy(file, dest, overwrite: true);
            if (!string.Equals(name, SkillLibrary.SkillFileName, StringComparison.OrdinalIgnoreCase))
            {
                supporting.Add(dest);
            }
        }

        var frontmatter = SkillMarkdown.ParseFrontmatter(File.ReadAllText(destSkillMd));
        var title = frontmatter.Name is { Length: > 0 } fmName ? fmName : entryId;
        return Task.FromResult(new LibrarySkill(entryId, title, destSkillMd, supporting));
    }
}
