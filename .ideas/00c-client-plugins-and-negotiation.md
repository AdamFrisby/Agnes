# Client-side plugins & client/host capability negotiation

| | |
|---|---|
| **Category** | Architecture / foundation |
| **Plugin surface** | Extends the plugin system (see `00-plugin-architecture.md`) to the client, plus a two-way negotiation |
| **Priority** | P1 — unlocks the inherently two-sided plugin-points (auth flows, transports, voice, notifications) |
| **Rough effort** | L |
| **Depends on** | `00-plugin-architecture.md` (host plugin model + `IPluginRegistry<T>`) |

## Background

The host plugin system (`00-plugin-architecture.md`) is host-only: the host loads NuGet plugins into isolated `AssemblyLoadContext`s, and clients merely *manage* those plugins over the wire and *consume their effects* through generic contracts (`ListAgents`, `GetCapabilities`, normalized `SessionEvent`s). That's the right model for host-only capabilities (agents, sandboxes, storage, git), but it leaves two things unaddressed:

1. **Some plugin-points are inherently two-sided.** A host that advertises a new auth method, a relay transport, a voice brain, or a notification trigger is only half a feature — the *client* needs a matching counterpart to drive the login flow, dial the transport, capture/play audio, or show the notification. Today those client halves are hardcoded per case (`DevicePairing`, `GitHubDeviceLogin`, `KeypairEnrollment`, `NativeOsNotifier`), so a new one can't be added without editing every client.

2. **Neither party knows what the other supports.** The host advertises its own capabilities (`GetCapabilities()`), but the client never tells the host what *it* can do, and the host never reconciles the two. So the host can't avoid offering a capability the connected client can't use (e.g. voice to a client with no audio), and the client can't avoid showing a control the host can't honor.

This doc specifies **client-side plugins** — with a platform-aware loading model — and a **two-way capability negotiation** so client and host each learn what the other supports and only surface end-to-end-usable features.

## Design goals

- Client plugins use the **same registry abstraction** as the host (`IPluginRegistry<T>` from `Agnes.Abstractions`) — one mental model, not two.
- **Platform-aware loading**: dynamic (runtime-loaded) plugins on platforms that permit it; compile-time ("locked at build") plugins everywhere, including iOS and WASM where dynamic code loading is impossible or forbidden.
- **Negotiation is explicit and symmetric**: both sides advertise, both sides receive a reconciled view, and consumers gate features on "usable end-to-end," not on one side alone.
- **Graceful degradation stays the rule**: a capability only one side supports is simply not offered — never an error.

## Part 1 — Client plugin loading model

A client plugin is registered into a client-side `IPluginRegistry<T>` (reusing `Agnes.Abstractions.PluginRegistry<T>`), populated from up to two **sources**:

### 1a. Static source (compile-time) — every platform

The app references client-plugin assemblies at build time and registers their modules through DI at startup. This is the **only** path on iOS (dynamic code loading is prohibited by App Store policy and the runtime) and WASM (no arbitrary assembly loading), and it's always available on desktop too. "Locked at build time" is exactly this: the set of plugins is fixed when the app is compiled.

```csharp
namespace Agnes.Ui.Core.Plugins;

/// <summary>A client plugin's entry point — mirrors the host's IAgnesPluginModule. Registers the plugin's
/// client-side provider instances (notification channels, auth-flow renderers, transport dialers, …) into
/// the DI container the client plugin registry is built from.</summary>
public interface IClientPluginModule
{
    void ConfigureServices(IServiceCollection services);
}
```

Static registration is just `services.AddSingleton<IClientPluginModule, MyModule>()` (or registering the provider types directly) in the head's composition root. A build flavor for a locked-down platform simply includes only the modules it's allowed to ship.

### 1b. Dynamic source (runtime) — capable platforms only

On the desktop head (and any future head where the runtime permits it), a `DynamicClientPluginLoader` loads plugin assemblies from a local directory (and, later, NuGet — reusing the host's feed/verifier machinery) into a collectible `AssemblyLoadContext`, exactly like the host's `PluginLoadContext`. This path is **compiled only into heads that support it** — the iOS/WASM heads never reference it, so there's no dead dynamic-loading code on a platform that can't use it.

The same security discipline as the host applies to dynamically-loaded client plugins: signature verification, an explicit capability/consent model, and ALC isolation. (Client plugins have *less* to reach than host plugins — no host filesystem/credentials — but they can still touch session content rendered in the UI, so consent is not skippable.)

### Capability reporting

Whichever sources populate it, the client exposes a stable capability descriptor: its platform, whether it supports dynamic plugins, which client plugin-points it implements, and the concrete capability ids it can honor. This descriptor is what negotiation (Part 2) sends to the host.

```csharp
public sealed record ClientPluginCapabilities(
    string Platform,                     // "desktop" | "android" | "ios" | "wasm"
    bool SupportsDynamicPlugins,         // true where an ALC loader is compiled in
    IReadOnlyList<string> PluginPointIds,// client plugin-points populated, e.g. "client.notification"
    IReadOnlyList<string> CapabilityIds);// concrete client capability ids, e.g. "notify.desktop-toast"
```

### Client plugin-points (the two-sided halves)

Defined in `Agnes.Ui.Core.Plugins`, each backing the client half of a two-sided capability:

| Client plugin-point | Client half | Host counterpart |
|---|---|---|
| `IClientNotificationChannel` | show a notification on this device | `INotificationChannel` (host fires the trigger) |
| `IAuthFlowRenderer` | drive a login flow (code entry / device flow / challenge) | `IAuthMethodProvider` (host validates/issues) |
| `ITransportDialer` | dial a transport to reach the host | `ITransportProvider` (host advertises the endpoint) |
| `IClientVoiceProvider` | device STT/TTS (mic/speaker) | `IVoiceProvider` (host-side brain) |
| `IToolRenderer` | custom rendering for a normalized tool call | (none — pure client presentation) |

The demonstrator this backlog builds first is `IClientNotificationChannel`, because the desktop already has a concrete notifier to express as a built-in client plugin, and it's purely client-side (no host-flow complexity) while still exercising the negotiation (the host learns the client can receive notifications).

## Part 2 — Client/host capability negotiation

### The exchange

On connect (and again whenever either side's plugin set changes), the client sends its `ClientPluginCapabilities` to the host; the host reconciles them against its own capability set (`GetCapabilities()` from `00-plugin-architecture.md`) and returns a **reconciled** view. Both parties then reason about what's usable *end to end*.

```csharp
// Agnes.Protocol — the client's advertisement and the reconciled result
public sealed record ClientCapabilities(
    string ClientId, string Platform, bool SupportsDynamicPlugins,
    IReadOnlyList<string> PluginPointIds, IReadOnlyList<string> CapabilityIds);

/// <summary>How a capability id lines up across the two parties.</summary>
public enum CapabilitySupport { HostOnly, ClientOnly, Both }

public sealed record NegotiatedCapability(string Id, CapabilitySupport Support, bool FailClosed);

public sealed record NegotiatedCapabilities(IReadOnlyList<NegotiatedCapability> Capabilities);

// IAgnesServer gains:
Task<NegotiatedCapabilities> Negotiate(ClientCapabilities client);
```

The host stores the client's advertisement per connection (so it can, e.g., skip pushing a voice prompt to a client that can't render it) and returns the reconciliation. `HostConnection` forwards `Negotiate` like every other hub call, and calls it automatically on connect.

### Reconciliation rules

For each capability id known to either party:
- present on **both** → `Both` (the only state where a two-sided feature is offered end to end);
- host-only → `HostOnly` (a host-only capability like sandboxing — usable regardless of client);
- client-only → `ClientOnly` (a purely client capability like a tool renderer — no host dependency).

Consumers decide per capability which states make it usable:
- A **two-sided** capability (voice, a relay transport, a pluggable auth method) requires **`Both`**.
- A **host-only** capability (agents, sandboxes, storage) requires the host side only.
- A **client-only** capability (custom tool rendering) requires the client side only.

`FailClosed` is carried through from the host's capability catalog so a consumer knows whether an absent capability should hard-fail a dependent request or silently hide the UI.

### Why negotiate rather than assume

Without negotiation the host would either over-offer (push voice/relay to a client that can't handle it → dead ends) or under-offer (never expose anything two-sided for fear the client can't). Negotiation makes "only surface what both sides can actually do" a first-class, data-driven decision instead of hardcoded per feature — and it's the same shape whether the mismatch is a locked-down iOS build missing a dynamic plugin or a headless host missing a voice brain.

## Acceptance criteria

- **AC1** — A client plugin can be registered statically (compile-time) and appears in the client's `IPluginRegistry<T>` on every platform, including a build with dynamic loading compiled out (iOS/WASM-equivalent).
- **AC2** — On a head that supports it, a client plugin can be loaded dynamically at runtime into an isolated `AssemblyLoadContext` and appears in the same registry alongside static ones; disabling/unloading it removes it with no app restart.
- **AC3** — A head with dynamic loading compiled out builds and runs with static plugins only, and reports `SupportsDynamicPlugins = false` — no dynamic-loading code is referenced on that platform.
- **AC4** — On connect, the client advertises its `ClientCapabilities` and receives a `NegotiatedCapabilities` reconciliation; the host retains the advertisement for the life of the connection.
- **AC5** — Reconciliation classifies each capability id correctly as `HostOnly` / `ClientOnly` / `Both`, and carries `FailClosed` from the host catalog — verified by unit tests over representative capability sets.
- **AC6** — A two-sided capability (the `IClientNotificationChannel` demonstrator) is reported `Both` only when the host has the trigger *and* the client has a channel; with either side absent it is not `Both`, and the dependent UI/behavior is hidden rather than erroring.
- **AC7** — Existing clients that don't call `Negotiate` continue to work unchanged (the method is additive; the host assumes a minimal client when it hasn't received an advertisement).
- **AC8** — The demonstrator notification path works end to end against the simulation and is unit-tested; the desktop head registers its OS notifier as a built-in `IClientNotificationChannel`.

## Open questions

- **NuGet for client plugins**: dynamic client plugins could reuse the host's NuGet feed/verifier/installer wholesale, or ship as plain assemblies from a local folder first. Start with local-folder + signature check; add NuGet once there's demand, to avoid pulling the full NuGet stack into the desktop client prematurely.
- **Consent UX for client plugins**: client plugins touch session content in the UI but not host credentials/filesystem — is a lighter consent prompt appropriate, or should it mirror the host's capability-consent exactly for consistency? Lean toward the same model for one mental model.
- **Re-negotiation triggers**: renegotiate on every plugin change, or only on connect? Connect-only is simplest and covers the common case; add change-push if a plugin installed mid-session needs to become usable without reconnecting.
- **Trust direction**: the host currently trusts a paired client. Should a host be able to *refuse* a client whose capabilities it distrusts (e.g. an unknown dynamic client plugin)? Out of scope for v1 — negotiation is about feature reconciliation, not a new trust boundary.
