# Review comments on files and diffs

| | |
|---|---|
| **Category** | Git & files |
| **Plugin surface** | Core host feature — no new plugin interface needed |
| **Priority** | P2 |
| **Rough effort** | M |

## Background

Reviewing a coding agent's work is a core Agnes workflow: a session produces a diff, and a human needs to leave feedback on specific parts of it — "this needs a null check," "wrong variable name here" — without that feedback getting lost or losing its anchor point as the file keeps changing underneath it. Plain chat messages don't work well for this: they float free of the code, so after a few more turns nobody can tell which line a comment three messages back was actually about.

The other requirement is durability across sessions. Agents are often re-run against the same project days apart, in fresh sessions. A comment like "this validation logic is fragile" is a fact about the *project*, not about the one conversation where it happened to get typed — it should still be visible and actionable the next time anyone (human or agent) opens that file, not scoped to a session that has since ended.

## Current state in Agnes

Agnes has no review-comment concept today. `Agnes.Ui.Core` already has a `DiffModel`, the rendering component diffs are shown through — that's the natural attachment point for a comment UI — but there's no data model for anchoring a comment to a specific line, and no persistence for it independent of a session.

## Proposed design

```csharp
// Agnes.Abstractions
public sealed record ReviewComment
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }      // workspace-scoped, not session-scoped
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public required string ContentHash { get; init; }     // hash of the line's content at comment-time, for drift detection
    public required string Text { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

Design notes:

- **Anchor by line number plus a content hash of that line, not line number alone.** A comment anchored purely by line number silently points at the wrong code the moment a line is inserted or deleted above it — the comment would appear to "move" without anyone noticing, which is worse than the file simply not having comments. Storing a hash of the commented line's content at creation time lets Agnes detect that mismatch and tell the user the comment's anchor no longer matches — the line may have moved or changed — instead of silently misattributing feedback to different code. The exact relocation heuristic beyond that (e.g. searching nearby lines for the same content) is an implementation detail worth prototyping against real diffs, not something to lock in up front.
- **Scope comments to the project, not the session.** Storing `ReviewComment` against Agnes's existing per-project storage (`ProjectStore`/`ProjectMapping`) rather than session storage means a comment survives past the session it was created in, matching how a human reviewer actually thinks about feedback — it's about the code, not about one conversation. This is a natural extension of storage Agnes already has, not a new persistence concept.
- **Rendering** — `DiffModel` looks up comments for the file currently displayed and renders them as structured cards grouped by file with a "jump to line" action, checking each comment's stored hash against the line currently at that position to decide whether to flag it stale.
- **Delivering a comment into a session** so the agent can act on it reuses the existing `IAgentSession.PromptAsync(IReadOnlyList<ContentBlock> content, ...)` call with a `ResourceLinkContent` block pointing at the file/line — no protocol change needed, since Agnes's content-block model already supports referencing a file location.

## Acceptance criteria

- Given a comment left on line N of a file, when the file is unchanged since the comment was created, then the comment renders anchored to line N with no stale warning.
- Given a comment left on line N, when lines are inserted above N such that the original content has moved to a different line number, then the comment either relocates to follow its content or is explicitly flagged as stale — it never silently renders attached to different, unrelated code.
- Given a comment created in one session, when a new session is started against the same project, then the comment is still visible and addressable — comments are not deleted or hidden when their originating session ends.
- Given a review comment referencing a file and line, when the user sends it into an active session, then the agent receives a prompt that identifies the specific file and line being referenced, not just free-floating text.
- Given multiple comments across multiple files in one project, when the review surface is opened, then comments are grouped by file and each has a working "jump to line" action that scrolls the diff view to the anchored location.
- Deleting a comment removes it from the project's stored comments and it no longer renders in any session's diff view.

## Open questions

- Exact drift-tolerant relocation algorithm (exact-hash-match-or-flag-stale vs. fuzzy nearest-line search) — worth prototyping against a handful of real diffs before locking in behavior.
- Should resolved/addressed comments be archived rather than deleted, to preserve a review history per project? Leaning toward yes but not required for a first version.
