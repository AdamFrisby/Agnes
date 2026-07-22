# Deep git integration

| | |
|---|---|
| **Category** | Git & files |
| **Plugin surface** | Extends the existing `Agnes.Host` `GitService`/`GitRemote`; new `IGitHostProvider` for PR/MR surfaces (see `../00-plugin-architecture.md`) |
| **Priority** | P1 |
| **Rough effort** | L |
| **Depends on** | `../sessions/06-tool-timeline-normalization.md` (for full session-attributed changed-file scoping) |

## Background

Agnes sessions run a coding agent against a real git working copy — every edit the agent makes lands directly on disk in the user's clone. Today, the only git-aware primitives are checking status and committing; everything else (staging selectively, stashing, switching branches, pulling, pushing, opening a PR) requires the user to drop out of Agnes into a separate terminal or GUI. That's a real workflow break for a product whose entire premise is driving a coding agent from anywhere without needing a local terminal — a user reviewing a session from their phone can't run `git stash` on that phone today, and even on desktop, bouncing between Agnes and a terminal for routine git operations undercuts the value of having a unified session surface at all.

The other half of the problem is safety. An agent session can generate a lot of git activity in a short time (stashes, branch switches, multiple commits), often while the user isn't watching every keystroke. Git operations that rewrite history or discard work are exactly the kind of thing that should be hard to trigger by accident from a remote client — a fat-fingered tap on a phone should never be able to lose committed work.

## Current state in Agnes

`GitService`/`GitRemote` already provides `GetGitStatus` and `GitCommit`, plus a credential broker for GitHub App and stored-token authentication. That's a working foundation but a narrow one: no stash management, no branch switching, no pull/push, no PR/MR detection or checkout, no way to scope "what changed" to something narrower than the whole repository, and no assisted commit-message generation.

## Proposed design

Extend the existing `GitService` surface — this is additive to something Agnes already has, not a new subsystem — with the missing operations, and add one genuinely new plugin point for forge detection:

```csharp
// Agnes.Host — additions alongside the existing GetGitStatus/GitCommit
Task<GitStashInfo> StashAsync(string sessionId, CancellationToken ct = default);
Task PopStashAsync(string sessionId, string stashId, CancellationToken ct = default);
Task SwitchBranchAsync(string sessionId, string branch, bool carryStash, CancellationToken ct = default);
Task FastForwardPullAsync(string sessionId, CancellationToken ct = default);
Task PushAsync(string sessionId, bool publishBranch, CancellationToken ct = default);

// Agnes.Abstractions — new plugin point, one implementation per forge
public interface IGitHostProvider
{
    string Id { get; }   // "github" | "gitlab" | "bitbucket"
    bool Matches(string remoteUrl);
    Task<IReadOnlyList<PullRequestInfo>> ListOpenPullRequestsAsync(string remoteUrl, CancellationToken ct = default);
    Task CheckoutPullRequestAsync(string sessionWorkingDirectory, string pullRequestId, CancellationToken ct = default);
}
```

Design notes:

- **Changed-file scoping** ("this turn" / "this session" / "whole repo") doesn't need a new tracking mechanism. Agnes's event-sourced session log already records, per `SessionEvent`, which tool-call events touched which files; once `../sessions/06-tool-timeline-normalization.md` lands, this becomes a straightforward filter over `NormalizedToolCall` events by file path and time range. Scoping "changed files" to a session is then just a query, not a new subsystem to build and keep in sync.
- **Commit-message generation** is best modeled as a one-shot, non-interactive agent run over the staged diff, rather than a bespoke summarization call: it reuses the same "spin up a session, give it a bounded task, take its output, tear it down" primitive that other one-shot generation needs (e.g. session-summary generation). Building that primitive once, generically, and sharing it avoids two independent ad hoc implementations that drift apart over time.
- **Safety rules must be enforced server-side, not just hidden in the UI.** `GitService` should reject a fast-forward-incompatible pull or a destructive history rewrite at the API layer — returning a typed error — rather than relying on the client simply not exposing a button for it. A client is not a trust boundary: a scripted client, a future third-party client, or a bug in one UI surface should not be able to bypass a safety rule that only lives in presentation code. Any `index.lock` recovery path must be validated (confirm no other process holds the lock) rather than a blanket "force it" option, since blindly deleting a lock file out from under a concurrent git process can corrupt the repository.

## Acceptance criteria

- Given a session with uncommitted changes, when the user requests a stash, then the working tree is clean afterward and the stash is listed with enough metadata (branch, timestamp, file count) to identify it later; popping it restores the exact original state.
- Given a session on a branch with uncommitted changes, when the user switches branches with "carry stash" enabled, then the changes are stashed, the branch switch succeeds, and the stash is reapplied on the new branch (or the operation fails cleanly with no data loss if the stash can't apply due to conflicts).
- Given a remote that is not fast-forwardable, when the user requests a pull, then the operation fails with a clear, actionable error rather than performing a merge or rebase silently.
- Given a repository whose remote matches a configured `IGitHostProvider` (e.g. a `github.com` or GitHub Enterprise remote URL), when the user opens the PR surface, then open pull requests against that remote are listed, and checking one out opens it in a session pointed at that PR's branch.
- Given "this turn," "this session," and "whole repository" changed-file scopes, when each is selected, then the file list returned matches exactly the files touched by that scope — no over- or under-inclusion — verified against a session with multiple turns touching overlapping and disjoint files.
- No safety rule exists only in the UI: a direct API call attempting a non-fast-forward pull or an exposed destructive rewrite operation is rejected by `GitService` itself, not merely omitted from the client.
- Existing `GetGitStatus`/`GitCommit` behavior and the existing credential broker continue to work unmodified after these additions land.

## Open questions

- Opening a checked-out PR "in a dedicated worktree session" implies a worktree-aware session-launch path (an `AgentSessionOptions.WorkingDirectory` pointed at a git worktree rather than the primary clone). Needs a check of whether `Agnes.Sandbox`'s per-session sandbox model composes cleanly with git worktrees, since both want an isolated working directory per session — worth prototyping before committing to the approach.
- Exact commit-message-generation UX (auto-generate on every commit vs. an explicit "suggest a message" action) is a product decision, not an architectural one — can be decided at implementation time.
