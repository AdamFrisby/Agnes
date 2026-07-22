# Prompts, skills and templates library

| | |
|---|---|
| **Category** | Extensibility |
| **Plugin surface** | New `IPromptRegistryProvider` (see `../00-plugin-architecture.md`) |
| **Priority** | P2 |
| **Rough effort** | M |

## Background

Anyone who uses a coding agent regularly ends up typing variations of the same instructions over and over — "review this diff for security issues," "write tests for the function I just changed," a house style guide the agent should always follow. Without somewhere to save and reuse these, users either retype them (error-prone, inconsistent over time) or keep them in a personal scratch file outside Agnes entirely, which defeats the point of having them available from any client. Separately, a growing convention across coding-agent tooling is the "skill" — a bundle of a primary instructions file plus supporting reference files and scripts the agent can pull in for a specific kind of task (say, a bundle of docs and helper scripts for working with a particular internal API). Agnes needs a place to store, organize, and invoke both of these, from any paired client, not just wherever they happen to have been typed the first time.

It's worth keeping several distinct concepts distinct rather than collapsing them into one generic "saved snippets" bucket, because they behave differently:

- A **prompt** is plain text meant to be reused as-is.
- A **skill** is a multi-file bundle (instructions plus supporting files) meant to be loaded as a unit.
- A **template** is a shortcut (a slash token like `/review`) that inserts a prompt, optionally sending it immediately.
- A **system-prompt addition** is a standing instruction attached ambiently to an agent or profile, not something invoked per-message.
- An **external registry** is a source of skills that live outside Agnes's own storage — either already on disk from other tooling, or fetched from a shared catalog.

## Current state in Agnes

None of this exists yet — no saved-prompt, skill-bundle, template, or external-registry concept in Agnes today.

## Proposed design

```csharp
namespace Agnes.Abstractions;

public sealed record LibraryPrompt(string Id, string Title, string MarkdownBody);
public sealed record LibrarySkill(string Id, string Title, string SkillMdPath, IReadOnlyList<string> SupportingFiles);
public sealed record PromptTemplate(string SlashToken, string PromptId, TemplateBehavior Behavior);
public enum TemplateBehavior { Insert, InsertAndSend }

/// <summary>One implementation per external registry source.</summary>
public interface IPromptRegistryProvider
{
    string Id { get; }   // e.g. "local-git", "shared-catalog"
    Task<IReadOnlyList<LibrarySkill>> ListAvailableAsync(CancellationToken ct = default);
    Task<LibrarySkill> FetchAsync(string skillId, CancellationToken ct = default);
}
```

Design notes:

- **Agnes's own library is the single source of truth; everything external is an explicit, tracked import.** This matters because the alternative — treating on-disk files as the live source of truth and having Agnes edit them directly — creates a real risk of silent data loss: if a user (or some other tool) has since modified a file Agnes previously installed, and Agnes overwrites it without checking, that edit is gone with no warning. Instead, Agnes computes a content digest at install time and re-checks it before any subsequent overwrite. If the external file has changed since Agnes installed it, the operation fails with an explicit conflict result rather than guessing which version should win. This is a small amount of extra bookkeeping (one hash comparison) in exchange for never silently destroying a user's edit — a good trade given how easy the alternative failure is to hit in practice.
- **Copy vs. Symlink as an explicit choice at install time**, not a hidden default: Copy writes an independent file (safe, but the two copies can drift); a link (a real symlink on Linux/macOS, and a junction or hardlink on Windows, since Agnes targets Windows as a first-class platform rather than only Unix-like systems) keeps a single Agnes-managed file that install/sync operations keep updated everywhere it's referenced. Making this an explicit per-install choice rather than one hardcoded behavior lets a user pick durability (copy) vs. always-current (link) per skill.
- **Adopt the existing `SKILL.md`-style bundle convention rather than inventing a new format.** A prompt/skill file format is exactly the kind of thing where being different for no reason is pure cost: users who already have skill bundles written for other agent tooling should be able to point Agnes at the same files and have them work, rather than needing to author or convert a parallel Agnes-specific format. This is a case where following an emerging convention is the more defensible engineering choice than inventing something bespoke, purely on the merits of interoperability and reduced user friction.
- **System-prompt additions** are an ordered, individually-toggleable list — attached to `AgentSessionOptions` (a new `SystemPromptAdditionIds` field) or to a session profile (see `../providers/04-profiles.md`) — resolved into the actual launch-time system-prompt injection per adapter, since each agent CLI has its own mechanism for supplying a system prompt.

## Acceptance criteria

- Given a saved prompt, when the user invokes its slash-token template with `Insert` behavior, then the prompt text is placed in the compose box unsent; with `InsertAndSend`, it is sent immediately.
- Given a skill bundle installed via Copy, when the external registry source is later updated, then Agnes's installed copy is unaffected until the user explicitly re-syncs.
- Given a skill bundle installed via Symlink/link, when the external registry source is updated and a sync is run, then the linked copy reflects the update everywhere it's referenced.
- Given an externally-linked skill file that has been modified outside Agnes since it was installed, when Agnes attempts to sync or overwrite it, then the operation fails with an explicit conflict rather than silently discarding the external edit.
- Given a system-prompt addition toggled on for a profile, when a session is started using that profile, then the addition is present in the agent's effective system prompt for that session; toggling it off and starting a new session confirms it is absent.
- Given a `SKILL.md`-format bundle authored for another tool, when it's pointed at as an external asset, then Agnes can list and install it without requiring any reformatting of its contents.
- Deleting a library prompt or skill that a template references leaves the template in a clearly-broken, visibly-flagged state rather than silently failing or resolving to nothing when invoked.

## Open questions

- Should the built-in registry include a live, hosted catalog of shared skills, or launch with local-file and git-based sources only and add a hosted catalog later once there's a real need for discovery beyond what a user's own git remotes provide? Leaning toward starting with local/git sources — a hosted catalog is a meaningfully bigger commitment (moderation, availability, trust) that doesn't need to block the core feature.
- Exact conflict-resolution UX (what the user sees and can do when a sync conflict is detected) needs design work but doesn't change the underlying architecture described above.
