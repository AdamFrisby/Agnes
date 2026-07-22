# Foundation: a general-purpose plugin system

| | |
|---|---|
| **Category** | Architecture / foundation |
| **Plugin surface** | This *is* the plugin surface definition |
| **Priority** | P0 — most other docs in this folder depend on it |
| **Rough effort** | M |

## Background

This backlog identifies roughly thirty capabilities Agnes needs to grow from an early alpha into a well-rounded remote-coding-agent platform: reachability from outside a LAN, broader provider support, richer session controls, collaboration, voice control, and more. The explicit goal alongside building these is to *not* build them as one-off, bespoke `Agnes.Host` code paths — each should be a swappable implementation behind a small, well-defined interface, the same way agent adapters and sandbox backends already are. This doc names that pattern once, so every other doc in this backlog can refer to it instead of re-deriving it.

## The pattern Agnes already uses

Agnes already has this pattern twice, and both look the same shape:

```csharp
// Agnes.Abstractions/Agent.cs
public interface IAgentAdapter
{
    AgentDescriptor Descriptor { get; }
    Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken ct = default);
    bool IsAvailable() => true;
}

// Agnes.Sandbox/ISandbox.cs
public interface ISandboxProvider
{
    string Name { get; }
    Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default);
    Task<IReadOnlyList<SandboxInfo>> ListManagedAsync(CancellationToken ct = default);
    Task<ISandbox> AttachAsync(string vmName, SandboxSpec spec, bool start, CancellationToken ct = default);
}
```

Common shape: a small **descriptor** (stable id + display name + capability flags), a **provider** interface that constructs/lists/attaches live instances, an **instance** interface for the actual work, and **optional capability interfaces** (`IPausableSandbox`, `IStoppableSandbox`) that an implementation only implements if it supports that extra behavior — callers do an `is`-check rather than every provider needing every method.

Every new plugin point proposed in this backlog follows this same shape: `I<X>Descriptor`, `I<X>Provider`, `I<X>` (the live instance), plus optional capability interfaces where a feature is only sometimes supported.

## New plugin points this backlog needs

| Interface | Lives in | Backs these docs | Built-in implementations to ship |
|---|---|---|---|
| `ITransportProvider` | `Agnes.Abstractions` (contract) + new `Agnes.Transport.*` packages | `connectivity/01-relay-and-tunneling.md` | `Direct` (today's behavior, default), `AgnesRelay`, `Tailscale` |
| `IAuthMethodProvider` | `Agnes.Abstractions` | `security/02-enterprise-auth.md` | `PairingCode`, `GitHubDeviceFlow`, `Keypair` (all three exist today, just not pluginized), `Oidc`, `Mtls` |
| `ISecureChannelProvider` | `Agnes.Abstractions` | `security/01-end-to-end-encryption.md` | `DirectTls` (today's default), `PinnedMtls` |
| `IVoiceProvider` | `Agnes.Abstractions` | `voice/01-voice-assistant.md` | `Device`, `OpenAiCompatible`, `RealtimeCloud`, `LocalNeural` |
| `INotificationChannel` | `Agnes.Abstractions` | `notifications/01-push-notifications.md`, `extensibility/04-channel-bridges.md` | `MobilePush`, `Desktop` (OS toast, already exists, needs pluginizing), `Telegram` |
| `IMcpPresetProvider` | `Agnes.Abstractions` | `extensibility/01-mcp-management.md` | `Curated` (common MCP servers), `Custom` |
| `IPromptRegistryProvider` | `Agnes.Abstractions` | `extensibility/02-prompts-skills-library.md` | `LocalFile`, `Git`, `RemoteCatalog` |
| `ISharingBackend` | `Agnes.Abstractions` | `collaboration/02-session-sharing-and-public-links.md` | `Direct` (host-issued tokens, no third party) |
| `IMemoryIndexProvider` | `Agnes.Abstractions` | `ops/02-memory-search.md` | `TextOnly`, `Embeddings` (pluggable model) |
| `IGitHostProvider` | `Agnes.Abstractions` | `git-and-files/01-deep-git-integration.md` | `GitHub`, `GitLab`, `Bitbucket` |
| `IAutomationTrigger` | `Agnes.Abstractions` | `extensibility/03-automations.md` | `Interval` (exists today), `Cron`, `Webhook` |
| `IBugReportSink` | `Agnes.Abstractions` | `ops/01-bug-reports-and-diagnostics.md` | `GitHubIssue`, `CustomEndpoint` |

## Plugin discovery and loading

`Agnes.Host` already has a plugin loader for agent adapters. Generalize it to a single `PluginRegistry` that:

1. Scans a configured set of assemblies/directories at startup (in-process for built-ins, `AssemblyLoadContext`-isolated for third-party plugins — matches the existing "new agents are new packages, not core changes" philosophy).
2. Groups discovered types by which plugin-point interface they implement.
3. Exposes typed registries: `IPluginRegistry<IAgentAdapter>`, `IPluginRegistry<ITransportProvider>`, etc. — `Agnes.Host`'s DI container resolves these instead of hardcoding a list.
4. Each registry entry carries its descriptor so a client can show a picker (this already happens for agents in the desktop app's agent picker — reuse the same UI pattern for transport/voice/auth pickers).

```csharp
public interface IPluginRegistry<TDescriptor, TProvider>
    where TDescriptor : notnull
{
    IReadOnlyList<TProvider> All { get; }
    TProvider? Find(string id);
}
```

## Configuration surface

Each provider needs a place to store its config (API keys, endpoints, feature toggles) without every plugin point inventing its own config file. Reuse the existing `Agnes:` configuration-key namespace from `docs/deployment.md` and extend it: `Agnes:Transport:Tailscale:AuthKey`, `Agnes:Voice:RealtimeCloud:ApiKey`, etc. — standard ASP.NET Core `IConfiguration` binding, one options class per provider (`TailscaleTransportOptions`, `RealtimeCloudVoiceOptions`, …), each provider only reads its own section.

## Capability negotiation and graceful degradation

A small, host-owned catalog of capability ids is worth building once every plugin point above exists: when a client asks for a capability the connected host's plugin set doesn't provide (e.g. a client requests voice but the host has no `IVoiceProvider` configured), the host should say so explicitly rather than the client discovering it via a failed call. Add a `GetCapabilities()` hub method returning which plugin-point ids are populated on this host, mirroring `AgentCapabilities` but at the host level instead of per-agent, with each capability able to declare whether its absence should hard-fail a dependent request (`fail_closed`) or degrade gracefully (`fail_open`) — e.g. "no voice provider configured" should simply hide the voice UI, not error.

## Acceptance criteria

- **AC1** — A new plugin (e.g. a new `ITransportProvider`) can be added as a new package with zero changes to `Agnes.Host`'s core code, only a registration entry — verified by implementing one full new plugin end-to-end using only the registry/DI surface this doc defines.
- **AC2** — `Agnes.Host` starts successfully with zero optional plugins configured (only the built-in defaults: `Direct` transport, `DirectTls` secure channel, existing agent adapters) — the plugin system adds capability, it never becomes a hard dependency for the base product to run.
- **AC3** — A client calling `GetCapabilities()` receives an accurate list of which plugin-point ids are actually populated on that host, and a request for an unconfigured capability fails with a clear, typed error (not a timeout or generic exception) or degrades gracefully per its declared fail mode.
- **AC4** — Existing `IAgentAdapter` and `ISandboxProvider` implementations continue to work unmodified — this generalization doesn't require touching working code, only extending the pattern to new interfaces.

## Open questions

- Third-party plugin loading (out-of-process vs `AssemblyLoadContext`) is a real security question once plugins can, e.g., see decrypted session content — start with in-repo/in-process-only plugins and revisit isolation once there's an actual out-of-tree plugin to design against.
- Should `Agnes.Protocol` version its capability list, or is "host and client are always deployed together" an acceptable assumption for v1? Revisit once host and client versions can realistically drift (e.g. once a hosted relay service, if ever built, could sit between differently-versioned hosts and clients).
- Versioning story for provider config schema changes (`TailscaleTransportOptions` gaining a field) — likely just normal `IConfiguration` binding + `Nullable<T>` defaults, no need for anything fancier yet.
