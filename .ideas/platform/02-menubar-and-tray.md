# Menubar / tray presence

| | |
|---|---|
| **Category** | Platform |
| **Plugin surface** | None — client UI feature |
| **Priority** | P3 |
| **Rough effort** | S–M |

## Background

"Menubar/tray presence" is really shorthand for two different needs that are worth separating rather than designing as one feature:

1. **An operator's quick-glance status tool.** Anyone running the Agnes host themselves (which, for a self-hosted product, is most users) periodically wants to know "is my host up, is it healthy, can I restart it" without opening a full client and navigating to a status page. A system tray/menu-bar icon is a natural fit for this because it's always visible and answers a yes/no question (idle/working/needs-attention) at a glance, with quick actions one click away.
2. **An always-visible presence for regular end users.** Separately, someone actively using Agnes throughout the day might want a persistent, lightweight way to see "is anything waiting on me right now" without keeping a full window open — the tray/menu-bar area is the natural OS-level place for this too, but the audience and the content shown are different: not host health, but session attention state.

These are worth keeping conceptually separate because they serve different audiences (the person running the daemon vs. the person using it day to day — often the same person, but wearing different hats) and have different content needs. Building them as one conflated feature risks a UI that's cluttered for the common case (most users just want "is anything waiting on me") with operator-only detail (PID, CPU, restart controls) that only matters to a small subset of self-hosters.

## Current state in Agnes

`Agnes.App.Desktop` (Avalonia) has no tray or menu-bar presence today — no icon, no background status indicator, nothing visible when the window is closed or minimized. There is also no dedicated self-host operator tool of any kind yet (no separate daemon-management utility beyond whatever `docker compose`/systemd commands are documented in `docs/deployment.md`).

## Proposed design

Given the two needs above have different audiences and don't need to ship together, treat this as two small, independently prioritizable pieces rather than one feature:

- **A minimal system-tray icon for `Agnes.App.Desktop`**, addressing need #2 above (end-user attention presence), since it's the more broadly useful of the two and Agnes already has the window/session state needed to drive it. Avalonia supports tray icons cross-platform (macOS menu bar, Windows system tray, Linux where the desktop environment supports it), which is a meaningfully smaller and more portable starting point than building something macOS-only first. The icon shows an aggregate status (counts of sessions idle / working / needing attention) and a right-click menu for jumping straight to a session or host without restoring the full window — essentially a zero-friction way to answer "does anything need me" without the window taking focus.
- **A self-host operator status tool** (need #1) is a distinct, lower-priority idea worth deferring until Agnes actually has enough self-host operational surface (daemon lifecycle, autostart, remote-access status) to justify a dedicated always-on status tool for it. Building this prematurely, before there's a settled notion of what "host health" means operationally for Agnes, risks designing the wrong dashboard.
- Any richer, more visually expressive always-on-top companion presence (an animated or personality-driven overlay, as opposed to a plain status icon) is out of scope for this doc — see `../delight/01-pets-companion.md` if that's ever prioritized separately. This doc's tray icon is useful on its own regardless of that decision and shouldn't be blocked on it.

## Acceptance criteria

- Given `Agnes.App.Desktop` is running with at least one paired host and active sessions, then a tray/menu-bar icon is visible showing an aggregate count broken out by idle / working / needs-attention.
- Given the main window is closed or minimized, when a session transitions into "needs attention" (e.g. a permission request fires), then the tray icon's aggregate state updates without requiring the window to be open.
- Given the user right-clicks (or the platform-equivalent gesture for) the tray icon, then a menu appears listing sessions currently needing attention, and selecting one brings the app to focus on that session.
- The tray icon behaves correctly on at least macOS and Windows, and degrades gracefully (no crash, feature simply absent) on a Linux desktop environment that doesn't support tray icons, rather than failing to start.
- Non-regression: closing/minimizing the app to the tray does not terminate active session connections or interrupt in-flight agent turns — the app keeps running in the background exactly as it does today when minimized, the tray icon is purely additive UI.

## Open questions

- Cross-platform tray behavior varies meaningfully (macOS menu bar conventions vs. Windows system tray vs. per-desktop-environment Linux support) — worth a small spike on Avalonia's tray API maturity across all three before committing to full scope.
- Whether "closing the window" should minimize-to-tray by default or require an explicit setting is a UX decision (some users expect closing the window to quit the app entirely) worth deciding deliberately rather than defaulting silently.
- Low enough priority overall that it's reasonable to defer entirely until higher-value items in this backlog land.
