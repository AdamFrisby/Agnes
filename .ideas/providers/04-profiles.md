# Session profiles: named, reusable backend configurations

| | |
|---|---|
| **Category** | Providers |
| **Plugin surface** | Core client/host feature; references `IAgentAdapter` and `ICredentialBroker` |
| **Priority** | P2 |
| **Rough effort** | S |
| **Depends on** | `02-connected-services-credential-broker.md` (a profile can reference a connected-service profile), `01-provider-breadth-acp-catalog.md` (custom ACP backends are valid compatible-agent targets) |

## Background

Users tend to repeat the same session-launch configuration for a given kind of task: "OpenCode, pointed at a specific alternate model endpoint, permissions relaxed, for throwaway experiments" versus "Claude Code, default permission prompts, for anything touching a production repo." Re-entering that combination of agent choice, environment variables, and permission defaults every time is repetitive and error-prone — it's easy to forget one environment variable and get a session that silently behaves differently than intended.

A named, saved bundle of launch configuration — pick it once, reuse it forever — removes that repetition and the class of bugs that comes with manually re-specifying the same setup. It also gives a stable "thing to point at" for other features that want to relaunch a session with the same setup it had before (see `../sessions/05-session-read-state-and-shortcuts.md`'s "new session with same setup").

## Current state in Agnes

`AgentSessionOptions` (`Agnes.Abstractions/Agent.cs`) already carries most of the per-launch knobs a profile would bundle: `WorkingDirectory`, `Environment`, `Sandbox`, `SkipPermissions`, `ResumeSessionId`, `McpConfigPath`. What's missing is purely the naming/persistence layer on top — there's no way today to save a particular combination of these under a name and pick it again later.

## Proposed design

This doesn't need a new plugin interface. It's a named, persisted `AgentSessionOptions` template plus a compatibility list (which agents it makes sense with):

```csharp
public sealed record SessionProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<string> CompatibleAgentIds { get; init; } = [];   // AgentDescriptor.Id values, incl. custom ACP
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public string? ConnectedServiceProfileId { get; init; }                // ties into 02-connected-services
    public bool SkipPermissionsDefault { get; init; }
    public bool IsBuiltIn { get; init; }
}
```

Stored host-side — `Agnes.Host`'s existing project-store-adjacent storage is a reasonable home, keeping profiles alongside the other host-owned configuration this backlog already assumes (see `../00-plugin-architecture.md`'s configuration surface). Profiles are surfaced in the new-session picker as an alternative to configuring an agent and its options from scratch.

A few design choices worth spelling out:

**Applied only at launch, fixed for the session's lifetime.** Agnes already has a mechanism for changing behavior mid-session — `SessionMode`/`SetModeAsync` (ACP's `session/set_mode`, e.g. switching between Ask/Code/Plan-style operating modes). A profile is a different, launch-time axis: which agent, which credentials, which starting environment. Keeping profiles strictly launch-time avoids conflating two different kinds of "change how the session behaves" and keeps each mechanism doing one clear thing — mode switching handles in-session behavior changes, profiles handle what a session starts as.

**Editing a built-in profile forks a copy rather than mutating it.** This is the same "duplicate a preset instead of overwriting it" pattern common to any tool with built-in configuration presets — it protects a known-good starting point from accidental modification and means a user can always get back to the original. Implementation-wise it's just an `IsBuiltIn` check that redirects a save to a new id rather than a real distinct mechanism.

**Secret resolution order: shell environment, then a saved secret binding, then an interactive prompt.** This precedence lets a host-level environment variable win for a quick one-off override, a saved binding cover the repeatable/automated case, and an interactive prompt be the last resort so a profile never silently fails to launch just because a needed value isn't set anywhere — the user gets asked instead of getting a cryptic downstream failure. This is specifically for arbitrary environment values a profile carries (e.g. a non-secret endpoint URL, or a secret for a use case `02-connected-services-credential-broker.md` doesn't cover); it is not a substitute for that feature's connected-service credential handling, which remains the preferred, more tightly scoped mechanism for actual provider credentials.

**Starter profiles worth considering** (not required for v1): profiles for common alternate-model-endpoint setups (pointing a compatible agent at a different inference endpoint or model family than its default), and a "relaxed permissions, scratch work" profile for low-stakes experimentation. These are illustrative, not a fixed list — see open questions.

## Acceptance criteria

- Saving the current session-launch configuration under a new name creates a `SessionProfile` that, when selected for a later session, reproduces the same agent, environment, connected-service profile, and permission default.
- A profile's `CompatibleAgentIds` list correctly restricts which agents it's offered for in the new-session picker, including custom ACP backends (`01-provider-breadth-acp-catalog.md`).
- Editing a built-in profile creates a new, independent profile and leaves the original built-in unchanged and still selectable.
- Deleting a profile that a currently-running session was launched from does not affect that already-running session — profile application happens once, at launch.
- **Given** a profile references an environment value with no shell-env override and no saved binding, **when** a session is launched from it interactively, **then** the user is prompted for the missing value rather than the launch failing silently or launching with an empty/incorrect value.
- Non-regression: launching a session without selecting any profile behaves exactly as it does today (profiles are purely additive to the existing `AgentSessionOptions`-based launch path).

## Open questions

- Ship a starter set of built-in profiles, or start with none? Leaning toward starting with none and letting real usage patterns tell us which presets are actually worth shipping by default, rather than guessing up front.
