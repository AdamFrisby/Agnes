# Embedded terminal, reused everywhere

| | |
|---|---|
| **Category** | Platform |
| **Plugin surface** | Core feature — extends Agnes's existing `ICliFallback`/`ITerminalHandle`, no new plugin interface |
| **Priority** | P2 |
| **Rough effort** | M |

## Background

Not everything a coding agent (or Agnes itself) needs to do fits the structured, event-based session model — some things are just a real shell command that needs a real terminal: a provider CLI's interactive login flow, a one-off debugging command, anything that expects a raw TTY. Agnes already accounts for this with a PTY fallback path so these cases aren't dead ends.

The risk with a capability like "open a real terminal" is that it's tempting to reinvent per feature: the session view builds its own shell-out mechanism, then a provider-login flow builds a different one, then a future feature builds a third. Each reinvention duplicates the genuinely fiddly parts of terminal handling (PTY lifecycle, resize, output buffering, URL detection in output, cross-platform differences) and multiplies the number of places that can get any one of those details wrong. The fix isn't new infrastructure — Agnes already has the right primitive — it's **discipline**: every feature that needs a real terminal routes through the same one, rather than each shelling out independently. That discipline is the actual subject of this doc; the underlying transport already exists (see below).

## Current state in Agnes

Agnes already has the core primitive. `Agnes.Abstractions/CliFallback.cs` defines:

```csharp
public interface ICliFallback
{
    Task<ITerminalHandle> OpenTerminalAsync(TerminalOptions options, CancellationToken cancellationToken = default);
}
```

`ITerminalHandle` gives write/resize over a live PTY, and terminal output is carried into the session's event log as its own `SessionEvent` kind (`TerminalOutputEvent`, per `docs/architecture.md`), interleaved in order with everything else that happened in that session. This is a real, working fallback — not a proposal.

What's missing is reuse discipline and client-side polish, not a new interface:

- Provider CLI login (`../providers/06-provider-authentication-detection.md`'s "Log in" action, for a provider whose CLI needs an interactive login the way many coding-agent CLIs do) is a second, independent place that will need to shell out to a real terminal. Left unchecked, it's easy for that to become its own bespoke process-spawn path instead of going through `ICliFallback`.
- There's no client-side terminal *panel* yet — no dockable location, no state persistence across UI changes, no mobile-specific show/hide behavior. `ICliFallback` is a host-side transport; the UI layer that presents it consistently across features doesn't exist yet.

## Proposed design

This is a discipline-and-polish doc, not a new-primitive doc:

- **Route every terminal need through `ICliFallback.OpenTerminalAsync`.** Specifically, `../providers/06-provider-authentication-detection.md`'s "Log in" action should open its provider CLI's login command through the same fallback path the in-session terminal already uses, instead of a bespoke shell-out. The payoff of doing this is concrete: any convenience built once into the shared terminal pane (URL detection and click-to-open in the login flow's output, consistent resize behavior, consistent error handling when the underlying process dies) is available to every feature that uses it, instead of needing to be re-implemented — or, more likely, silently missing — in whichever feature didn't go through the shared path.
- **A dedicated client-side terminal panel.** `Agnes.Ui.Core` gains a `TerminalPanelViewModel` with a `DockLocation` (Bottom/Sidebar/Details), so a user can place the terminal where it fits their workflow rather than it being a fixed, inflexible pane. Panel state (scrollback position, dock location) is persisted **per session id**, not per window — the natural key given Agnes's session-centric model, and it matches the expectation that switching back to a session you were in earlier should restore what you left, while a session you've never opened a terminal in starts clean.
- **Mobile show/hide, not mobile break.** On phone form factors, a full interactive terminal is a poor fit for the screen size much of the time, but hiding it unconditionally would remove a capability some users genuinely need on the go (checking on a login flow, say). The right behavior is to show a "Terminal" entry in the mobile navigation when the target host is reachable and reachable-and-supports-fallback, and simply omit it (not show a broken/erroring entry) when it isn't — reusing whatever reachability signal Agnes's session/connection management already tracks for its own presence indicators, rather than standing up a second reachability check that can drift out of sync with the first.

## Acceptance criteria

- Given a session with an active `ICliFallback`-backed terminal, when the user changes its `DockLocation` (e.g. Bottom to Sidebar), then the live terminal session (scrollback and running process) persists across the move — no reconnect, no lost output.
- Given a user reopens a session they previously had a terminal open in, then the terminal panel restores its prior dock location and scrollback for that session id specifically.
- Given a user opens a *different* session that has never had a terminal opened in it, then that session's terminal panel starts fresh (no leaked state from another session).
- Given the provider-login "Log in" action from `../providers/06-provider-authentication-detection.md` is invoked, when the login flow starts, then it runs inside the same shared terminal pane/transport as the in-session terminal, not a separate shell-out mechanism — verified by both features sharing the same `ICliFallback` call path in code, not just similar-looking UI.
- Given a mobile client connected to a host it currently cannot reach for terminal fallback, then the "Terminal" entry is hidden from navigation rather than present-but-erroring.
- Given a mobile client connected to a reachable host, then the "Terminal" entry appears and opens a working terminal session.
- Non-regression: the existing in-session PTY fallback behavior (terminal output appearing as interleaved `TerminalOutputEvent`s in the session log) is unchanged by adding the panel/dock/reuse layer on top of it.

## Open questions

- `docs/architecture.md`'s own "Open risks" section already flags **terminal-fallback rendering on WASM** (a VT/terminal emulator running inside Uno vs. a JS-interop-based embed) as unresolved. This doc's mobile/web reuse ambitions are downstream of that spike resolving — sequencing this doc's WASM-facing work after that risk is closed, rather than in parallel, avoids building panel polish on top of a rendering approach that might still change.
- Whether the provider-login reuse (routing "Log in" through `ICliFallback`) should ship before or alongside the panel UI work is an open sequencing question — the login-flow reuse has value even without the dockable-panel polish, so it may be worth splitting into its own smaller first milestone.
