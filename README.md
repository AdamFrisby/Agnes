# Agnes

**A remote interface to coding CLIs.** Run one **host** where your coding agents live (Claude Code, Codex, Gemini, Cursor, …); connect from **many clients** — web, desktop (macOS / Windows / Linux-KDE), and mobile (Android). Think `claude` in `tmux` + `ssh`, but without tmux's limits: no fixed character grid, unlimited server-side scrollback, and each client renders at its own size.

> Status: **early alpha** — building the walking skeleton. See [`docs/architecture.md`](docs/architecture.md).

## Why

Coding CLIs are great locally but awkward to reach remotely. The usual answer — `tmux` + `ssh` — couples every client to a single fixed terminal grid, mangles scrollback, and breaks when window sizes differ.

Agnes runs each CLI in its **[Agent Client Protocol](https://agentclientprotocol.com) (ACP)** mode, a JSON-RPC 2.0 stream of *structured* events (message chunks, tool calls, diffs, plans, permission requests) rather than a character grid. The host normalizes that stream into an **event-sourced session log**, so:

- **Unlimited scrollback**, stored on the host.
- **Many clients, one session** — each connects and gets a snapshot + live tail; reconnects replay from a cursor.
- **Native, reflowable rendering** at each client's own size and form factor.
- **True CLI fallback** — a real PTY covers anything ACP can't express.

## Architecture at a glance

```
Host daemon ── spawns each CLI in ACP mode (host is the ACP *client*)
            ── normalizes session/update -> event-sourced log (SQLite)
            ── ASP.NET Core + SignalR hub (TLS + device-pairing tokens)
                     │  Agnes wire protocol
   Clients ── Agnes.Client connection pool (many hosts, dozens of agents)
            ── Uno Platform UI: distinct Desktop and Mobile shells
```

Full design: [`docs/architecture.md`](docs/architecture.md).

## Repository layout

| Project | Role |
| --- | --- |
| `src/Agnes.Abstractions` | Plugin & domain contracts (`IAgentAdapter`, `SessionEvent`, …) |
| `src/Agnes.Acp` | Generic ACP-over-stdio client (on StreamJsonRpc) — reused by every agent |
| `src/Agnes.Agents.ClaudeCode` | Reference agent plugin (launch descriptor for Claude Code's ACP endpoint) |
| `src/Agnes.Protocol` | Transport-agnostic host↔client wire contract |
| `src/Agnes.Host` | ASP.NET Core daemon: plugins, session manager, event store, SignalR hub |
| `src/Agnes.Client` | Reusable client library: multi-host connection pool, snapshot+tail |
| `src/Agnes.Ui.Core` | Shared Uno view models + ACP-event render components |
| `src/Agnes.App` | Uno multi-head app (Desktop + Mobile shells) |
| `tests/*` | Unit tests + a fake ACP agent test double |

## Build

Requires the **.NET 10 SDK**. The backend (core, host, client, UI view models) and all tests build with no extra workloads:

```bash
dotnet build Agnes.Core.slnf     # backend + tests (what CI builds)
dotnet test  Agnes.Core.slnf
```

The Uno UI app is a separate subtree (`src/Agnes.App`) with its own solution and build config. Its WebAssembly head needs the `wasm-tools` workload; the Android head needs the `android` workload:

```bash
dotnet build src/Agnes.App/Agnes.App/Agnes.App.csproj -f net10.0-desktop      # Linux/macOS/Windows (Skia)
dotnet build src/Agnes.App/Agnes.App/Agnes.App.csproj -f net10.0-browserwasm  # web
```

## Run the walking skeleton

1. **Host** — from `src/Agnes.Host`, `dotnet run`. It loads the Claude Code plugin and logs an `Agnes pairing token`. (Claude Code's ACP bridge is launched on demand via `npx @zed-industries/claude-code-acp`; configure the command in `appsettings.json`.)
2. **Frontend** — run an `Agnes.App` head (desktop or web). Enter the host URL (`https://localhost:5081`), paste the pairing token, pick an agent, and start a session. The desktop shell is a multi-pane layout; below ~720px it switches to the single-column mobile shell.

The transcript renders reflowable ACP events (messages, tool calls, plans, permission prompts); open a second client to see the same session replay via snapshot + live tail.

## Supported agents

Claude Code first; Codex, Gemini, and Cursor to follow as thin plugins over the shared ACP client.

## License

[MIT](LICENSE) © 2026 Adam Frisby
