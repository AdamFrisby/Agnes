# Local CLI wrapper with instant local/remote handoff

| | |
|---|---|
| **Category** | Sessions |
| **Plugin surface** | Core client feature — a new lightweight CLI entry point built on `ICliFallback`/`Agnes.Client`; extends the "Direct session" concept |
| **Priority** | P1 |
| **Rough effort** | L |
| **Depends on** | `../sessions/02-direct-vs-synced-sessions.md` (this is a purpose-built way to create a Direct session, rather than discovering one after the fact) |

## Background

Every Agnes session today starts because some client told the host "start a session" — the host is always the one spawning the agent CLI. That's a fine model when the user's starting point is already a remote client (their phone, a browser). It's a worse fit for the most common real starting point: a developer sitting at their own machine, in their own terminal, who just wants to run a coding agent *right now*, the same way they'd normally type `claude` or `codex` — and only cares about remote access later, opportunistically, if they need to step away mid-task.

Forcing that developer to open a separate Agnes client and drive session creation through a GUI, just to start something they'd otherwise type directly into their terminal, is real friction for the single most common way a session begins. A lightweight wrapper — `agnes claude` instead of `claude` — that behaves exactly like the underlying CLI by default, but transparently registers the resulting session with the local host daemon (if one is running) so it becomes visible and attachable from any paired remote client, removes that friction entirely: the local terminal stays the primary, zero-overhead way to start working, and remote access becomes something that's simply already available if and when it's needed, with no separate step required at session-start time.

The other half of this feature is what happens when a remote client *does* want to take over, or when the user at the terminal wants to hand off to their phone before stepping away: switching which surface is actively driving the session needs to be immediate and unambiguous, without the risk of a stray keypress accidentally triggering a handoff mid-task.

## Current state in Agnes

`ICliFallback`/`ITerminalHandle` (`Agnes.Abstractions/CliFallback.cs`) already provides a real PTY, and `../sessions/02-direct-vs-synced-sessions.md` proposes a way to discover and attach to a session an agent CLI created entirely outside Agnes, after the fact, by reading that CLI's own log files. There is no wrapper entry point that starts a session *as* an Agnes-registered session from the moment it launches, and no local/remote control-handoff mechanic at all — every session today is driven by exactly one thing at a time (whichever client sent the last prompt), with no explicit "who's currently in control" concept or user-facing way to switch it deliberately.

## Proposed design

**A thin wrapper executable** (`agnes <agent> [args...]`, e.g. `agnes claude`) that, on startup:

1. Execs the underlying agent CLI as a normal foreground child process, with the wrapper's own terminal as that process's controlling terminal by default — so with no host daemon running at all, `agnes claude` behaves exactly like running `claude` directly, with zero behavior change and zero added latency. This matters: the wrapper must never be a worse experience than the bare CLI when Agnes isn't in the picture at all.
2. If a local host daemon *is* running on this machine, registers the new session with it over its local loopback control endpoint at spawn time — not discovered later by scanning logs, but known to the host from the first event onward, the same way a host-initiated session is. This makes it a full Direct session per `../sessions/02-direct-vs-synced-sessions.md` from birth, immediately attachable and steerable from any paired remote client, with no separate "take over" step needed unless the user wants to promote it to a fully Synced session.
3. Interleaves the terminal's raw output into the same `TerminalOutputEvent` stream `ICliFallback` already produces for the PTY-fallback case — this wrapper's output *is* exactly a PTY-fallback session as far as the event log and remote clients are concerned; nothing new needs inventing on that side.

**Local/remote handoff**, once a remote client has attached to the session:

- Pressing a single dedicated key (e.g. space) arms a handoff confirmation, shown in the terminal ("press again, or Ctrl+<key>, to hand off to remote control — any other key cancels"); pressing it again within a short window confirms the switch, and any other keypress cancels the confirmation and is passed through to the running process as normal input. A second, separate key combination performs an instant handoff with no confirmation step, for users who want speed over the safety margin.
- The two-step design is a deliberate choice over a literal "any key switches" design: with only one confirmation key, an accidental double-press (e.g. residual key-repeat, or a key mashed while thinking) can't silently hand off control the user didn't intend, while the deliberate instant-handoff shortcut still exists for the case where the user genuinely wants speed and knows what they're doing.
- While remote-controlled, keystrokes typed into the local terminal are not silently forwarded to the running process (which would be actively dangerous — a user assuming they're just watching output could accidentally inject input into a session someone else is now driving) — they queue locally with a clear "remote is in control" indicator, and are either discarded or offered for replay once local control resumes, but never sent while remote control is active.

## Acceptance criteria

- Given no host daemon is running, `agnes claude` behaves identically to running `claude` directly — verified by diffing output and exit codes between the two for an identical scripted interaction.
- Given a host daemon running locally, starting a session via `agnes claude` makes that session immediately visible to a remote client paired to that host, with no separate registration or "take over" step required.
- Given a session started via the wrapper, its terminal output appears in the session's event log as `TerminalOutputEvent`s, interleaved correctly with any remote-originated prompts sent to the same session.
- Given a remote client is actively controlling a wrapper-originated session, pressing the confirmation key once and then any key other than the confirmation key or instant-handoff key cancels the handoff and the pressed key is passed through to the running process as ordinary input.
- Given a remote client is in control, keystrokes at the local terminal are never forwarded to the running process — verified by confirming a keypress at the local terminal during remote control produces no effect on the agent's input, only a local "remote is in control" indication.
- The instant-handoff shortcut immediately switches control with no confirmation step, and is distinguishable in behavior from the two-step confirm sequence in an automated test driving both paths.
- Non-regression: the existing `ICliFallback`/PTY-fallback path for host-initiated sessions is unmodified by this feature — the wrapper is a new way to create and interact with a Direct session, not a replacement for the existing fallback mechanism.

## Open questions

- Should the wrapper support attaching to an *existing* session by id (`agnes attach <session-id>`) as well as starting a new one, unifying with whatever attach mechanic `../platform/03-embedded-terminal-everywhere.md` and `../sessions/02-direct-vs-synced-sessions.md` end up with? Likely yes, worth designing as one coherent attach path rather than three separate ones.
- Windows terminal handling (ConPTY vs. the Unix PTY path) needs its own verification pass — the confirm/cancel keyboard handling in particular may need platform-specific input handling given differences in how raw terminal input is delivered.
