# Session handoff across machines

| | |
|---|---|
| **Category** | Connectivity |
| **Plugin surface** | Core host feature; extends `IAgentAdapter` via a new capability interface, `IHandoffCapableAdapter` |
| **Priority** | P1 |
| **Rough effort** | L |
| **Depends on** | `01-relay-and-tunneling.md` (for cross-network workspace transfer) |

## Background

An Agnes session is tied to the host that started it: the agent process, its working directory, and its conversation state all live on one machine. That's fine as long as the machine stays available for the life of the task, but real usage doesn't respect that: a laptop goes to sleep or loses network, a user wants to hand a long-running task from their personal desktop to a beefier cloud-hosted sandbox, or they simply want to keep working on the same task from a different machine without starting the conversation over from scratch.

Agnes already solves a *related* but distinct problem well: its event-sourced session model (see `/work/docs/architecture.md`) lets any number of clients view and interact with the same session concurrently, replaying the full event log on join. That's multi-**client** access to one session's live execution. Session handoff is a different problem — moving the live execution itself, the running agent process and its state, from one **host** to another — and today there is no mechanism for that at all.

## Current state in Agnes

Per `/work/docs/architecture.md`, sessions are event-sourced: every `session/update` from the agent is normalized into a `SessionEvent` and appended to a per-session log with a monotonic sequence number, stored in SQLite on the host. Clients can join, replay from a cursor, and stay in sync — but the agent process producing those events only ever runs on the one host that launched it. There is no way today to take a live session and continue it, under the same session identity and transcript, on a different host. Doing so requires two capabilities Agnes doesn't have yet: (a) a portable way to describe "resume this conversation" that a *different* host's agent process can consume, and (b) optionally, moving the working directory's file contents along with it.

## Proposed design

This is fundamentally a host-to-host operation. It's most naturally modeled as a capability that two hosts negotiate directly with each other, rather than something that requires the relay or any third party to understand — the relay's job (per `01-relay-and-tunneling.md`) is just to move bytes, not to understand session semantics.

```csharp
// Agnes.Abstractions — extends the existing IAgentSession/IAgentAdapter contract
public interface IHandoffCapableAdapter
{
    /// <summary>Whether this agent kind supports resuming on a different machine at all.
    /// Support varies by agent CLI: some expose a native fork/resume primitive over their
    /// own protocol, others don't expose enough state to resume elsewhere at all.</summary>
    HandoffSupport Support { get; }

    /// <summary>Produces a portable resume token capturing what StartSessionAsync's
    /// ResumeSessionId needs, plus anything provider-specific the target host's copy
    /// of this adapter will need to pick the conversation back up.</summary>
    Task<string> ExportHandoffStateAsync(IAgentSession session, CancellationToken ct = default);
}

public enum HandoffSupport { Unsupported, Replay, NativeFork }
```

- **`NativeFork`** — adapters for agent CLIs whose own protocol exposes a resume/fork primitive implement `IHandoffCapableAdapter` and use that mechanism directly, since it's the agent's own authoritative state, not a reconstruction of it. This is the preferred path wherever an agent CLI supports it, because it's exact by construction rather than approximated.
- **`Replay`** — the universal fallback for agent CLIs with no native resume mechanism: replay the session's own `SessionEvent` log (the same log Agnes already keeps for every session) as the seed conversation for a fresh session on the target host. This is deliberately *the same underlying mechanism* proposed for same-host session forking in `../sessions/01-session-forking-and-replay.md` — a cross-host handoff is just a fork whose child session happens to live on a different host. Building same-host forking first means this becomes mostly plumbing: reuse the replay logic, point the new session at a different host's adapter instead of the same host's.
- **Workspace transfer** — an explicit, separate step from resuming the conversation: the source host streams the working directory's contents to the target host over an authenticated host-to-host channel. This should reuse whatever channel the relay's byte-forwarding path already provides (see `01-relay-and-tunneling.md`) rather than standing up a second transport just for file transfer — one authenticated pipe between hosts is easier to secure and reason about than two. Workspace transfer needs an explicit conflict policy (what happens if the target path already has files — write a sibling copy, or require the destination be empty/replaced) and must refuse unsafe source paths (the user's home directory, the filesystem root) to avoid accidentally streaming far more than the intended project directory.

## Acceptance criteria

- **AC1 — Given/When/Then (native fork).** Given a session running on an agent whose adapter reports `HandoffSupport.NativeFork`, when handoff to a second host is initiated, then the session continues on the target host under conversation continuity the agent's own protocol guarantees (verified by comparing pre- and post-handoff conversation state).
- **AC2 — Given/When/Then (replay fallback).** Given a session running on an agent whose adapter reports `HandoffSupport.Replay`, when handoff is initiated, then a new session is created on the target host whose event log, when replayed, reproduces the same conversation history visible to a client that was watching throughout.
- **AC3 — Workspace transfer conflict handling.** Given a target host whose destination working directory already contains files, when a workspace transfer is requested, then the configured conflict policy (sibling copy vs. explicit replace) is honored and never silently overwrites existing files without the policy calling for it.
- **AC4 — Unsafe path rejection.** A handoff request whose source working directory is the user's home directory or the filesystem root is rejected before any transfer begins, with a clear error — never partially executed.
- **AC5 — Adapter without handoff support degrades explicitly.** Given an agent whose adapter reports `HandoffSupport.Unsupported`, a client attempting handoff receives a clear, typed "not supported for this agent" response, not a timeout, a silent no-op, or a generic failure.
- **AC6 — No regression to multi-client viewing.** Existing multi-client session viewing (multiple clients watching one session on its original host, per today's event-sourced replay model) continues to work unmodified for sessions that never use handoff.

## Open questions

- Should workspace transfer always route through the relay (works regardless of NAT/network topology, per `01-relay-and-tunneling.md`) or attempt a direct host-to-host connection when both hosts happen to be reachable from each other (same LAN or overlay network, likely faster)? Supporting both as explicit, separate options is probably worth the complexity given how different "two laptops on the same Wi-Fi" and "laptop to a cloud sandbox" are as use cases — but relay-routed-only is a reasonable, simpler starting point if effort needs to be trimmed.
- Should handoff require both hosts to be online at the same time (a true live handoff), or should Agnes also support an "export now, import later" mode closer to a manual session export/import? The latter is strictly easier to build first, doesn't require solving simultaneous-connectivity coordination, and is useful on its own even before live handoff exists — a reasonable candidate for a first milestone.
