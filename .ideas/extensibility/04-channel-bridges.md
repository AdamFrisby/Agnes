# Channel bridges (e.g. Telegram)

| | |
|---|---|
| **Category** | Extensibility |
| **Plugin surface** | New `IChannelBridge`, modeled alongside `INotificationChannel` (see `../00-plugin-architecture.md`) |
| **Priority** | P3 — real but niche relative to the rest of the backlog |
| **Rough effort** | M per bridge |

## Background

Agnes already needs to notify a user when a session needs their attention — a turn finished, a permission request is waiting, a decision is needed — and it does that through push notifications and desktop toasts. A channel bridge extends that same idea one step further: instead of (or alongside) opening an Agnes client to respond, the user can reply from a chat app they already have open — approve a permission request, or send a quick follow-up instruction, from a chat conversation (e.g. Telegram), without switching context to Agnes itself. For users who already live in a messaging app during their workday, this can meaningfully lower the friction of responding to a waiting session, especially for quick approve/deny decisions that don't need the full session view.

This is a genuinely useful feature for the users who want it, but it's also a narrower need than most of the rest of this backlog — most users will be well served by push notifications and the normal client UI, and a channel bridge is an additive convenience for a specific workflow rather than a gap that blocks core usage. It's included here for completeness, not because it's an urgent build.

## Current state in Agnes

Nothing exists in this area today, and nothing conceptually adjacent either — the closest related work is push notifications and desktop toasts, which are outbound-only.

## Proposed design

Model a channel bridge as a specialization of the notification-channel plugin point, not a wholly separate system, since the events that should trigger a bridge message are the same ones that already drive push notifications (turn-ready, permission requests, user-action requests) — and a bridge additionally needs an inbound path, which push notifications don't:

```csharp
namespace Agnes.Abstractions;

public interface IChannelBridge
{
    string Id { get; }   // "telegram" | ...
    Task SendAsync(string externalChatId, string message, ChannelBridgeContext context, CancellationToken ct = default);

    /// <summary>Inbound: the bridge's own webhook/poll handler calls back into Agnes.Host to
    /// deliver a message as if it came from a paired client (e.g. reply "allow" to a permission prompt).</summary>
    event Func<InboundChannelMessage, Task>? OnInboundMessage;
}
```

Design notes:

- **Reuse the notification-trigger events rather than inventing a parallel event source.** The set of moments a user might want to act on — turn complete, permission requested, explicit user-action requested — is already defined and already wired to push notifications; a channel bridge is just another delivery target for those same events, plus a reply path back in. Building a second, bridge-specific "what's worth notifying about" concept would duplicate logic that already has to stay correct in one place.
- **Inbound messages need to resolve to a real, authorized identity — not just "a message arrived that looks like a reply."** An inbound "allow" from a chat app is, functionally, the same trust-sensitive action as approving a permission request from a paired device, so it must go through the same authorization path: `Agnes.Host` maps the external chat id to an identity established during an explicit linking step (reusing whatever identity/authorization model device pairing already uses — see `../security/02-enterprise-auth.md` and `../connectivity/04-device-linking-and-restore.md`), not an implicit trust based solely on knowing a chat id. Treating an inbound bridge message with less scrutiny than a request from a paired app client would create a real hole: anyone who could message that bot/chat could otherwise approve destructive actions.
- Each bridge implementation owns its own transport specifics (webhook vs. long-poll, message formatting, rate limits) behind the common interface — the linking/authorization and event-triggering logic stays shared.

## Acceptance criteria

- Given a permission request fires while a channel bridge is linked and configured for that session, then a message describing the request is sent to the linked external chat within the same latency bounds as an equivalent push notification.
- Given a user replies "allow" (or the bridge's equivalent affirmative action) from the linked chat, then the permission request is resolved exactly as if a paired client had approved it, and the session proceeds accordingly.
- Given an inbound message arrives from a chat id that has not completed the explicit linking/authorization step, then it is not treated as an authorized action on any session — no permission request can be approved and no session can be steered by an unlinked chat id.
- Given a bridge is unlinked/removed, then no further outbound messages are sent to that chat and any previously-linked identity mapping is invalidated — a stale link cannot later be reused to approve actions.
- Given the same triggering event (e.g. a permission request) fires while both push notifications and a channel bridge are configured, then both are delivered independently — one delivery path does not need to succeed or fail based on the other.
- A new bridge (e.g. a second chat platform) can be added as a new `IChannelBridge` implementation registered through the plugin system without changes to `Agnes.Host`'s core notification-triggering logic.

## Open questions

- Given this is both the least-specified idea in this backlog and the lowest priority, it's a reasonable candidate to defer past a first pass entirely and build only if users actually request it — included here for completeness rather than as a near-term target.
- Message formatting/UX per platform (how much session context to include in a single chat message before it becomes unreadable) is bridge-specific product work, not an architectural question this doc needs to resolve.
