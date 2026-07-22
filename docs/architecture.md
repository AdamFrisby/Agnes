# Agnes — Architecture

## Goal

A remote interface to coding CLIs. One **host** runs the agents; **many clients** connect to it — like `claude` in `tmux` + `ssh`, but without tmux's limits (fixed grids, poor scrollback, terminal-size coupling). One client can connect to **dozens of agents across multiple hosts**.

## The key idea

Run each coding CLI in its **ACP** (Agent Client Protocol) mode. ACP is JSON-RPC 2.0 over stdio; its `session/update` stream is a sequence of **structured, reflowable events** — assistant message chunks, tool calls, diffs, plans, permission requests — not a fixed character grid.

Because events are structured, the host can:

- persist **unlimited scrollback** server-side,
- **rebroadcast** the same session to N clients,
- let each client render **natively at its own size / form factor**,

and fall back to a **real PTY** for anything ACP cannot express.

The ACP **client role lives on the host**, co-located with the CLI over stdio (as ACP intends). Frontends never touch stdio; they speak only the Agnes wire protocol, so they can be remote, browser-based, or mobile.

## Components

```
┌────────────── HOST (daemon, one per machine with CLIs) ────────────┐
│  Plugin loader → Agent adapters (Claude Code, Codex, …)            │
│     each adapter = generic ACP-stdio client (Agnes.Acp)           │
│     spawns the CLI in ACP mode; host is the ACP *client*          │
│  PtyManager — true CLI fallback per agent (real terminal)         │
│  SessionManager — event-sourced session log (SQLite), scrollback  │
│  ASP.NET Core + SignalR hub — auth, pairing, per-session groups    │
└───────────────────────────▲───────────────────────────────────────┘
        Agnes wire protocol  │  (SignalR default binding; TLS + token)
   ┌─────────────────────────┴───────────────────────────┐
   │ Agnes.Client — connection pool across many hosts     │
   │ Agnes.Ui.Core — view models + ACP-event renderers    │
   │   Desktop shell (multi-pane)  │  Mobile shell (phone) │
   │   Uno heads: Windows / macOS / Linux / Android / WASM │
   └──────────────────────────────────────────────────────┘
```

### `Agnes.Abstractions`
Domain + plugin contracts, no external dependencies:
- `IAgentAdapter` — describes and launches an agent kind; produces `IAgentSession`s.
- `IAgentSession` — a live conversation: send a prompt, cancel, respond to permission requests; emits `SessionEvent`s.
- `AgentDescriptor` / `AgentCapabilities` — identity + negotiated capabilities.
- `SessionEvent` — the normalized event model the whole system speaks (see below).
- `ICliFallback` — raw PTY fallback contract.
- `IPluginRegistry<TProvider>` / `PluginRegistry<TProvider>` — the general-purpose plugin-point pattern (below).

### Plugin architecture

Every plugin point in Agnes (agent adapters, sandbox providers, and any future provider kind — transports, auth methods, voice, …) follows one shape: a small interface implemented by each provider, plus an `IPluginRegistry<TProvider>` the host builds from every DI-registered implementation of that interface:

```csharp
public interface IPluginRegistry<TProvider> where TProvider : notnull
{
    IReadOnlyList<TProvider> All { get; }
    TProvider? Find(string id);
}
```

`PluginRegistry<TProvider>` is the default implementation — built from `IEnumerable<TProvider>` plus a per-call id-selector function, since different plugin-point interfaces name their id differently (`IAgentAdapter.Descriptor.Id`, `ISandboxProvider.Name`, …). `Agnes.Host`'s composition root (`Program.cs`) registers one `IPluginRegistry<T>` singleton per plugin point from `sp.GetServices<T>()`; consumers (`SessionManager`, `AgnesHub`) depend on the registry interface, never on a hand-rolled dictionary or a hardcoded list. Adding a new provider for an existing plugin point is exactly one `AddSingleton<T>()` registration — no changes to the registry, `SessionManager`, or the hub.

Host-level **capability negotiation** builds on the same registries: `GetCapabilities()` (on `IAgnesServer`/`AgnesHub`, proxied through `Agnes.Client`'s `IAgnesHost.GetCapabilitiesAsync()`) reports which plugin-point ids are actually populated on this host, each tagged `FailClosed` (the request should hard-fail without it) or not (the caller should degrade gracefully — e.g. no sandbox provider just means sessions run on the host instead of in a VM). A client can check this up front instead of discovering an absent capability via a failed call.

NuGet-distributed third-party plugins (packaging, signature verification, install/enable/disable lifecycle, and a management UI) are specified in `.ideas/00-plugin-architecture.md` but not yet built — today every plugin is a built-in shipped in-process with `Agnes.Host`.

### `Agnes.Acp`
The workhorse: a generic **ACP-over-stdio client** built on **StreamJsonRpc** (Microsoft). It owns the child process lifecycle, JSON-RPC framing, capability negotiation, and the mapping between ACP messages and Agnes `SessionEvent`s. We hand-model the ACP message subset we use (rather than depend on low-reputation third-party ACP packages) — see [Dependencies](#dependencies).

ACP surface we implement (protocol v1):
- Client → Agent: `initialize`, `authenticate`, `session/new`, `session/prompt`, `session/load`, `session/set_mode`.
- Agent → Client: `session/request_permission`, `fs/read_text_file`, `fs/write_text_file`, `terminal/*`.
- Notifications: `session/update` (streamed), `session/cancel`.

Conventions: JSON keys camelCase; discriminators snake_case; all paths absolute; line numbers 1-based.

### `Agnes.Agents.*`
One thin plugin per CLI (`ClaudeCode` first). Mostly configuration atop `Agnes.Acp`: launch command / args / env, authentication handling, capability quirks, and a CLI-fallback command map. New agents are new packages, not core changes.

### `Agnes.Protocol`
The **transport-agnostic wire contract** between host and clients: DTOs and a hub interface — subscribe/unsubscribe, send prompt, permission response, terminal I/O, and snapshot/tail cursors. The default binding is SignalR, but the contract does not assume it.

### `Agnes.Host`
ASP.NET Core daemon:
- discovers/loads agent plugins,
- `SessionManager` orchestrates agent sessions,
- **event-sourced session store** (SQLite): every `SessionEvent` appended with a monotonic sequence number,
- `PtyManager` (real terminal) for fallback,
- **SignalR hub** implementing `Agnes.Protocol`, with per-session broadcast groups,
- auth: **TLS + device-pairing tokens** (short code / QR → per-device revocable bearer token).

### `Agnes.Client`
Reusable, frontend-agnostic client library: a **connection pool across multiple hosts**, session subscription, snapshot+tail replay, automatic reconnection, and a device-token store.

### `Agnes.Ui.Core` + `Agnes.App`
Uno Platform UI. `Agnes.Ui.Core` holds shared view models and reusable render components for each `SessionEvent` kind (message stream, tool-call card, diff viewer, plan view, permission prompt, and a terminal-view control for fallback). `Agnes.App` composes **two genuinely distinct shells** from that core:
- **Desktop shell** — sidebar of hosts→agents, multi-pane, keyboard-driven (Windows / macOS / Linux-KDE / large-screen WASM).
- **Mobile shell** — single-column, navigation-stack, notification-oriented (Android).

The shell is chosen responsively by form factor; the two shells live in separate namespaces so neither is one UI stretched onto the wrong device.

## The event-sourced session model

Every `session/update` from an agent is normalized to a `SessionEvent` and **appended** to that session's log with a monotonically increasing sequence number. This one decision delivers most of the product goals:

- **Scrollback** — the log *is* the history; nothing is bound to a screen buffer.
- **Multi-client consistency** — a joining client requests `since = cursor`; the host replies with a snapshot up to `head` then streams the live tail. Every client converges on the same ordered log.
- **Reconnect** — a dropped client resumes from its last acknowledged sequence number with no lost or duplicated events.
- **Fallback** — raw PTY output is carried as its own `SessionEvent` kind, interleaved in order.

## Security model (v1)

- Host exposes a **TLS** listener.
- A new client **pairs** via a short code / QR and receives a **long-lived per-device bearer token**, revocable individually.
- SignalR connections authenticate with that token; per-session authorization gates group membership.

## Dependencies

Reputable, first-party where possible (supply-chain risk is treated as real):

- **StreamJsonRpc** (Microsoft) — JSON-RPC 2.0 for ACP over stdio.
- **Microsoft.AspNetCore.SignalR / .Client** (Microsoft) — transport.
- **Microsoft.Data.Sqlite** (Microsoft) — event store.
- **Uno.Sdk** (Uno Platform, official) — UI.
- **Porta.Pty** — PTY fallback; introduced in the fallback pass, not the skeleton.

Deliberately **not** used: the third-party `dotacp.*` / `AgentClientProtocol4CSharp` NuGet packages (low downloads, single obscure owner). We model the ACP subset we need ourselves on StreamJsonRpc instead.

## Roadmap

1. **Walking skeleton** *(current)* — host launches Claude Code; one frontend pairs, opens a session, sends a prompt, renders the streamed events and a permission round-trip; a second client proves snapshot+tail.
2. More agent plugins — Codex, Gemini, Cursor.
3. Terminal-fallback renderer hardening (incl. WASM strategy).
4. Full pairing/QR + multi-host workspace UX.
5. Mobile shell polish + push notifications.
6. Session persistence limits, packaging per platform.

## Open risks

- **Terminal-fallback rendering on WASM** — VT emulator in Uno vs. JS-interop embed; spike during the fallback pass.
- **Exact Claude Code ACP launch invocation** — verified at implementation time.
- **PTY dependency** (`Porta.Pty`) reputation — revisit at the fallback pass; vendor if needed.
