# Foundation: a general-purpose plugin system

| | |
|---|---|
| **Category** | Architecture / foundation |
| **Plugin surface** | This *is* the plugin surface definition |
| **Priority** | P0 — most other docs in this folder depend on it |
| **Rough effort** | L (up from M — NuGet-based distribution and a management UI are real scope, not a footnote) |

## Why this doc exists

This backlog identifies roughly thirty capabilities Agnes needs to grow from an early alpha into a well-rounded remote-coding-agent platform: reachability from outside a LAN, broader provider support, richer session controls, collaboration, voice control, and more. The explicit goal alongside building these is to *not* build them as one-off, bespoke `Agnes.Host` code paths — each should be a swappable implementation behind a small, well-defined interface, the same way agent adapters and sandbox backends already are. This doc names that pattern once, so every other doc in this backlog can refer to it instead of re-deriving it.

This is arguably the single highest-leverage doc in the backlog: get the plugin model right, and the rest of this folder is mostly "write a new package that implements an existing interface" rather than "modify `Agnes.Host` again." Get it wrong, and every subsequent feature either bypasses it (defeating the point) or fights it. The scope here has grown beyond "define some interfaces" to cover the full lifecycle a plugin actually needs: how it's packaged, how a user finds and installs one without hand-copying DLLs, how the host keeps itself safe from code it didn't write, and how any of that is actually usable from a UI rather than a config file.

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

Every interface in this table is a legitimate target for a **third-party plugin package**, not just Agnes's own built-ins — the rest of this doc is about making that real.

## Plugin discovery and loading

`Agnes.Host` already has a plugin loader for agent adapters. Generalize it to a single `PluginRegistry` that:

1. Scans a configured set of assemblies/directories at startup (in-process for built-ins) plus every **installed NuGet-sourced plugin** (see below), each loaded into its own isolated context.
2. Groups discovered types by which plugin-point interface they implement.
3. Exposes typed registries: `IPluginRegistry<IAgentAdapter>`, `IPluginRegistry<ITransportProvider>`, etc. — `Agnes.Host`'s DI container resolves these instead of hardcoding a list.
4. Each registry entry carries its descriptor so a client can show a picker (this already happens for agents in the desktop app's agent picker — reuse the same UI pattern for transport/voice/auth pickers, and for the plugin-management UI described below).

```csharp
public interface IPluginRegistry<TDescriptor, TProvider>
    where TDescriptor : notnull
{
    IReadOnlyList<TProvider> All { get; }
    TProvider? Find(string id);
}
```

## Distributing plugins as NuGet packages

Built-in providers ship compiled into `Agnes.Host`. Third-party and community providers need a way to reach a running host without the user hand-copying DLLs into a plugins folder and hoping the versions line up — that's real friction, and it's exactly the problem an existing, mature package ecosystem already solves. Rather than inventing a bespoke plugin-package format and a bespoke registry to host it, **a plugin is just a NuGet package**, discovered and installed through NuGet's own protocol and tooling. This is a deliberate "don't build what already exists" choice, consistent with Agnes's existing preference for reputable, first-party dependencies (`StreamJsonRpc` over a niche ACP package, `Microsoft.Data.Sqlite` over a random ORM) — `NuGet.Protocol`/`NuGet.Packaging` (Microsoft, first-party) already provide a well-audited client for exactly this: search, download, dependency resolution, and **package signature verification**, all for free.

### Package shape

- **Package type**: NuGet's `packageType` metadata field (a first-class, standard NuGet mechanism for exactly this purpose — distinguishing package *kinds*, the same feature that lets `dotnet tool install` find only tool packages) is set to `AgnesPlugin`. This is what makes a plugin discoverable as a plugin at all, rather than a random library someone happened to tag.
- **Manifest**: the package includes a small `agnes-plugin.json` file declaring:
  - `pluginPoints`: which interface(s) from the table above it implements.
  - `agnesApiVersion`: a semver range of compatible `Agnes.Abstractions` versions — the host refuses to load (with a clear message, not a cryptic type-load exception) a plugin declaring an incompatible range, rather than silently misbehaving against an API surface it wasn't built against.
  - `capabilities`: a declared list of what the plugin needs access to (see **Security model** below) — `network`, `filesystem`, `credentials`, `sessionContent`, or a specific plugin-point's own resource (e.g. a `ITransportProvider` inherently needs `network`).
  - `publisher`, `repositoryUrl`, `homepage` — surfaced in the management UI for the user to make an informed trust decision, the same information a mobile app store shows before an install.
- **Entry point**: the package's main assembly exposes a type implementing a small `IAgnesPluginModule` interface (`void ConfigureServices(IServiceCollection services)`), the one place the loader calls into — everything else about how the plugin registers its `IAgentAdapter`/`ITransportProvider`/etc. instances happens through ordinary DI registration inside that method.

### Where packages come from

- **Default source**: NuGet's public search, filtered to `packageType:AgnesPlugin` (or, on older feeds without packageType support, the `agnes-plugin` tag as a fallback) — no bespoke catalog server for Agnes to build or operate.
- **Additional sources**: standard NuGet package sources (a private feed, an internal Azure Artifacts/GitHub Packages feed, or a folder — all things `NuGet.Protocol` already resolves against), configurable via `Agnes:Plugins:Sources`, the same `Agnes:` configuration namespace every other plugin point already uses. An enterprise self-hoster who wants to run their own internal plugin feed and nothing else just configures that as the only source — no Agnes-specific server software required.
- **An optional "reviewed" signal, not a gate**: Agnes (the project) can maintain a small, published allowlist of package-id + publisher-key pairs it has reviewed, surfaced as a badge in the UI. This is a trust *signal*, not a requirement to install — permissionless publishing (like NuGet itself) stays the default, matching how the rest of the .NET ecosystem works, rather than Agnes becoming a locked-down app-store gatekeeper for its own plugin ecosystem.

## Installing, enabling, and configuring plugins

A new host-side service, `PluginInstaller`, owns the full lifecycle — this is genuinely new work, not just "call NuGet and you're done":

```csharp
namespace Agnes.Abstractions;

public interface IPluginInstaller
{
    Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<InstalledPlugin> InstallAsync(string packageId, string? version, CancellationToken ct = default);
    Task UpdateAsync(string pluginId, CancellationToken ct = default);
    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default);
    Task UninstallAsync(string pluginId, CancellationToken ct = default);
    Task ConfigureAsync(string pluginId, IReadOnlyDictionary<string, string> settings, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledPlugin>> ListInstalledAsync(CancellationToken ct = default);
}

public sealed record PluginSearchResult(string PackageId, string DisplayName, string? Description,
    string Publisher, IReadOnlyList<string> Versions, bool IsReviewed);

public sealed record InstalledPlugin(string PluginId, string Version, bool Enabled,
    IReadOnlyList<string> GrantedCapabilities, bool UpdateAvailable);
```

`InstallAsync` does, in order: download via `NuGet.Protocol`; **verify the package's NuGet signature** (see Security model — this step is not optional); extract to a per-plugin directory under Agnes's data directory; parse and validate `agnes-plugin.json` against the host's own `Agnes.Abstractions` version; surface the declared capability list to the user for explicit consent (below) before anything from the package actually runs; on approval, load the assembly into a new, isolated context and register it into the matching `IPluginRegistry<T>`.

Installed-plugin state (id, version, source, enabled flag, granted capabilities, install date) is tracked in a small persisted table alongside the host's existing event store — this is host state, exactly like paired-device records, not something that lives only in memory.

**This lifecycle is driven over the wire, not just locally.** Agnes's whole premise is managing a host from any paired client — a plugin-management flow that only works by SSHing into the host and running a CLI command would contradict that premise. `Agnes.Protocol`'s `IAgnesHub` gains the corresponding methods (`SearchPlugins`, `InstallPlugin`, `SetPluginEnabled`, `UninstallPlugin`, `ConfigurePlugin`, `ListInstalledPlugins`), so a phone can install a plugin on a desktop machine it's paired to exactly as easily as a person sitting at that machine can.

### Security model

Plugins run arbitrary third-party code with, potentially, access to session content, stored credentials, and the filesystem — this is the part of the feature where getting it right actually matters, not a footnote to defer:

1. **Package signature verification is mandatory by default.** An unsigned package, or one with an invalid signature, is refused. A host operator can opt into allowing unsigned packages (for local development of a plugin before it's published) via an explicit config flag that is off by default and logs loudly every time it's exercised — this should feel like turning off a safety catch, not a normal setting.
2. **Declared capabilities are enforced, not just displayed.** A plugin's manifest states what it needs (`network`, `filesystem`, `credentials`, `sessionContent`, etc.); the host's DI container only makes the specific scoped services backing a *granted* capability resolvable inside that plugin's `ConfigureServices` call — a plugin that didn't declare (and get approved for) `credentials` has no constructor-injectable path to `ICredentialBroker` at all, regardless of what its own code tries to do. Enforcement by absence is much harder to get wrong than a runtime permission check sprinkled through the codebase that a future refactor might accidentally skip.
3. **Explicit, human-readable consent before anything runs.** Before a plugin's code executes for the first time — at install, and again on any update that *adds* a capability it didn't have before — the user sees exactly what it's asking for, in plain language, the same pattern a mobile OS uses for app permissions. No capability is ever silently granted, including on auto-update.
4. **Per-plugin isolation via a collectible `AssemblyLoadContext`** is the default isolation tier: enabling/disabling/updating a plugin doesn't require restarting `Agnes.Host`, and one plugin's dependency versions can't collide with another's or with Agnes's own. This is a real, meaningful boundary (separate load context, separate dependency graph) without the deployment complexity of a second process per plugin.
5. **Process-level isolation is named as a future, opt-in hardening tier, not a v1 requirement.** Agnes already has real sandboxing infrastructure (`Agnes.Sandbox`/`ISandboxProvider`, built for agent CLIs) that a stricter isolation mode for higher-risk plugin categories could plausibly reuse later — worth keeping in mind as the natural next step if a specific plugin category (e.g. anything handling raw credentials or untrusted network input) warrants it, but building that for every plugin from day one would add real overhead and complexity for the common, low-risk case (a notification channel, a git-host detector) that doesn't need it.
6. **Updates that widen the capability set re-trigger consent.** A version bump that only touches code, with no manifest capability change, can update quietly (or automatically, per user preference); one that adds a new declared capability behaves like a fresh install's consent step — a plugin can't grow its own reach over time by attrition.

## Plugin management UI

A "Plugins" screen, following the same visual/interaction language as the existing host and agent pickers:

- **Installed tab**: every installed plugin, with its enabled/disabled toggle, current version, an "update available" badge when relevant, and Configure/Uninstall actions. Disabling is instant and reversible (unloads the `AssemblyLoadContext`, keeps the extracted package and its settings on disk); uninstalling removes both.
- **Browse tab**: a search box against the configured NuGet source(s), results showing name, publisher, short description, and the "reviewed" badge where applicable, with a one-click Install.
- **Detail pane**: full description (the package's own README, rendered — NuGet packages can carry one), publisher and source feed, version history, and the declared capability list with which ones are currently granted.
- **Configure panel**: driven by whatever settings surface the plugin exposes. Most plugins describe a small, flat settings schema (field name, type, label, default) that the host renders generically as a form — covering the common case (an API key, an endpoint URL, a toggle) with zero plugin-side UI code. A plugin with genuinely custom configuration needs (a multi-step OAuth flow, say) can instead supply its own settings view component as an escape hatch, rather than every plugin author being forced to shoehorn something bespoke into a generic-fields form.

## Configuration surface

Each provider needs a place to store its config (API keys, endpoints, feature toggles) without every plugin point inventing its own config file. Reuse the existing `Agnes:` configuration-key namespace from `docs/deployment.md` and extend it: `Agnes:Transport:Tailscale:AuthKey`, `Agnes:Voice:RealtimeCloud:ApiKey`, `Agnes:Plugins:Sources`, etc. — standard ASP.NET Core `IConfiguration` binding, one options class per provider (`TailscaleTransportOptions`, `RealtimeCloudVoiceOptions`, …), each provider only reads its own section. For NuGet-installed plugins specifically, the values a user enters through the **Configure panel** above are what populates this same per-plugin options section — one configuration model serves both a built-in provider shipped in-process and a third-party one installed from a package, so nothing about how a provider reads its own settings needs to know or care which way it arrived.

## Capability negotiation and graceful degradation

A small, host-owned catalog of capability ids is worth building once every plugin point above exists: when a client asks for a capability the connected host's plugin set doesn't provide (e.g. a client requests voice but the host has no `IVoiceProvider` configured or installed), the host should say so explicitly rather than the client discovering it via a failed call. Add a `GetCapabilities()` hub method returning which plugin-point ids are populated on this host, mirroring `AgentCapabilities` but at the host level instead of per-agent, with each capability able to declare whether its absence should hard-fail a dependent request (`fail_closed`) or degrade gracefully (`fail_open`) — e.g. "no voice provider configured" should simply hide the voice UI, not error.

## Acceptance criteria

- **AC1** — A new plugin (e.g. a new `ITransportProvider`) can be added as a new package with zero changes to `Agnes.Host`'s core code, only a registration entry — verified by implementing one full new plugin end-to-end using only the registry/DI surface this doc defines.
- **AC2** — `Agnes.Host` starts successfully with zero optional plugins configured (only the built-in defaults: `Direct` transport, `DirectTls` secure channel, existing agent adapters) — the plugin system adds capability, it never becomes a hard dependency for the base product to run.
- **AC3** — A client calling `GetCapabilities()` receives an accurate list of which plugin-point ids are actually populated on that host, and a request for an unconfigured capability fails with a clear, typed error (not a timeout or generic exception) or degrades gracefully per its declared fail mode.
- **AC4** — Existing `IAgentAdapter` and `ISandboxProvider` implementations continue to work unmodified — this generalization doesn't require touching working code, only extending the pattern to new interfaces.
- **AC5** — Given a plugin package published to a configured NuGet source with `packageType: AgnesPlugin`, when a user searches for it from the **Browse** tab (on any paired client, not just locally on the host), then it appears with name, publisher, and description.
- **AC6** — Given a validly-signed plugin package, when a user clicks Install, then the package downloads, its manifest's `agnesApiVersion` is checked against the host's actual version, the declared capability list is shown for explicit consent, and — only after approval — it loads into a new `AssemblyLoadContext` and appears in the Installed list, enabled.
- **AC7** — Given a package with a missing or invalid signature, when install is attempted with the default configuration, then it is refused with a clear, specific error; it only proceeds if the host operator has explicitly enabled the unsigned-package override.
- **AC8** — Given an installed, enabled plugin, when the user disables it, then its `AssemblyLoadContext` is unloaded and its plugin-point registrations disappear from the corresponding `IPluginRegistry<T>` immediately, with no host restart required, and its files/settings remain on disk.
- **AC9** — Given an installed plugin, when the user re-enables it, then it reloads with its previously-configured settings intact, with no re-install needed.
- **AC10** — Given a plugin update whose manifest declares a capability the currently-installed version didn't have, when the update is applied, then the user is re-prompted for consent to the new capability before the updated code runs — an update never silently grants new access.
- **AC11** — A plugin that did not declare (or was not granted) the `credentials` capability cannot obtain a working `ICredentialBroker` (or equivalent scoped service) from its own `ConfigureServices` registration — verified by a test plugin that attempts to resolve one and confirms the resolution fails or returns nothing usable.
- **AC12** — Given a client paired to a remote host, performing search/install/enable/disable/configure/uninstall from that client produces the same end state on the host as performing the same action from a client running locally on the host machine.
- **AC13** - Review the existing systems and design, and migrate them to the plugin system as built-in plugins. Verify that there are no chunks of functionality remaining that could become plugins.

## Open questions

- Third-party plugin loading isolation beyond `AssemblyLoadContext` (out-of-process, or reusing `Agnes.Sandbox`) is named above as a future hardening tier rather than resolved here — worth revisiting once there's a concrete plugin category that actually needs it, rather than over-building isolation for the common case up front.
- Should `Agnes.Protocol` version its capability list, or is "host and client are always deployed together" an acceptable assumption for v1? Revisit once host and client versions can realistically drift (e.g. once a hosted relay service, if ever built, could sit between differently-versioned hosts and clients).
- Exactly how strict should `agnesApiVersion` compatibility checking be — a semver range the plugin author declares (trusting them to get it right), or something the host can additionally verify by reflecting over the actual `Agnes.Abstractions` types the plugin references at load time (catching a stale or wrong declaration)? The latter is more robust but more work; worth prototyping the simpler declared-range approach first and hardening later if version-mismatch bugs actually show up in practice.
- Should the "reviewed" publisher badge be a simple static allowlist Agnes ships and updates periodically, or does it warrant its own small review-submission process for third-party authors? Start with the simplest version (a static list) and only build process around it once there's real submission volume to justify it.
- Auto-update policy default (notify-only vs. auto-update for capability-neutral patch releases) is a real UX decision with security implications either way — leaning toward notify-only by default, with auto-update as an explicit opt-in per plugin, given these are third-party code paths with real access to a user's session content and credentials.
