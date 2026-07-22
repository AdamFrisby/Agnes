# MCP server management

| | |
|---|---|
| **Category** | Extensibility |
| **Plugin surface** | New `IMcpPresetProvider`; extends the existing `McpRegistry` (see `../00-plugin-architecture.md`) |
| **Priority** | P2 — Agnes already has the core of this |
| **Rough effort** | S–M |

## Background

MCP (Model Context Protocol) servers give a coding agent access to extra tools — browser automation, a documentation lookup, project-management integrations, and so on — beyond what's built into the CLI itself. Configuring them by hand (editing JSON, remembering command/args/env for each server) is real friction, and it gets worse in a multi-host, multi-workspace product like Agnes: a user pairs one client to potentially many hosts and works across many projects, and "which MCP servers are active for this particular agent, on this particular host, in this particular workspace, right now" is not always obvious from a static config file. Two failure modes matter in practice: silently starting a session with fewer tools than the user expected (a misconfigured server was skipped), and silently failing to start a session at all because of one broken server the user didn't even care about for this task. Both need to be visible and controllable rather than discovered by surprise.

## Current state in Agnes

Agnes already has the substantial part of this working: `McpRegistry`/`McpForward` runs MCP servers either host-side or inside the sandbox, and `McpToolCallEvent` gives an audit trail of MCP tool calls even when they happen inside a sandbox. What's missing: a curated set of quick-install presets for common servers, a read-only view of servers already configured natively in an agent CLI's own config (so Agnes doesn't require re-entering config a user already has elsewhere), a preview of the effective merged tool set for a specific host/workspace/agent combination before a session starts, explicit per-scope defaults (all hosts / one host / one workspace) instead of a flatter today's-config model, and a strict-vs-lenient startup validation toggle.

## Proposed design

```csharp
// Agnes.Abstractions — new, small plugin point for curated quick-install entries
public interface IMcpPresetProvider
{
    IReadOnlyList<McpPresetInfo> CuratedPresets { get; }
}

public sealed record McpPresetInfo(string Id, string DisplayName, McpServerInfo Template);
```

Design notes:

- **Three views, not one, because they answer three different questions.** A "configured" view (servers Agnes manages, editable) answers "what have I set up." A read-only "detected" view (servers found in an agent CLI's own native config, importable but not directly editable) answers "what does this specific tool already know about, that Agnes doesn't." A "preview" view (the effective merged config for a specific host+workspace+agent) answers "what will actually be active if I start a session right now." Collapsing these into one screen would either hide native-config servers Agnes doesn't manage, or hide the fact that scope rules mean a server configured "for this workspace only" won't show up somewhere else — keeping them separate keeps each answer honest.
- **Scope rules** (all-hosts / one-host / one-workspace) extend `McpServerInfo` (already in `Agnes.Protocol`) with an `ApplyScope` enum, defaulted at save time and overridable per-session the same way `AgentSessionOptions.McpConfigPath` already lets one specific launch diverge from the default config.
- **Native-config detection** is adapter-level, not central, because every agent CLI's own MCP config format is different — a new `IMcpDiscoveryAdapter.DetectNativeConfigAsync()` implemented per agent package follows the same "provider-specific detection lives with the provider" pattern already used for other per-adapter capability probing.
- **Preview is a pure read**, not a new subsystem: `McpRegistry` already has to compute the effective merged config for a host+workspace combination in order to actually launch a session, so exposing that same resolution logic as a queryable hub method (`PreviewEffectiveMcpConfig(agentId, hostId, workspaceId)`) is additive.
- **Strict mode is a small, explicit safety knob at the point where servers actually get resolved**, not a UI-only setting: a boolean on `McpRegistry`'s startup resolution controls whether an unresolvable *enabled* server aborts session start (strict) or is skipped with a visible warning while the session still starts (lenient, the default) — the point being that failing to launch a routine session because of one unrelated broken MCP server is a worse default experience than a visible warning, but a user who genuinely needs a specific server present for a task should be able to demand that guarantee.

## Acceptance criteria

- Given a curated preset is selected for quick-install, when the user confirms, then a working `McpServerInfo` entry is created from the preset template without the user hand-typing command/args/env.
- Given an agent CLI with its own native MCP config already populated, when the "detected" view is opened, then those servers are listed read-only with an explicit "import" action, and importing one creates an editable Agnes-managed copy without silently overwriting or deleting the CLI's original native config file.
- Given a server scoped to "one workspace," when previewing the effective config for a different workspace on the same host, then that server does not appear in the preview.
- Given strict mode is off (default) and one enabled server fails to resolve, when a session starts, then the session starts successfully with the remaining servers active and a visible warning identifying the failed one.
- Given strict mode is on and one enabled server fails to resolve, when a session start is attempted, then the session fails to start with a clear error identifying which server blocked it.
- Given a server's scope or config is changed while a session is already running, then the running session's tool set is unaffected — changes apply on next session start only, with this documented and not silently assumed.
- Existing `McpRegistry`/`McpForward` sandboxed-execution behavior and `McpToolCallEvent` audit logging continue to work unmodified for servers configured through any of the three views.

## Open questions

- Curated preset list — low-risk, can start with a small handful of genuinely widely-used MCP servers and grow based on actual user requests rather than trying to be exhaustive up front.
- Should "detected" servers be re-scanned automatically (e.g. on session start) or only on explicit user refresh? Leaning toward explicit refresh to avoid surprising config changes appearing mid-session.
