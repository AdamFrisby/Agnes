# Provider breadth: a generic ACP backend catalog

| | |
|---|---|
| **Category** | Providers |
| **Plugin surface** | Extends the existing `IAgentAdapter` (see `../00-plugin-architecture.md`) + a new generic "custom ACP backend" adapter |
| **Priority** | P0 — cheapest high-value win; the plugin point already exists |
| **Rough effort** | M for the generic-ACP adapter, S per additional built-in adapter |

## Background

Agnes's entire value proposition is "run whatever coding-agent CLI you already use, from anywhere." That only holds if the set of CLIs Agnes actually supports keeps pace with the (fast-growing, fast-changing) universe of coding-agent CLIs. Users don't converge on one CLI — they pick based on which subscription they already pay for, which model family they trust for a given task, or plain habit — so every CLI Agnes can't run is a reason a given user can't fully switch to Agnes.

Two different problems follow from that:

1. **Breadth of maintained, first-class support.** Each additional CLI Agnes ships a real adapter for gets full treatment: proper capability negotiation, resume support where the CLI allows it, a documented feature set.
2. **The long tail.** There will always be CLIs Agnes hasn't (yet, or ever will) write a dedicated package for — niche tools, internal company forks, brand-new entrants. Waiting on an Agnes release to try a new CLI is a real barrier to adoption. If a CLI already speaks ACP (Agent Client Protocol — the JSON-RPC-over-stdio protocol Agnes is built around, see `../../docs/architecture.md`), there is no fundamental reason a user should have to wait for Agnes engineering to write a bespoke package before they can point Agnes at it.

This doc proposes closing both gaps: a small number of additional first-class adapters, and — more importantly — a generic "bring your own ACP CLI" path so users aren't blocked on Agnes shipping code for every CLI that exists.

## Current state in Agnes

Agnes already has the right plugin shape for this: `IAgentAdapter` (`Agnes.Abstractions/Agent.cs`) is a small descriptor-plus-launcher interface, and each supported CLI is its own thin package (`Agnes.Agents.ClaudeCode`, `Agnes.Agents.OpenCode`, `Agnes.Agents.Codex`) that layers CLI-specific launch config over the generic ACP-over-stdio client in `Agnes.Acp`. Per `../../docs/architecture.md`, `Agnes.Acp` already does the hard part — process lifecycle, JSON-RPC framing, capability negotiation, and mapping ACP messages to Agnes's `SessionEvent` model — so a new adapter package is, in the common case, genuinely just configuration: a launch command, some args/env, and a bit of capability-quirk handling.

Two things are missing:

- **Only 3 adapters ship today.** Nothing wrong with that per se, but it's a small fraction of the ACP-speaking CLIs a user might already have installed.
- **No generic "custom ACP backend" path.** Every new provider today requires writing and shipping a new C# package, even though most of what a new adapter needs (a generic ACP-speaking client, capability negotiation, event mapping) already exists in `Agnes.Acp` and doesn't vary per CLI. There's no way for a user or host operator to say "here's a command that speaks ACP, treat it as an agent" without an Agnes code change and release.

`IAgentAdapter.IsAvailable()` today only answers "is the CLI installed and resolvable" (`AgentCommand.IsOnPath`) — that check is generic enough to work unchanged for a user-configured backend too.

## Proposed design

### 1. A generic custom-ACP adapter

The core insight is that `Agnes.Acp`'s client is already generic — it doesn't know or care which CLI it's talking to, only that the CLI speaks ACP over stdio. So a "custom backend" doesn't need a new code path at all, just a data-driven `IAgentAdapter` implementation constructed from user-supplied configuration instead of compiled per-CLI:

```csharp
public sealed record CustomAcpBackendConfig
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public string? DefaultModeId { get; init; }
}

/// <summary>One IAgentAdapter instance per user-configured CustomAcpBackendConfig — constructed
/// at runtime from Host config, not compiled per-CLI like Agnes.Agents.*.</summary>
public sealed class CustomAcpAgentAdapter(CustomAcpBackendConfig config, IAcpClientFactory acpClientFactory) : IAgentAdapter
{
    public AgentDescriptor Descriptor => new() { Id = config.Id, DisplayName = config.DisplayName };
    public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken ct = default) =>
        acpClientFactory.Create(config).StartSessionAsync(options, ct);
}
```

`Agnes.Host` reads a list of `CustomAcpBackendConfig` entries from configuration (or a small admin UI) and materializes one `CustomAcpAgentAdapter` per entry into the same plugin registry that compiled `IAgentAdapter` packages register into (see `../00-plugin-architecture.md`'s `IPluginRegistry<TDescriptor, TProvider>`). Clients — the new-session picker, profiles, permission defaults — see one flat list of agents and cannot tell a built-in adapter from a user-configured one, because from the registry's point of view they're the same interface.

Config lives host-side rather than per-client: Agnes already treats "the host owns the set of agent plugins it can run" as the model (the host is the machine with the CLIs installed; a client is just a viewer/controller). A custom backend is meaningless without the actual binary present on that host, so host-side config is not just simpler, it's the only place the setting could be enforced correctly — a per-client setting would let a client "configure" a backend that doesn't exist on whichever host it happens to be talking to.

### 2. A small number of additional first-class adapters

Prioritize by two independent factors: how popular the CLI actually is, and how cheap it is to support well. A CLI that's already ACP-native (for example, a "Gemini CLI"-style tool with native ACP support) is a near-copy of the existing `Agnes.Agents.OpenCode` package's shape — cheap, do these first. CLIs that don't speak ACP at all and would need a non-ACP bridge (translating an arbitrary tool-call format into Agnes's normalized event model) are a materially bigger lift — that's really a different feature (see `../sessions/06-tool-timeline-normalization.md`) and shouldn't be scoped into "add a new adapter."

### 3. A capability doc per adapter

Parity across many CLIs will always be uneven in practice — not every CLI's ACP implementation supports resuming a session, forking, MCP passthrough, multiple permission modes, or the same content types. Rather than let users discover a missing capability by hitting a confusing failure mid-session, document it explicitly per adapter (e.g. `docs/agents/claude-code.md`): resume support, mode support, image/audio prompt support (the latter two are already modeled by `AgentCapabilities`). This is cheap to produce alongside each adapter and turns "why didn't that work" into something answerable by reading a doc instead of filing a bug.

## Acceptance criteria

- **Given** a host with a `CustomAcpBackendConfig` entry pointing at an installed ACP-speaking binary, **when** a client requests the new-session picker, **then** that backend appears alongside built-in adapters with no visual or behavioral distinction other than its configured display name.
- **Given** a custom ACP backend config pointing at a command that isn't on the host's `PATH`, **when** `IsAvailable()` is checked, **then** it returns `false` and the picker does not offer it as launchable — matching today's behavior for built-in adapters whose CLI isn't installed.
- A session started against a custom ACP backend produces the same `SessionEvent` stream shape (message chunks, tool calls, permission requests, diffs) as a built-in adapter talking to the same underlying ACP protocol version — verified by running the same ACP-conformant test CLI through both a built-in-style adapter and the custom-backend path and diffing the emitted events.
- Adding a new built-in adapter package requires no changes to `Agnes.Host`'s core code, only the new package and a registration entry (non-regression check on the plugin-architecture pattern from `../00-plugin-architecture.md`).
- Removing or misconfiguring one custom ACP backend entry does not prevent the host from starting or affect any other adapter (built-in or custom) — a bad custom config fails closed for that one entry only.
- Each shipped adapter (built-in or documented custom-backend example) has a capability doc stating its actual resume/mode/image/audio support, kept accurate as an explicit non-regression check in review (a capability flip without a doc update is a review blocker).

## Open questions

- Auth handling for custom ACP backends varies a lot per CLI (OAuth device flow, API key, or "already logged in via its own config"). Out of scope for v1 of the generic adapter — v1 assumes the CLI is already authenticated on the host, the same assumption `IsAvailable()` makes today. See `06-provider-authentication-detection.md` for a separate, later feature that could surface login state for custom backends too, if the CLI exposes any way to check it.
- Should a custom ACP backend be allowed to declare which optional capabilities it claims to support (resume, modes, etc.) up front, or should Agnes always probe live via ACP's own capability negotiation? Live negotiation is more honest but assumes every CLI negotiates capabilities correctly; worth validating against a couple of real third-party CLIs before deciding.
