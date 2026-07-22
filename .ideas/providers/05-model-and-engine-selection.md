# Model and engine selection

| | |
|---|---|
| **Category** | Providers |
| **Plugin surface** | Extends `IAgentAdapter`/`AgentDescriptor` with an optional model-listing capability |
| **Priority** | P2 |
| **Rough effort** | S–M |
| **Depends on** | `01-provider-breadth-acp-catalog.md` (custom ACP backends are engines too, and should get model listing on the same terms as built-in ones) |

## Background

Choosing *which backend to run* and choosing *which underlying model that backend should use* are two different decisions that are easy to conflate. The backend choice — Claude Code vs. OpenCode vs. a custom ACP backend, call it the "engine" — determines the CLI and its tool-use behavior. Within a chosen engine, many CLIs also let the caller pick a specific model (a faster/cheaper one for routine work, a more capable one for a hard problem), and that choice materially affects cost, latency, and answer quality independent of which engine is running.

Agnes's abstractions today capture the engine axis reasonably well (`AgentDescriptor`) but have no notion of the model axis at all. Letting a user pick a model from Agnes's own UI — rather than only ever baking a model choice into a profile's static environment variables — matters because model choice is often a per-task decision made in the moment ("this one's tricky, bump to a stronger model for this session") rather than a fixed property of a saved configuration.

## Current state in Agnes

`AgentDescriptor`/`AgentCapabilities` (`Agnes.Abstractions/Agent.cs`) describe the *agent kind* and its negotiated protocol capabilities (`LoadSession`, `PromptImage`, `PromptAudio`, `Modes`), but nothing about which models that agent can be told to use. `SessionMode` (ACP's `session/set_mode`, covering things like Ask/Code/Plan-style operating modes) is a genuinely different axis from model selection and shouldn't be conflated with it.

## Proposed design

A small, optional capability addition rather than a new top-level plugin point — model listing is naturally scoped to "a thing an agent adapter may or may not be able to do," the same shape as other optional capabilities in this backlog:

```csharp
public interface IModelListingAdapter
{
    /// <summary>Live-probes the provider for currently available models, if it supports that;
    /// null if only a static list is known.</summary>
    Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Static fallback list, used when live probing isn't supported or fails.</summary>
    IReadOnlyList<ModelInfo> StaticModels { get; }
}

public sealed record ModelInfo(string Id, string DisplayName, bool IsCustomEntryAllowed = true);
```

`AgentSessionOptions` gains an optional `ModelId`, threaded through to the underlying CLI invocation by each adapter in whatever way that CLI expects (a flag, an environment variable, or a session-start parameter).

Design choices worth explaining:

**Live probing is optional, with a static fallback.** ACP doesn't standardize a way to list available models, so whether live probing is even possible is entirely adapter- and CLI-specific — some CLIs can be asked what's currently available, others can't. Making `ListModelsAsync` return `null` (rather than requiring every adapter to implement live probing) means the static list always has somewhere to fall back to, so the model picker is never empty just because live probing isn't supported by a given CLI.

**Free-text custom model-id entry.** Providers ship new models faster than Agnes can realistically keep a static list updated. Allowing a free-text entry (gated per-model via `IsCustomEntryAllowed`, so an adapter can still lock this down where a free-text id genuinely wouldn't make sense) means a newly released model is usable the moment the underlying CLI supports it, without Agnes being a bottleneck.

**Favorites are pure client-side state**, keyed by `(AgentDescriptor.Id, ModelInfo.Id)`, with no host involvement. This is a lightweight, personal UX preference with no security or correctness implications, so there's no reason to add host-side storage or round-trips for it — keeping it client-local also means it works identically for a user-configured custom ACP backend the host doesn't specially know about, since the key is just an id pair.

**Favorites are checked against the current live/static catalog before being offered as selectable.** A model can be deprecated or removed by a provider after a user has favorited it; offering a stale favorite as if it still worked would produce a confusing failure only once the user tries to actually start a session with it. Checking against the current catalog at picker-render time surfaces that as a visible "no longer available" state instead, which is a much cheaper place to catch the problem.

## Acceptance criteria

- **Given** an adapter implementing `IModelListingAdapter` with live probing available, **when** the model picker is opened for that agent, **then** it shows the live-probed list.
- **Given** the same adapter but with live probing failing or timing out, **when** the model picker is opened, **then** it falls back to `StaticModels` rather than showing an empty list or an unhandled error.
- A free-text model id can be entered and used for any `ModelInfo` with `IsCustomEntryAllowed = true`, and is rejected with a clear message for one with it set to `false`.
- A favorited `(AgentDescriptor.Id, ModelInfo.Id)` pair persists across an application restart and appears in the favorites rail.
- **Given** a previously favorited model that no longer appears in either the live or static catalog, **when** the favorites rail is rendered, **then** that favorite is visibly marked as no longer available rather than silently offered as a normal, working choice.
- Selecting a model and starting a session results in `AgentSessionOptions.ModelId` actually reaching the spawned CLI invocation in the form that CLI expects — verified per adapter that implements `IModelListingAdapter`.
- Non-regression: an adapter that doesn't implement `IModelListingAdapter` continues to start sessions normally with no model picker shown, exactly as today.

## Open questions

- Which of the currently supported adapters (Claude Code, OpenCode, Codex) actually expose a usable live model-listing API versus needing a hand-maintained static list? Worth a short spike per adapter before finalizing which ones get `ListModelsAsync` support in the first pass.
