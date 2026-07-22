# Multi-machine workspace model

| | |
|---|---|
| **Category** | Connectivity |
| **Plugin surface** | Core host/protocol feature; composes with `IGitHostProvider` (see `../git-and-files/01-deep-git-integration.md`) |
| **Priority** | P2 |
| **Rough effort** | L |
| **Depends on** | `../git-and-files/01-deep-git-integration.md` (worktree management), `../connectivity/03-session-handoff.md` (related but distinct — see Background) |

## Background

A developer working on one project often isn't working on just one machine: a laptop for quick edits, a beefier cloud or on-prem box for anything heavy, maybe a second machine entirely for a specific environment. Today, Agnes treats each of those as an unrelated host with its own independent set of sessions and working directories — there's no concept that "the checkout of this repo on my laptop" and "the checkout of the same repo on my cloud box" are two local copies of the *same logical project*, even though a user thinks of them that way and regularly wants to act on that relationship (e.g. "start a session on whichever machine already has this branch checked out," or "clean up every machine's copy of a branch I just merged").

This is a different problem from `../connectivity/03-session-handoff.md`, which moves one *live session's* execution from one machine to another. This feature is about the *project* as a persistent, multi-machine concept — independent of whether any session is currently running — with each machine's actual on-disk copy (a specific clone, working directory, or worktree) tracked as a distinct, lifecycle-managed thing in its own right, separate from the logical project it belongs to.

## Current state in Agnes

Agnes's `ProjectStore`/`ProjectMapping` (per the host's existing per-repo project bundles) already groups sessions by project on a *single* host — that's the right foundation, but it stops at the host boundary: there's no representation of the same project existing as separate checkouts across multiple hosts, and no lifecycle management (create/switch-branch/clean-up) for those checkouts as a set.

## Proposed design

Two new concepts, deliberately kept distinct because they answer different questions:

```csharp
namespace Agnes.Abstractions;

/// <summary>A logical project, potentially checked out on more than one host. Spans hosts;
/// has no filesystem presence of its own.</summary>
public sealed record Workspace(string Id, string DisplayName, string RepositoryUrl);

/// <summary>One host's actual on-disk copy of a Workspace — a specific clone or worktree,
/// with its own lifecycle independent of any other host's checkout of the same Workspace.</summary>
public sealed record Checkout(string Id, string WorkspaceId, string HostId, string Path, string? Branch);
```

- **A `Workspace` is the "what project is this" identity**; a `Checkout` is "this specific host's copy of it, right now, on this branch." A user might have three `Checkout`s of one `Workspace` (laptop on a feature branch, cloud box on `main`, a CI-adjacent machine on a release branch) — modeling them separately means asking "which machines have this project checked out, and on what branch" is a direct query rather than something inferred by matching repository URLs across otherwise-unrelated per-host project records.
- **Checkout lifecycle** — create (clone or add a worktree on a specific host), switch branch, and clean up (remove the checkout, optionally only after confirming no uncommitted work would be lost) — becomes explicit host operations scoped to a `Checkout`, reusing the git primitives `../git-and-files/01-deep-git-integration.md` already proposes (stash/branch-switch) rather than duplicating them.
- **Worktrees, not always full clones.** Where a host already has one `Checkout` of a `Workspace` and a user wants a second, independent working copy on the *same* host (e.g. to work two branches in parallel without stashing), a `Checkout` backed by a git worktree of the existing clone is the natural choice — this is the same worktree-management question `../git-and-files/01-deep-git-integration.md` flags as an open question for PR-checkout sessions, so building it once, generically, here, and reusing it there (rather than each inventing its own worktree handling) avoids two independent implementations drifting apart.
- **New-session flows gain a "which checkout" step** where relevant: starting a session against a `Workspace` that has more than one active `Checkout` should let the user (or, for `../extensibility/03-automations.md`'s multi-host assignment, the scheduling logic) pick which machine's copy to use, rather than requiring the user to already know and separately navigate to the right host.

## Acceptance criteria

- Given a repository checked out on two different hosts, both checkouts can be associated with the same `Workspace`, and a query for "which machines have this project" returns both, along with each one's current branch.
- Creating a new `Checkout` of an existing `Workspace` on a host that doesn't yet have one performs a real clone (or worktree add, if another checkout already exists on that host) and the result is immediately usable as a session's working directory.
- Given a `Checkout` with uncommitted changes, attempting to remove it without an explicit force/confirm flag fails with a clear error identifying the uncommitted work, rather than silently discarding it.
- Given a `Workspace` with multiple active checkouts, starting a new session against that workspace presents the available checkouts (host + branch) for the user to choose from, rather than defaulting silently to an arbitrary one.
- A second `Checkout` of a `Workspace` on a host that already has one is created as a worktree of the existing clone, not a second independent full clone, when the underlying filesystem/host supports it.
- Removing a `Workspace` itself (as opposed to one of its checkouts) does not delete any host's on-disk checkout — it only removes the logical grouping; each `Checkout` must be explicitly removed on its own terms.
- Non-regression: a host with only ever having had one checkout of one project continues to behave exactly as Agnes's existing single-host `ProjectStore`/`ProjectMapping` does today — the Workspace/Checkout model is additive, not a required migration for existing simple setups.

## Open questions

- Should `Workspace` identity be inferred automatically from matching repository URLs across hosts, or does a user need to explicitly link two independently-created per-host projects into one `Workspace`? Automatic inference is more convenient but risks incorrectly merging two genuinely unrelated forks/mirrors that happen to share a URL pattern — leaning toward explicit linking with an automatic *suggestion* when a URL match is detected, rather than fully automatic merging.
- How this interacts with `../connectivity/01-relay-and-tunneling.md`'s eventual multi-host reachability — querying "which machines have this workspace" is only useful if those machines are actually reachable, so this feature's value scales with how much of the connectivity backlog has already landed.
