# Pending message queue & mid-turn steering

| | |
|---|---|
| **Category** | Sessions |
| **Plugin surface** | Core host/protocol feature; steering optionally uses an `ISteerableSession` capability on `IAgentSession` |
| **Priority** | P1 |
| **Rough effort** | M |

## Background

A coding agent's turn can run for a while — reading files, running commands, writing code. During that time it's completely normal for a user to think of something else to say: a correction, a new instruction, a piece of missing context. Right now there's no well-defined answer for what happens if they try. Since Agnes is explicitly a *multi-client* system — the same session reachable from desktop, web, and mobile clients at once — "what happens if I send a message while the agent is busy" isn't a rare edge case, it's something that will happen constantly, including from two different clients on the same session.

There are two genuinely different things a user might want when the agent is busy, and conflating them leads to a confusing feature:

1. **"Hold this for whenever it's ready"** — queue the message so it's sent automatically once the current turn ends, without the user needing to sit and watch for that moment.
2. **"I need to say this right now, into what's already happening"** — inject the message into the turn that's currently running, changing its course, without throwing away the work that turn has already done.

The first (queueing) is always possible to build, since it only requires Agnes to hold a message and send it later — no cooperation from the underlying CLI needed. The second (true in-flight steering) is only possible if the underlying agent protocol actually supports receiving new input mid-turn; Agnes has to be honest about which of its sessions can and can't do that, and fall back sensibly when they can't.

## Current state in Agnes

`IAgentSession` (`/work/src/Agnes.Abstractions/Agent.cs`) has `PromptAsync` (send and await turn-end) and `CancelAsync` (cancel the in-flight turn) — a minimal send/cancel model with no queueing and no distinction between "cancel" and "steer." A client that sends a second prompt while one is already in flight today has no host-defined behavior beyond whatever `SessionManager` happens to do incidentally (most likely: reject the second call, or serialize it behind the first).

## Proposed design

**Pending queue** is a host/protocol-level addition — it needs no new agent-plugin capability, since it works by holding messages in Agnes rather than by asking the underlying CLI for anything:

```csharp
// Agnes.Protocol additions to IAgnesHub
Task EnqueuePendingMessage(string sessionId, IReadOnlyList<ContentBlock> content);
Task ReorderPendingMessage(string sessionId, string messageId, int newIndex);
Task SendPendingNow(string sessionId, string messageId);   // interrupts current turn
Task RemovePendingMessage(string sessionId, string messageId);
```

`SessionManager` owns one `PendingQueue` per session — a single ordered list, not one per client. This is a deliberate choice, not an arbitrary one: Agnes's whole design point is that a session is one shared thing multiple clients converge on, and a per-device queue would mean the phone and the desktop each thinking they know what's "next" for the same session, which breaks that guarantee for no benefit. Queue mutations are themselves appended as `SessionEvent`s, the same way everything else in a session is — which gets multi-client-consistent queue state for free from the sync model Agnes already has, rather than needing a bespoke queue-sync mechanism.

Three send policies make sense as session (or account-level default) settings, because "what should happen when I hit send while the agent is busy" is a genuine, situational preference rather than something with one right answer:
- **Queue in agent** (default) — safest default; nothing is lost, nothing interrupts work already in progress.
- **Interrupt & send** — for when the new message supersedes what's currently happening (e.g. "stop, that's the wrong file").
- **Pending until ready** — queue, but don't auto-send; require an explicit "send now."

A queued message can become impossible to deliver — most plausibly because the session ended, the target turn was cancelled in a way that invalidates the plan the message was written against, or the session itself was deleted before the queue drained. Rather than silently dropping such a message, it moves to a **discarded** sub-list the user can still see and copy from, so a written-but-unsent message is never just gone without explanation.

**Steering** needs an explicit capability check, since whether a message can be injected into an *already-running* turn depends entirely on what the underlying CLI's protocol allows:

```csharp
/// <summary>Optional: a session that can inject a message into its own active turn rather than
/// only being able to cancel-and-restart.</summary>
public interface ISteerableSession
{
    Task<bool> TrySteerAsync(IReadOnlyList<ContentBlock> content, CancellationToken ct = default);
}
```

`SessionManager`'s busy-send handling: if `IAgentSession is ISteerableSession` and the configured policy calls for steering, try `TrySteerAsync`; on `false` (or when the session doesn't implement the interface at all), fall back to the always-available behavior of `CancelAsync` followed by `PromptAsync` with the new content. This fallback is worth keeping simple and uniform — a user shouldn't need to know or care whether their specific agent CLI supports true mid-turn injection; they should just see "steering" work, sometimes via true injection and sometimes via a fast cancel-and-resend that looks the same from the outside. Whether a given CLI's protocol legally allows sending new input while a turn is active needs a per-adapter check, since this is a protocol-level detail that will differ CLI to CLI.

Interrupting a turn (cancel-then-resend, or true steering) is deliberately **not** the same operation as stopping a subagent the main turn may have spawned (see `04-participant-routing-and-subagents-panel.md`) — a subagent that's mid-flight should generally keep working even if the lead turn that launched it gets interrupted and redirected. That distinction only becomes concrete once subagents are addressed by that doc; it's called out here so the two features aren't accidentally built to conflate "the current turn" with "everything currently running in this session."

## Acceptance criteria

- Given a turn in progress, enqueuing a message under the default policy does not interrupt the running turn, and the queued message is sent automatically once the turn ends.
- Given multiple queued messages, reordering, editing, and removing entries all behave as expected, and the resulting order is what actually gets sent.
- Given "Send now" on a queued message, the current turn is interrupted and that message is sent immediately, ahead of anything else still queued.
- Given two clients connected to the same session, a message queued from one client is visible, in the same position, to the other client without a manual refresh.
- Given an `ISteerableSession`-capable session and the Steer policy, sending a message while a turn is running injects it into that turn rather than restarting it; given a non-steerable session with the same policy, the same user action transparently falls back to cancel-then-resend.
- If the target turn or session becomes invalid before a queued message is delivered, the message moves to the discarded list rather than silently disappearing, and remains visible there.
- Interrupting the current turn does not stop a subagent the turn had already launched.

## Open questions

- Does the ACP subset Agnes implements today expose anything steerable at all, or is `ISteerableSession` going to have zero real implementations until protocol support catches up? Worth confirming against the current protocol surface before building the interface — it may be reasonable to ship the pending-queue half alone first, with steering added once at least one adapter can actually support it.
- The interaction between interrupting a turn and any subagent it spawned only has full meaning once `04-participant-routing-and-subagents-panel.md`'s subagent model exists — sequence that piece after it.
