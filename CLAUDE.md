# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Agnes: a remote interface to coding CLIs. One **host** daemon runs coding agents (Claude Code, OpenCode, Codex) in their **ACP** (Agent Client Protocol) mode; **many clients** (Avalonia desktop, Uno web/WASM, Android) connect to it, similar to `claude` in `tmux`+`ssh` but without a fixed character grid — sessions are event-sourced and reflow natively per client. Status: alpha (see `README.md`, `docs/architecture.md`).

## Build & test

Requires the **.NET 10 SDK** (pinned in `global.json`). The backend (core, host, client, UI view models) and all tests build without extra workloads via the `Agnes.Core.slnf` solution filter — this is what CI builds:

```bash
dotnet build Agnes.Core.slnf                          # backend + tests
dotnet test  Agnes.Core.slnf                           # all tests
dotnet test tests/Agnes.Host.Tests                     # one test project
dotnet test tests/Agnes.Host.Tests --filter FullyQualifiedName~AuthRateLimitTests  # one class/test
```

Tests use **xunit**. `tests/Agnes.TestKit` holds shared fakes (e.g. `FakeAcpAgent`); `tests/Agnes.Integration.Tests` and the `recordings/*.json` fixtures (`RecordedHost`) drive end-to-end scenarios offline without a real CLI or VM.

The Uno UI app (`src/Agnes.App`) is a separate subtree with its own solution, not in `Agnes.Core.slnf`. Its web head needs the `wasm-tools` workload, the Android head needs `android`:

```bash
dotnet build src/Agnes.App/Agnes.App/Agnes.App.csproj -f net10.0-desktop       # Linux/macOS/Windows (Skia)
dotnet build src/Agnes.App/Agnes.App/Agnes.App.csproj -f net10.0-browserwasm   # web (needs wasm-tools)
```

Run the host directly for manual testing: `dotnet run --project src/Agnes.Host` (logs a pairing code; configure agent launch commands in `appsettings.json`). Screenshots of the UI are generated offline against a simulated host via `dotnet run --project tools/Agnes.Screenshots`. `tools/Agnes.Record` records a live/sandboxed agent session to a `recordings/*.json` fixture.

Package distributable native builds with `./build.sh` / `./build.ps1` (outputs to git-ignored `builds/`); see the script headers for target flags (`linux windows mac android web`, `--client-only`).

## Architecture

```
Host daemon ── spawns each CLI (ACP mode, or a native stream-json adapter)
            ── normalizes updates -> event-sourced log (SQLite) + session catalogue
            ── ASP.NET Core + SignalR hub (TLS + per-device pairing tokens)
                     │  Agnes wire protocol
   Clients ── Agnes.Client connection pool (many hosts, dozens of agents)
            ── Avalonia desktop app · Uno web (WASM) + Android heads
```

Full design rationale: `docs/architecture.md`. Deployment/auth/config reference: `docs/deployment.md`. Incus sandbox live-testing notes and known gotchas: `docs/sandbox-live-testing.md`.

### The core idea: everything is a `SessionEvent`

Every `session/update` from an agent's ACP stream is normalized into a `SessionEvent` and **appended** to a per-session log with a monotonic sequence number. This one decision is why scrollback is unlimited, multiple clients stay consistent (a joining client requests `since = cursor`, gets a snapshot to `head`, then the live tail), and reconnects resume with no lost/duplicated events. Raw PTY fallback output is carried as its own `SessionEvent` kind, interleaved in the same order. Understand this model before touching `Agnes.Host`, `Agnes.Protocol`, or `Agnes.Ui.Core` — most cross-cutting behavior traces back to it.

### Project map (`src/`)

| Project | Role |
| --- | --- |
| `Agnes.Abstractions` | Plugin & domain contracts: `IAgentAdapter`, `IAgentSession`, `SessionEvent`, `ICliFallback`. No external deps. |
| `Agnes.Acp` | Generic **ACP-over-stdio client** on StreamJsonRpc — child process lifecycle, JSON-RPC framing, capability negotiation, ACP↔`SessionEvent` mapping. Reused by every agent plugin. |
| `Agnes.Agents.ClaudeCode` / `Agnes.Agents.OpenCode` | Thin plugins over `Agnes.Acp`: launch command/args/env, auth handling, capability quirks. |
| `Agnes.Agents.Native` | Native stream-json adapter (e.g. `claude --print --input-format stream-json`) for agents driven outside ACP proper. |
| `Agnes.Agents.Codex` | Codex adapter (native app-server, persistent JSON-RPC over stdio). |
| `Agnes.Protocol` | Transport-agnostic host↔client wire contract (DTOs + hub interface: subscribe, send prompt, permission response, terminal I/O, snapshot/tail cursors). Default binding is SignalR but the contract doesn't assume it. |
| `Agnes.Host` | ASP.NET Core daemon: plugin loader, `SessionManager`, event-sourced SQLite store, `PtyManager` fallback, SignalR hub, device-pairing/GitHub/keypair auth, scheduled tasks. |
| `Agnes.Client` | Frontend-agnostic client library: connection pool across multiple hosts, snapshot+tail replay, auto-reconnect, device-token store. |
| `Agnes.Client.Simulation` | In-memory simulated host/agent for offline UI development and screenshots. |
| `Agnes.Recording` | Support for recording real/sandboxed sessions to replayable JSON fixtures (used by `tools/Agnes.Record` and `RecordedHost` test fixtures). |
| `Agnes.Sandbox` / `Agnes.Sandbox.Incus` | Optional per-session VM sandboxing: credential broker, Incus provider. See `docs/sandbox-live-testing.md`. |
| `Agnes.Ui.Core` | Framework-agnostic view models + ACP-event render logic, shared by every UI head. |
| `Agnes.App.Desktop` | Avalonia desktop client — primary, full-featured. |
| `Agnes.App` | Uno Platform multi-head app: web (WASM), Android, and a desktop head, composed from `Agnes.Ui.Core`. Desktop vs. mobile are **two distinct shells in separate namespaces** chosen responsively by form factor, not one UI stretched to fit. |

New agent CLIs are added as new `Agnes.Agents.*` packages implementing `IAgentAdapter`, not by changing core code.

### ACP surface implemented (protocol v1)

Client → Agent: `initialize`, `authenticate`, `session/new`, `session/prompt`, `session/load`, `session/set_mode`. Agent → Client: `session/request_permission`, `fs/read_text_file`, `fs/write_text_file`, `terminal/*`. Notifications: `session/update` (streamed), `session/cancel`. Conventions: JSON keys camelCase, discriminators snake_case, all paths absolute, line numbers 1-based.

### Security model

TLS listener on the host; new clients pair via a short code/QR (or GitHub device-flow SSO, or an `authorized_keys`-style P-256 keypair) and receive a per-device bearer token, individually revocable and stored hashed. SignalR connections authenticate with that token; per-session authorization gates group membership. Agents ask for permission per tool call by default (`--permission-prompt-tool stdio` control protocol) — `--dangerously-skip-permissions` / autonomous mode is opt-in per session, never default.

### Dependency policy

Reputable, first-party dependencies preferred — supply-chain risk is treated as real. Notably: **no** third-party ACP NuGet package (`dotacp.*`, `AgentClientProtocol4CSharp` are deliberately avoided — low downloads, single obscure owner); the ACP subset Agnes needs is hand-modeled on Microsoft's StreamJsonRpc instead. Keep this discipline when adding new plugin surfaces.

## Design directives

How to add behaviour to Agnes. These are defaults, not absolutes — deviate when a case genuinely warrants it, and say why in the code/PR.

- **Prefer events over direct calls.** Non-trivial actions flow through the event spine (`IEventBus` in `Agnes.Abstractions.Events`), not hard-wired method calls, so plugins can observe, intercept, mutate, or veto them. The convention: `Before*Event` is a `CancelableEvent` dispatched *before* an action commits (an interceptor may rewrite its settable payload or `Cancel()` it — each action defines what a veto does); `*edEvent` is an observe-only fact dispatched *after*. Inbound agent facts ride the spine too — `SessionEvent : IAgnesEvent`, so a plugin can observe `ToolCallEvent`, `TurnEndedEvent`, etc. Add a new action as **one event record + one dispatch at that action's own call site** — never a central router or `switch`. See `.ideas/00d-event-spine-and-ui-extensibility.md` for the full taxonomy and the implemented surface.
- **Prefer modularity and plugin-ness.** New capabilities are plugins, not edits to core. A new agent CLI is a new `Agnes.Agents.*` implementing `IAgentAdapter`; a new auth method, automation trigger, git host, MCP preset, transport, event store, or client UI extension is a new implementation registered through an `IPluginRegistry<T>` + `IPluginPointMerger`, merged from built-ins and NuGet-installed plugins alike. Do not special-case core to know about a concrete feature. **No god objects / monolithic hubs**: the `EventBus` knows about zero concrete event types, events are split one-file-per-domain, and registries are generic — keep that discipline.
- **Prefer pure, functional interfaces.** Model contracts as functions over their inputs that return values, rather than side effects on shared, mutable state. Take dependencies as constructor/parameter inputs (DI); don't reach for ambient singletons or module-global statics, and don't mutate shared state as a covert channel. Where a component must hold state, keep it local and explicit. On the spine specifically: an interceptor mutates only its own event payload, and an observer must never change the action's outcome (its exceptions are isolated). Favour immutable records for contracts and events.
- **Prefer strong static typing; keep loose JSON at the boundary.** Model data as typed records and enums the compiler can check. Untyped, dynamic JSON (`JsonElement`/`JsonDocument` traversal, the `dynamic` keyword, `Dictionary<string, object>` bags, `Deserialize<object>`) is acceptable **only** at a genuine external edge — parsing a CLI, API, or file whose schema we don't own — and even there you deserialize into typed records *immediately* rather than letting `JsonElement`/`object` flow inward. Our own wire contract, domain model, and internal data are always fully typed. A `JsonElement`/`object`/string-bag field anywhere non-boundary (especially in `Agnes.Abstractions` or `Agnes.Protocol`) is a red flag. When a boundary schema is genuinely polymorphic (a field that's a string in one message and an array in the next), keep just that sub-field as `JsonElement` and say why — don't untype the whole payload.

## Other notes

- `.ideas/` is git-ignored planning scratch (feature backlog specs + a phased dependency-ordered build plan) — not shipped docs. A spec gets promoted into `docs/` and deleted from `.ideas/` once actually implemented; don't treat its contents as current behavior.
- CI (`.github/workflows/ci.yml`) runs on PRs to `main`, daily (only if there were commits), and on demand — not on every push to main. It has two jobs: `build-test` (restores/builds/tests `Agnes.Core.slnf`, with a single automatic retry on test failure to absorb a known cold-start JIT flake in the desktop simulation tests) and `ui-build` (builds the Uno heads with `wasm-tools`+`android` workloads installed).
- All projects: nullable enabled, warnings as errors, `LangVersion=latest` (`Directory.Build.props`) — expect a strict build. Philips.CodeAnalysis analyzers run too; the curated rule set (and why each is on/off) lives in `.editorconfig`.
- The Uno multi-head app (`Agnes.App`) is transitional: the web/mobile heads are slated to consolidate onto **Avalonia** eventually. Don't over-invest in Uno-specific shells, and don't treat Desktop↔Uno divergence as urgent — put genuinely shared logic in `Agnes.Ui.Core` and let the Desktop (Avalonia) head lead.
