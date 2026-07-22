# Participant routing & subagents panel

| | |
|---|---|
| **Category** | Sessions |
| **Plugin surface** | New optional `ISubagentCapableSession` on `IAgentSession` |
| **Priority** | P2 — genuinely complex, sequence after forking (`01`) and steering (`03`) land |
| **Rough effort** | XL |

## Background

Modern coding agents don't always do all their work as one linear thread. A lead agent may delegate a bounded piece of work to a subagent — "review this diff," "investigate this bug in parallel," "go implement this while I keep talking to the user" — and some CLIs go further, running genuinely concurrent side-conversations the main agent doesn't fully control or even see the full detail of. `SessionEvent` already has a `SubagentStartedEvent(SubagentId, Name, ParentAgentId)` (`/work/src/Agnes.Abstractions/SessionEvent.cs`) precisely because Agnes's event model already needs to represent "this event belongs to a subagent's sub-conversation, not the main one."

Without a dedicated view, this multi-thread activity is easy to lose track of inside a single flat transcript — a user watching the main conversation can miss that three other things are happening, or lose the ability to tell which tool-call card in the transcript corresponds to which ongoing thread. A **roster** — a single place that lists everything currently running in a session, main thread and subagents alike — solves that without needing to abandon the transcript as the primary view of *what happened, in order*. The roster is for *what's running right now and how to reach it*; the transcript stays the record of history.

It's also worth being precise about a naming collision that will otherwise confuse both users and this doc's own readers: a **subagent doing parallel work** and **switching the current session's own mode** (e.g. via `IAgentSession.SetModeAsync`) are two unrelated mechanisms. The former spins up another agent thread; the latter changes how the *existing* single thread behaves. They can share vocabulary in casual conversation ("put it in plan mode" vs "kick off a planning subagent") without being the same feature, and the design below keeps them as genuinely separate calls so that ambiguity can't leak into the implementation.

## Current state in Agnes

`SessionEvent` already carries `SubagentStartedEvent`, so Agnes's normalized event model has real awareness that a subagent concept exists in the underlying protocols and is already flowing through the event log for at least the adapters that emit it (the native Claude Code stream mapper maps its Task-tool delegation to this event kind today). What's missing is any UI that surfaces this — no roster, no routing a message to a specific subagent, and no distinction between subagents Agnes can actually control (launch/message/stop) versus ones it can only observe.

## Proposed design

Given the real complexity here — this is one of the more structurally involved features in this backlog — it's worth deliberately building it in two tiers rather than attempting the whole thing at once.

**Tier 1 — visibility only.** `Agnes.Ui.Core`'s session view model gains a roster view driven purely by subagent-related `SessionEvent`s already flowing through the event log — grouping `SubagentStartedEvent` and subsequent events carrying that `AgentId` into per-subagent rows. This needs no new host or protocol work at all: it's a pure client-side rendering feature layered on data Agnes already has. It ships fast, and it correctly represents the case where an adapter can tell Agnes a subagent exists but offers no way to control it — those rows are monitor-only, with no controls that would silently do nothing if pressed.

**Tier 2 — controllable subagents**, once at least one adapter can actually launch/message/stop one as a first-class operation rather than just reporting that one started:

```csharp
public interface ISubagentCapableSession
{
    Task<SubagentHandle> LaunchSubagentAsync(SubagentSpec spec, CancellationToken ct = default);
    Task<IReadOnlyList<SubagentHandle>> ListSubagentsAsync(CancellationToken ct = default);
    Task SendToSubagentAsync(string subagentId, IReadOnlyList<ContentBlock> content, CancellationToken ct = default);
    Task StopSubagentAsync(string subagentId, CancellationToken ct = default);
}

public sealed record SubagentSpec(string Preset, string? InitialPrompt);   // e.g. "review" | "plan" | "delegate"
public sealed record SubagentHandle(string Id, string Preset, SubagentState State);
```

A composer "Send to" control becomes possible once `SendToSubagentAsync` exists as a real target — routing a message to the main thread vs. a specific subagent vs. broadcasting to all of them. The mode-vs-subagent distinction from the background section is enforced structurally, not just by convention: routing a message to a subagent always goes through `SendToSubagentAsync`, and changing the current session's behavior always goes through the existing `SetModeAsync` — there's no shared code path where the two could be confused at the API level.

Some agent CLIs may expose their own native multi-agent primitive that goes beyond simple bounded delegation — for instance, letting the lead agent create a persistent group of collaborating agent instances, address messages to a specific one, broadcast to all of them, and tear the group down explicitly. Where that exists, it's a genuinely CLI-specific capability, not something the general ACP-style protocol surface expresses uniformly — so it belongs as adapter-specific surface in that CLI's own adapter package (e.g. `Agnes.Agents.ClaudeCode`) rather than something `ISubagentCapableSession` tries to model generically. `ISubagentCapableSession` should stay scoped to the bounded-delegation shape above (launch/list/send/stop), which is the common shape most providers can plausibly support; provider-specific extensions layer on top via the adapter, not by growing the shared interface into a superset of every provider's own model.

The roster should keep three groupings — things you can currently start (available presets/actions), things currently running, and things that have finished — because a terminated subagent shouldn't just vanish: keeping it visible (even though it's no longer sendable) matters for tracing back what happened during debugging or review, which is exactly the kind of history Agnes's event-sourced model is otherwise good at preserving.

## Acceptance criteria

- Given a session where the underlying adapter reports subagent activity via `SubagentStartedEvent`, the roster shows a row for each subagent, grouped separately from the main thread, without any host or protocol changes beyond what already exists.
- Clicking a roster row for a subagent and clicking the corresponding tool-call card in the main transcript land on the same detail view of that subagent's activity.
- Given an adapter that only reports subagents (Tier 1) with no control capability, the roster never shows a control (send/stop) that would silently no-op if used.
- Given an `ISubagentCapableSession`-capable adapter, launching a subagent from the roster, sending it a follow-up message, and stopping it all work and are reflected in the roster's running/finished grouping in real time.
- Switching the current session's mode (`SetModeAsync`) never appears in the same UI surface as, or is mistakable for, launching a subagent — they are distinct actions with distinct entry points.
- A subagent that has finished or been stopped remains visible in a "recent/finished" grouping after it's no longer sendable, rather than disappearing from the roster entirely.
- Interrupting or steering the main thread's current turn (see `03-pending-queue-and-steering.md`) does not stop or otherwise affect a subagent that's still running.

## Open questions

- Do any of Agnes's current adapters expose a controllable-subagent primitive today, or does Tier 2 have zero real implementations at launch? Ship Tier 1 regardless — it's cheap, adds real value, and is provider-agnostic since it only consumes events Agnes already normalizes.
- How much of a CLI-native multi-agent primitive (persistent groups, broadcast, etc.), if any of Agnes's adapters end up exposing one, is worth surfacing in the shared roster UI at all, versus being adapter-specific chrome that only shows up for that one agent kind?
