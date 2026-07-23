namespace Agnes.Abstractions;

/// <summary>
/// A saved prompt in the host's library: reusable plain/markdown text keyed by a stable <see cref="Id"/>,
/// so a recurring instruction ("review this diff for security issues") lives once and is available from
/// any paired client rather than being retyped per message.
/// </summary>
public sealed record LibraryPrompt(string Id, string Title, string MarkdownBody)
{
    /// <summary>
    /// When true this prompt is a <em>system-prompt addition</em>: a standing instruction that is collected
    /// (with every other enabled addition) and prepended to a session's effective system prompt at open,
    /// rather than a per-message snippet. Additive and defaulted false so existing prompts are unaffected.
    /// </summary>
    public bool IsSystemPromptAddition { get; init; }
}

/// <summary>What invoking a template's slash-token does to the composer.</summary>
public enum TemplateBehavior
{
    /// <summary>Place the referenced prompt's body in the composer, unsent.</summary>
    Insert,

    /// <summary>Place the referenced prompt's body in the composer and send it immediately.</summary>
    InsertAndSend,
}

/// <summary>
/// A slash-token shortcut (e.g. <c>/review</c>) that expands to a <see cref="LibraryPrompt"/> referenced by
/// <see cref="PromptId"/>. If that prompt no longer resolves the template is <em>broken</em> and must be
/// surfaced as such rather than silently resolving to empty (see <c>PromptLibrary.Resolve</c>).
/// </summary>
public sealed record PromptTemplate(string SlashToken, string PromptId, TemplateBehavior Behavior);

/// <summary>
/// A skill bundle in the host's library: a primary <c>SKILL.md</c> instructions file plus any number of
/// supporting files (reference docs, helper scripts) loaded and managed as ONE unit, keyed by a stable
/// <see cref="Id"/>. Adopts the emerging <c>SKILL.md</c> convention (a markdown file with a <c>name</c>/
/// <c>description</c> frontmatter block) so bundles authored for other agent tooling work unchanged.
/// <see cref="SkillMdPath"/> and <see cref="SupportingFiles"/> are absolute paths to the host-managed copies.
/// </summary>
public sealed record LibrarySkill(string Id, string Title, string SkillMdPath, IReadOnlyList<string> SupportingFiles);

/// <summary>
/// A skill offered by an external registry source (a local directory / git checkout, later a shared catalog),
/// as surfaced by <see cref="IPromptRegistryProvider.ListAsync"/> before it is fetched into the library.
/// <see cref="Source"/> is a human-readable origin (e.g. the on-disk path or catalog url).
/// </summary>
public sealed record RegistrySkillEntry(string Id, string Title, string? Description, string Source);

/// <summary>How an installed skill's files are materialized into an agent-visible target directory.</summary>
public enum SyncMode
{
    /// <summary>Write an independent copy of each file — durable, but the two copies can drift.</summary>
    Copy,

    /// <summary>Link each file back to the Agnes-managed original — always current, kept in sync everywhere
    /// it is referenced. Falls back to a copy on platforms/permissions where a link can't be created.</summary>
    Symlink,
}

/// <summary>
/// A refused overwrite during a skill sync: the target <see cref="Path"/> already exists with content whose
/// digest (<see cref="ExistingDigest"/>) differs from what Agnes would install (<see cref="IncomingDigest"/>),
/// so Agnes surfaces the conflict for the caller/UI to resolve rather than silently discarding the edit.
/// </summary>
public sealed record SkillSyncConflict(string Path, string ExistingDigest, string IncomingDigest);

/// <summary>
/// One implementation per external registry source (see <c>.ideas/extensibility/02-prompts-skills-library.md</c>).
/// Registered as an <see cref="IPluginRegistry{TProvider}"/> plugin point so "where do skills come from" is
/// extensible — a built-in local-directory/git provider ships as the reference, and a shared-catalog/HTTP
/// provider is a later drop-in with no core change.
/// </summary>
public interface IPromptRegistryProvider
{
    /// <summary>Stable id for this registry source, e.g. <c>local-dir</c> or <c>shared-catalog</c>.</summary>
    string Id { get; }

    /// <summary>The skills this source currently offers.</summary>
    Task<IReadOnlyList<RegistrySkillEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Materializes the entry identified by <paramref name="entryId"/> into <paramref name="destinationDir"/>
    /// (creating it if needed) and returns the resulting <see cref="LibrarySkill"/> pointing at the copied
    /// files. Never reaches back into Agnes's own library — the host decides whether to import the result.
    /// </summary>
    Task<LibrarySkill> FetchAsync(string entryId, string destinationDir, CancellationToken ct = default);
}
