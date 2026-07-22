# Event spine & client UI extensibility

| | |
|---|---|
| **Category** | Architecture / foundation |
| **Plugin surface** | New event bus (host + client) + client UI-extension registries; consumed by host and client plugins |
| **Priority** | P1 — the substrate that makes plugins able to *change behaviour*, not just add isolated providers |
| **Rough effort** | XL |
| **Depends on** | `00-plugin-architecture.md` (host plugins), `00c-client-plugins-and-negotiation.md` (client plugins) |

## Background

The plugin system so far lets a plugin *add* a swappable provider (an agent, a transport, a notification channel). What it can't do is *participate in what the app already does*: observe an action, veto it, or change its payload before it happens; or, on the client, reshape the UI — decorate or replace built-in views (above all the conversation area) and add whole new screens.

Two capabilities close that gap:

1. **An event spine** in both the host and the client — most meaningful actions are *dispatched as events* through a bus, and plugins can **bind** (observe) events and **intercept** them (mutate the payload or cancel the action before it commits). This turns "the app does X" into "the app proposes X, plugins may adjust or veto X, then X happens," uniformly.

2. **Client UI extension points** — plugins can contribute into named UI **slots**, **replace or extend** built-in views (especially the conversation area), and register **custom screens/tabs** that open in place of the conversation view, exactly the way the built-in Settings tab does today.

These are deliberately one design: custom UI and behaviour changes are driven by, and observable through, the same event spine.

## Part 1 — The event spine

### Model

Two kinds of participation, because not every event should be vetoable:

- **Observe** — a fact that already happened; listeners run after the fact and can't change it.
- **Intercept** — an action *about to* happen; ordered interceptors run first and may mutate the payload or cancel the action; the caller checks the result and proceeds or aborts.

```csharp
namespace Agnes.Abstractions.Events;

/// <summary>Marker for anything that flows through the bus.</summary>
public interface IAgnesEvent { }

/// <summary>An action event interceptors can veto or mutate before it commits.</summary>
public abstract class CancelableEvent : IAgnesEvent
{
    public bool IsCanceled { get; private set; }
    public string? CancelReason { get; private set; }
    public void Cancel(string? reason = null) { IsCanceled = true; CancelReason ??= reason; }
}

/// <summary>Runs before an action commits; may mutate the event or cancel it. Lower Order runs first.</summary>
public interface IEventInterceptor<in TEvent> where TEvent : IAgnesEvent
{
    int Order => 0;
    ValueTask InterceptAsync(TEvent evt, CancellationToken ct = default);
}

/// <summary>Runs after dispatch; never changes the outcome.</summary>
public interface IEventObserver<in TEvent> where TEvent : IAgnesEvent
{
    ValueTask ObserveAsync(TEvent evt, CancellationToken ct = default);
}

public interface IEventBus
{
    /// <summary>Runs matching interceptors in Order (each may mutate/cancel); once a CancelableEvent is
    /// canceled, remaining interceptors are skipped. Returns the (possibly mutated) event so the caller can
    /// check IsCanceled. Observers run afterward only if it wasn't canceled.</summary>
    Task<TEvent> DispatchAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : IAgnesEvent;

    /// <summary>Runtime registration (used by dynamically-loaded plugins); returns a handle that
    /// unregisters on dispose. Statically-registered handlers are seeded from DI at construction.</summary>
    IDisposable Intercept<TEvent>(IEventInterceptor<TEvent> interceptor) where TEvent : IAgnesEvent;
    IDisposable Observe<TEvent>(IEventObserver<TEvent> observer) where TEvent : IAgnesEvent;
}
```

Payload mutation is expressed by making the interceptable event's fields settable (or by exposing a mutable builder on it) — e.g. `BeforePromptEvent.Content` is settable so an interceptor can rewrite the prompt; the caller reads the possibly-rewritten value after dispatch.

### Where the spine lives

- The bus contract and default implementation live in `Agnes.Abstractions.Events` (no external deps), so both `Agnes.Host` and the client (`Agnes.Ui.Core`) use one implementation and plugins on either side code against one contract.
- The **host** has one bus instance; the **client** has its own. They are independent in-process buses — the wire protocol already carries cross-machine facts as `SessionEvent`s, so the spine is deliberately *in-process* per app, not a network bus. (A host event and a client event about "the same" thing are distinct events on distinct buses.)

### Plugin binding

- **Host plugins**: a plugin's `ConfigureServices` registers `IEventInterceptor<T>`/`IEventObserver<T>` into its own container; the installer's merger mechanism (from `00-plugin-architecture.md`) collects them and `Intercept`/`Observe`s them on the host bus, unregistering on disable/uninstall.
- **Client plugins**: a client module registers interceptors/observers via the collector (`00c`), gathered onto the client bus when the plugin set is built; dynamically-loaded client plugins register at load and unregister on unload.

### What flows through it (initial set; grows over time)

Not everything at once — the spine plus a representative set proves the model; more actions are routed incrementally.

- **Host**: before-prompt (cancelable/mutable), before-permission-response, session-opened (observe), tool-call-normalized (observe), plugin-installed (observe).
- **Client**: before-notification-shown (cancelable/mutable), tab-opened (observe), before-message-sent (cancelable/mutable), navigation (observe).

A retrofit is mechanical: wrap an existing action call in `DispatchAsync` + an `IsCanceled` check.

## Part 2 — Client UI extensibility

Three escalating levers, all client-side, registered by client plugins:

### 2a. UI slots (contribute into built-in surfaces)

Named insertion points a plugin adds content to without owning the surface — e.g. a button in the composer action row, a banner above the conversation, a panel in the session sidebar.

```csharp
namespace Agnes.Ui.Core.Plugins;

/// <summary>A contribution rendered into a named UI slot. The head renders CreateContent() into the slot;
/// content is a view-model the head resolves to a view (see the rendering note below).</summary>
public interface IUiContribution
{
    string SlotId { get; }   // well-known ids: "composer.actions", "conversation.banner", "session.sidebar", …
    int Order => 0;
    object CreateContent();
}
```

Well-known slot ids are documented constants (`UiSlots.ComposerActions`, …); a head that doesn't render a given slot simply ignores contributions to it (graceful degradation).

### 2b. Renderer overrides (extend/replace built-in item rendering)

The conversation area renders each transcript item (message, tool call, diff, …). A plugin can override how a given item kind renders — e.g. a custom card for a specific normalized tool (`IToolRenderer` from `providers`/`sessions` docs is the tool-call case of this), or a decorator wrapped around the built-in rendering.

```csharp
/// <summary>Overrides or decorates how a transcript item of a given kind renders. Returning null falls
/// back to the built-in renderer; returning a view-model replaces it; a decorator can wrap the built-in.</summary>
public interface IConversationItemRenderer
{
    /// <summary>The normalized item kind this renderer handles (e.g. a ToolKind, "message", "diff").</summary>
    string ItemKind { get; }
    object? CreateView(ConversationItemContext context);
}
```

This is the "modify/replace built-in behaviours and UI elements, especially the conversation area" lever. The registry resolves, per item, the highest-priority plugin renderer for that kind, else the built-in.

### 2c. Custom screens/tabs (replace the conversation view entirely)

A plugin registers a whole screen that opens as a tab — exactly how the built-in Settings tab replaces the conversation view in the dock. The plugin owns the screen's view-model; the head hosts it as a document and offers a way to open it (command palette entry, menu item).

```csharp
/// <summary>A custom top-level screen a plugin contributes, opened as a tab/document like the built-in
/// Settings screen. The head hosts CreateViewModel() as a document and resolves its view.</summary>
public interface ICustomScreenProvider
{
    string ScreenId { get; }     // "myplugin.dashboard"
    string Title { get; }
    string? Icon { get; }
    object CreateViewModel();
}
```

The client plugin set aggregates `IUiContribution`, `IConversationItemRenderer`, and `ICustomScreenProvider` alongside the existing `IClientNotificationChannel`. The desktop head:
- renders slot contributions into the matching surfaces,
- consults the renderer registry when building each conversation item,
- lists custom screens in the command palette / a "plugins" menu and opens the chosen one as a Dock document.

### Rendering note (how a plugin view-model becomes a view)

MVVM heads resolve a view-model to a view via data templates / a view locator. For plugin-provided view-models to render, the head's view locator must be extended to also look in plugin assemblies (or the plugin supplies its view type explicitly). On the desktop head (dynamic client plugins), a plugin ships its own views; the locator resolves them from the plugin assembly. On locked-down heads (iOS/WASM, static plugins only), the same applies but the views are compiled in. This is the one genuinely head-specific, runtime-heavy part; the registries above are head-agnostic and unit-testable, and the desktop view-resolution is wired as the concrete first implementation.

## The retrofitted action event surface (implemented)

Modularity, mirroring the plugin system: the `EventBus` is a small generic dispatcher that knows about **no** concrete events; events are grouped by domain into their own files (`SessionEvents`, `SessionCommandEvents`, `SandboxEvents`, `InteractionEvents`, `GitEvents`, `PluginLifecycleEvents`, `AutomationEvents`, `AuthEvents`, `AgentEventBridge`, client `ClientEvents`), not one monolith; dispatch happens at **each action's own call site**, not in a central router. Plugins bind through the same merger as every other plugin-point, and — because the bus is generic and the contracts live in `Agnes.Abstractions.Events` (referenced by plugins) — **a plugin can define, dispatch, and handle its own event types** with zero core changes (the bus is injected into host plugin DI and exposed on the client `ClientPluginCollector`).

Non-trivial actions now flow through the spine (not direct calls). `Before*` = cancelable/mutable; `*ed` = observe-only:

| Side | Action | Event(s) | Veto behaviour |
|---|---|---|---|
| Host | open session | `BeforeSessionOpenEvent` (redirect adapter/dir) / `SessionOpenedEvent` | error to caller |
| Host | stop session | `BeforeSessionStopEvent` / `SessionStoppedEvent` | stays running |
| Host | fork session | `BeforeSessionForkEvent` (retarget dir) | error to caller |
| Host | prompt | `BeforePromptEvent` (rewrite content) | not sent; notice emitted |
| Host | permission response | `BeforePermissionResponseEvent` (override option) | not forwarded |
| Host | question answer | `BeforeQuestionAnswerEvent` (rewrite answers) | not forwarded |
| Host | mode change | `BeforeModeChangeEvent` (override mode) | no change |
| Host | git commit | `BeforeGitCommitEvent` (rewrite message) / `GitCommittedEvent` | failed result |
| Host | cancel turn | `BeforeSessionCancelEvent` | turn keeps running |
| Host | restart agent | `BeforeAgentRestartEvent` / `AgentRestartedEvent` | agent left as-is |
| Host | resume session | `BeforeSessionResumeEvent` / `SessionResumedEvent` | error to caller |
| Host | pause/resume/delete sandbox | `BeforeSandbox{Pause,Resume,Delete}Event` / `SandboxDeletedEvent` | sandbox unchanged (delete is the safety veto) |
| Host | plugin install/update | `BeforePluginInstallEvent` / `PluginInstalledEvent` | blocked outcome |
| Host | plugin enable/disable | `BeforePluginEnableChangeEvent` / `PluginEnableChangedEvent` | state unchanged |
| Host | plugin uninstall | `BeforePluginUninstallEvent` / `PluginUninstalledEvent` | stays installed |
| Host | schedule task | `BeforeScheduledTaskCreateEvent` / `ScheduledTaskCreatedEvent` | error to caller |
| Host | remove scheduled task | `BeforeScheduledTaskRemoveEvent` / `ScheduledTaskRemovedEvent` | task kept |
| Host | device paired / revoked | `DevicePairedEvent` / `DeviceRevokedEvent` | — (observe; audit) |
| Host | inbound agent event | every `SessionEvent` (e.g. `ToolCallEvent`) + `BeforeAgentEventEvent` | still logged; veto only redacts from clients |
| Client | send message | `BeforeMessageSendEvent` (rewrite text) | not sent |
| Client | show notification | `BeforeNotificationEvent` (rewrite) | not shown |
| Client | interrupt (stop) turn | `BeforeTurnCancelEvent` | turn keeps running |
| Client | close session tab | `BeforeSessionCloseEvent` / `SessionClosedEvent` | tab stays open |
| Client | activate session (navigate) | `SessionActivatedEvent` | — (observe) |
| Client | retry / reconnect | `RetryRequestedEvent` | — (observe) |
| Client | add attachment | `AttachmentAddedEvent` | — (observe) |
| Client | open session/screen tab | `SessionTabOpenedEvent` / `CustomScreenOpenedEvent` | — (observe) |

The inbound agent-event bridge is the one place spine events carry a session **fact** rather than a command: because `SessionEvent : IAgnesEvent`, every event the agent produces is dispatchable, so a plugin observes `ToolCallEvent`, `TurnEndedEvent`, etc. with full typing. It stays observe-by-default — you can't un-happen what the agent did — with a single cancelable `BeforeAgentEventEvent` that only **redacts** an event from clients (it is always appended to the durable log first, so history stays complete).

Deferred (pure-config mutations, low interception value, spread across sync call sites): MCP preset add/remove/toggle and project save. Left off the spine until a plugin actually needs them, rather than bloating the surface.

Each is a **published contract** once shipped: its shape is a compatibility surface. New actions are added the same mechanical way (one event record + one call-site dispatch), so the surface grows without the bus or any registry changing.

## Acceptance criteria

- **AC1** — `DispatchAsync` runs interceptors in `Order`; an interceptor mutating the event's payload is visible to the caller after dispatch; an interceptor calling `Cancel()` sets `IsCanceled`, skips remaining interceptors, and suppresses observers.
- **AC2** — Observers run after a non-canceled dispatch and cannot change the outcome; an observer throwing does not abort the action (its failure is isolated/logged).
- **AC3** — Runtime `Intercept`/`Observe` registrations take effect for subsequent dispatches and stop taking effect once their handle is disposed.
- **AC4** — A host plugin can register an interceptor that cancels a real host action (the wired before-prompt event), and the action is not performed; disabling the plugin restores the default behaviour with no host restart.
- **AC5** — A client plugin can register an interceptor for a real client action (before-notification) that mutates or cancels it, observable end to end against the simulation.
- **AC6** — A client plugin can contribute an `IUiContribution` to a named slot and it appears in the aggregated slot query; a contribution to an unknown slot is ignored, not an error.
- **AC7** — A client plugin can register an `ICustomScreenProvider`; the app lists it and can open it as a document/tab distinct from the conversation view (the desktop head opens it in the dock like Settings).
- **AC8** — A client plugin can register an `IConversationItemRenderer` for a given item kind; the registry resolves that renderer for matching items and falls back to the built-in for others.
- **AC9** — Everything above works with dynamic client plugins on the desktop head and with static (compile-time) plugins on a locked-down head — the registries and bus are DI-free and platform-agnostic; only the desktop view-resolution is head-specific.
- **AC10** — Existing behaviour is unchanged when no plugin binds an event or contributes UI: dispatch with no handlers performs the action exactly as before, and heads with no contributions render exactly as today.

## Open questions

- **Async cancellation semantics**: should a canceled action surface a reason to the user (e.g. "a plugin blocked this prompt")? Start by exposing `CancelReason` and letting the caller decide whether to surface it.
- **Ordering across plugins**: `Order` gives deterministic ordering, but two plugins choosing the same order need a tiebreak (registration order). Document it; revisit if collisions matter.
- **Event surface growth**: which actions become events is a judgement call — too few limits plugins, too many is churn and a performance tax. Start with the representative set above; add on real demand, keeping each event a stable contract once published.
- **View trust**: a plugin view can render arbitrary content in the client. It runs in the client's own UI process (client plugins already do). Keep the same consent posture as other client plugins; a hostile client plugin is a client-trust problem, not one the spine introduces.
- **Renderer performance**: consulting the renderer registry per transcript item must stay cheap (a dictionary lookup by kind). Resolve-and-cache per kind rather than per item where possible.
