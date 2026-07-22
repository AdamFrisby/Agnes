# Scriptable agent-control CLI (headless, for scripts and CI)

| | |
|---|---|
| **Category** | Extensibility |
| **Plugin surface** | None — a new first-party client built entirely on the existing `Agnes.Client` library, plus one small host-side addition (a blocking "wait until idle" call) |
| **Priority** | P2 |
| **Rough effort** | M |
| **Depends on** | `../extensibility/03-automations.md` (related but distinct — see Background) |

## Background

Every interactive Agnes client (desktop, web, mobile) is built for a human watching a screen: pick an agent, type a prompt, watch it stream, tap approve/deny. There's a whole separate class of use case that wants none of that — a CI pipeline that needs to kick off an agent task and block until it's done, a shell script that wants to fire off a quick prompt and grab the result, a cron job (outside Agnes's own scheduling) that needs to check on a long-running session's status and exit non-zero if it failed. None of these want a UI; they want a small number of composable commands with clean exit codes and scriptable output (JSON), so they can be dropped into a `Makefile`, a GitHub Actions step, or a one-liner in a terminal.

This is a different need from `../extensibility/03-automations.md` (scheduled, recurring sessions triggered by Agnes's own clock) — automations are about Agnes deciding *when* to run something on a schedule it owns. This feature is about an *external* process (a script, a CI runner, a human at a shell prompt) deciding when to act, imperatively, right now, and needing a thin, scriptable interface to do it — closer to `kubectl` or `gh` than to a cron daemon.

## Current state in Agnes

`Agnes.Client` already exists as a "reusable, frontend-agnostic client library" (per `docs/architecture.md`) with a connection pool across hosts, session subscription, and snapshot+tail replay — this is exactly the library a scriptable CLI would be built on, and none of it needs to be invented. What's missing is a CLI surface over that library, and one small host capability it needs: a way to block until a session goes idle (finishes its current turn and has nothing queued) with a timeout, rather than a caller having to poll `IAgnesHub` manually and reimplement that loop themselves every time.

## Proposed design

A new, thin executable (`agnes-agent` or similar — kept as a separate binary from any future interactive `agnes` CLI, so its dependency footprint and command surface stay minimal and stable for scripting) built directly on `Agnes.Client`:

```
agnes-agent auth login                          # pairs this machine's script identity, same auth flow as any other device
agnes-agent machines [--json]                    # lists reachable hosts
agnes-agent spawn --host <id> --path <dir> --agent <agentId> [--create-dir]
agnes-agent send <session-id> "<prompt>" [--skip-permissions] [--wait]
agnes-agent status <session-id> [--json]
agnes-agent wait <session-id> [--timeout <seconds>]   # blocks until idle; exit 0 = idle, 1 = timeout, 2 = agent error
agnes-agent stop <session-id>
```

Design notes:

- **Every session/machine id argument supports unambiguous prefix matching** (as long as the prefix is unique) — scripts and humans both benefit from not having to paste a full GUID, and this is a small, cheap ergonomics win with no architectural cost.
- **`--json` on every read command**, so output is pipeline-friendly (`agnes-agent status <id> --json | jq .state`) without needing to scrape human-readable text.
- **`wait` is the one new host-side primitive needed.** Everything else (`spawn`, `send`, `status`, `stop`) is a direct, thin wrapper over calls `Agnes.Client` already exposes. `wait` needs the host to support blocking (with a timeout) until a session's state crosses into "idle" — implemented as a straightforward subscription to the session's existing event stream, resolving the wait when a turn-ended event arrives and nothing is queued, rather than a new polling mechanism; this can live entirely in `Agnes.Client` (subscribe and watch for the condition) without any new `IAgnesHub` method, unless polling efficiency at scale later warrants a dedicated server-side wait call.
- **Auth reuses an existing pairing mechanism, not a new one.** A script identity is just another paired device from the host's point of view — it gets a revocable bearer token the same way a mobile client does, so revoking a compromised CI credential is exactly the existing device-revocation flow (`DELETE /devices/{id}`), not a new concept to build and secure separately. This token must be stored **hashed only, with no recoverable plaintext copy retained anywhere server-side** — a credential meant to run unattended in CI is exactly the kind of thing that ends up copy-pasted into more places than a human-held one, which makes it more valuable to an attacker and more important that a database compromise can't recover it directly. Storing a hash alongside a separate plaintext copy "for convenience" defeats the purpose of hashing at all and should be treated as a defect if it ever shows up in review, not a minor style issue.
- **This is a distinct binary from `Agnes.Host`, from any future interactive terminal-wrapper CLI (see `../sessions/07-local-cli-wrapper-and-handoff.md`), and from the desktop/mobile/web clients** — it never runs an agent itself, never opens a terminal, and has no UI. Keeping it a separate, minimal binary means its dependency surface (and therefore its audit surface, given it's likely to run with real credentials in CI) stays as small as possible.

## Acceptance criteria

- Given a paired script identity, `agnes-agent spawn` against a valid host/path/agent starts a real session and prints its session id (or the same as JSON with `--json`).
- Given a running session, `agnes-agent send <id> "<prompt>" --wait` blocks until the resulting turn completes and prints the final assistant output; without `--wait`, it returns immediately after the prompt is accepted.
- `agnes-agent wait <id> --timeout 30` on a session that goes idle within 30 seconds exits 0; on a session still running after 30 seconds, it exits 1 without killing or otherwise affecting the session.
- `agnes-agent wait <id>` on a session that ends in an agent error (not just "still running") exits with a distinct code (2) from a timeout (1), so a CI script can tell "still working" apart from "actually failed."
- Every command accepts an unambiguous id prefix in place of a full session/machine id, and reports a clear "ambiguous prefix" error (listing the candidates) if the prefix matches more than one.
- `agnes-agent auth login` produces a device token that shows up in the host's existing paired-device list (`GET /devices`) and can be revoked from there like any other device, with the CLI failing clearly on its next call once revoked.
- Non-regression: none of this requires any change to how interactive clients (desktop/web/mobile) authenticate or operate — the scriptable CLI is purely additive on top of `Agnes.Client`.

## Open questions

- Should `spawn`/`send` support attaching files (mirroring `../git-and-files/03-attachments-and-file-browser.md`) for scripted workflows that need to hand the agent a generated artifact? Reasonable follow-up, not needed for a first version focused on the core spawn/send/wait/status loop.
- Distribution: ship as a small standalone self-contained binary (matching the packaging approach `build.sh`/`build.ps1` already use for the desktop client) so CI runners don't need a .NET SDK installed just to use it.
