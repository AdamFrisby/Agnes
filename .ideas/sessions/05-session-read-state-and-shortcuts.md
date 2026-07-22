# Session read/unread state & new-session shortcuts

| | |
|---|---|
| **Category** | Sessions |
| **Plugin surface** | Core host/protocol feature — no new plugin interface needed |
| **Priority** | P2 — small, high polish-value |
| **Rough effort** | S |

## Background

Two small, unrelated conveniences that both matter for how it feels to use Agnes day to day across multiple sessions and multiple devices:

**Read/unread state.** Agnes is designed around dozens of agents running across multiple hosts, reachable from more than one client. Once a user has more than a handful of sessions open, "which of these have new activity I haven't looked at yet" becomes a real navigational question — the same problem email and chat apps solve with unread indicators. Because Agnes sessions are explicitly multi-client (the same session viewable from a phone and a desktop at once), read state has to be a property of the *session*, not of any one device — otherwise a user who reads something on their phone would still see it as unread on their desktop, which defeats the purpose of showing unread state at all.

**New-session shortcuts.** Starting a new session involves picking a machine, a working directory, an agent/engine, permission settings, and possibly a model or MCP configuration. Most of the time, a user's next session in the same project wants exactly the same configuration as their last one there — re-picking all of it by hand every time is friction with no benefit. This is a convenience feature, not a persistence feature: it should reuse the *configuration* of a prior session, never its transcript or identity, so it's clearly "start fresh, same setup" and not confusable with resuming.

## Current state in Agnes

Agnes's event-sourced model already tracks a monotonic `Sequence` per session (see `/work/docs/architecture.md`), which is exactly the primitive read state needs — "unread" reduces to "the read cursor is behind the session's current head." There's no cursor stored today, though, and no shortcut for relaunching a new session with a prior session's configuration. The shortcut idea is related to, but lighter-weight than, named/saved configurations (`../providers/04-profiles.md`): this is about reusing the *last-used* configuration, not creating or picking a *named* one.

## Proposed design

**Read state** — a small addition to `Agnes.Protocol`/`Agnes.Host`:

```csharp
// IAgnesHub
Task MarkSessionRead(string sessionId);
Task MarkSessionUnread(string sessionId);
```

`SessionManager` stores one `ReadCursor` (a sequence number) per session, scoped to the account rather than to a device — the reasoning above (a session is one shared thing across a user's clients) applies directly here, and it's also the simpler implementation: one cursor per session rather than a cursor per (session, device) pair. Cursor updates push to connected clients via the same notification path session events already use, so every client's unread indicator converges without needing a separate sync mechanism. Two behaviors are worth being explicit about: marking a session unread is a real, sticky user action — it should not silently flip back to read just because the session happens to still be open in a client's view — and read state should be hidden entirely for sessions with nothing to be unread about (empty sessions, or sessions the user only has view access to, e.g. via `../collaboration/02-session-sharing-and-public-links.md`).

**New-session shortcuts** — no new interface needed. `SessionManager` already has (or can reconstruct from stored session metadata) every session's originating `AgentSessionOptions`, since it needed those to start the session in the first place. "Same setup" is just reading a prior session's options and pre-filling a new-session request with them — explicitly excluding `ResumeSessionId`, since carrying that over would make it a resume rather than a fresh session with a reused configuration, defeating the point of the feature. Two entry points make sense: relaunching "the last session in this project" (a project-level shortcut) and "same setup as this specific session" (from any session's own detail view). Preserving anything a user has already typed into a new-session composer while the launch-config fields underneath it get swapped is purely a client-side view-model concern, not something the host needs to know about.

## Acceptance criteria

- Given unread activity in a session, opening that session from a different client than the one that generated the activity clears the unread state on both clients without a manual refresh on either.
- Manually marking an open, currently-viewed session as unread keeps it unread — it does not immediately flip back to read while still being viewed.
- Read/unread indicators are not shown at all for sessions with no transcript activity, and for sessions the current user can only view, not participate in.
- Starting a new session via "same setup as this session" reuses that session's machine, working directory, agent/engine, and permission configuration, but starts an entirely new session with no transcript history and no shared identity with the original.
- Any text already typed in the new-session composer before invoking a shortcut is preserved after the shortcut applies its configuration.
- Read state remains correct (i.e., unread reappears) if new activity arrives in a session after it was marked read.

## Open questions

- None significant — this is one of the lowest-risk docs in the backlog and could be picked up opportunistically alongside other session-model work rather than needing its own dedicated slot.
