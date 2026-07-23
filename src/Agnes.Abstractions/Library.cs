namespace Agnes.Abstractions;

/// <summary>
/// A saved prompt in the host's library: reusable plain/markdown text keyed by a stable <see cref="Id"/>,
/// so a recurring instruction ("review this diff for security issues") lives once and is available from
/// any paired client rather than being retyped per message.
/// </summary>
public sealed record LibraryPrompt(string Id, string Title, string MarkdownBody);

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

// Deferred (see .ideas/extensibility/02-prompts-skills-library.md), intentionally NOT modelled this pass:
// - Skill bundles: a multi-file `LibrarySkill(Id, Title, SkillMdPath, SupportingFiles)` loaded as a unit.
// - External registries: `IPromptRegistryProvider` (list/fetch skills from local-git / shared-catalog sources).
// - Copy-vs-symlink sync of installed skill files with content-digest conflict detection.
// - System-prompt additions: an ordered, toggleable standing-instruction list on a session/profile.
