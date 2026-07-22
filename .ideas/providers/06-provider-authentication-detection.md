# Provider CLI authentication status & detection

| | |
|---|---|
| **Category** | Providers |
| **Plugin surface** | Optional capability interface on `IAgentAdapter` |
| **Priority** | P2 |
| **Rough effort** | S |
| **Depends on** | none (distinct from `02-connected-services-credential-broker.md` — see Background) |

## Background

There's a real gap between "an agent CLI is installed" and "an agent CLI is actually usable right now." A user setting up a fresh host — or adding a new agent CLI to an existing one — has no way today to tell whether a given CLI is logged in to its provider without simply trying to start a session and watching whether it fails partway through. That's a poor first-run experience, particularly for a less experienced user who won't necessarily know that a cryptic mid-session failure means "you're not logged in" rather than something else entirely.

This is deliberately about **machine-local CLI login state** — whatever login session the CLI itself already has on this particular host, using its own config files or its own OAuth cache — not about Agnes's own managed credential store. It's a genuinely different concern from `02-connected-services-credential-broker.md`, which is Agnes's own multi-profile credential vault that can be materialized into an agent's environment on demand. This feature is about detecting and using whatever the CLI has *already* set up for itself, independent of whether the connected-services feature is even in play for that agent at all — a user who has simply run `claude login` by hand on the host still benefits from Agnes being able to tell them that's the case.

## Current state in Agnes

`IAgentAdapter.IsAvailable()` today answers exactly one binary question: is the CLI installed and resolvable (`AgentCommand.IsOnPath`, `Agnes.Abstractions/Agent.cs`). It has no notion of authenticated versus not. A user pairing to a fresh host has no way to discover "this CLI is installed but not logged in" other than starting a session and watching it fail.

## Proposed design

An optional capability interface, the same pattern used for other "not every implementation supports this" cases in this backlog (see `03-quota-monitoring.md`'s `IQuotaReportingBroker`, and the general pattern in `../00-plugin-architecture.md`):

```csharp
public interface IAuthStatusAdapter
{
    Task<ProviderAuthStatus> GetAuthStatusAsync(CancellationToken ct = default);
}

public sealed record ProviderAuthStatus(
    bool IsLoggedIn,
    string? Identity,
    string? Method,
    string? Issue,
    DateTimeOffset CheckedAt);
```

Surfaced in the agent picker alongside `IsAvailable()` — both become part of the same underlying question a user actually cares about: "can I use this agent right now." A "Log in" action shells out to the CLI's own real login command, run inside Agnes's existing embedded-terminal fallback path (`ICliFallback`/`PtyManager`, per `../../docs/architecture.md`) rather than Agnes trying to reimplement or intercept each provider's login UX — the CLI's own login flow already works correctly and is the thing that's actually authoritative for its own login state, so running it as-is and re-checking status afterward is both the least work and the most correct behavior. A "Check now" action forces a refresh, bypassing any cached status.

**Detection reliability is genuinely uneven across CLIs, and the design should not paper over that.** Some CLIs expose a direct way to check login state (a `whoami`/`auth status`-style command); others only reveal it indirectly, such as via the presence or shape of a config file; some expose no reliable signal at all. Rather than have every adapter implement `IAuthStatusAdapter` and have some of them return a low-confidence guess, an adapter should only implement the interface when it can give an honestly reliable answer. For adapters that can't, the picker simply shows no auth badge at all for that agent (falling back to `IsAvailable()`'s installed/not-installed signal only) — a confidently wrong "not logged in" badge is worse than showing nothing, because it actively misleads a user who may in fact already be logged in.

## Acceptance criteria

- **Given** an adapter implementing `IAuthStatusAdapter` for a CLI that is logged in, **when** its auth status is checked, **then** the returned `ProviderAuthStatus` has `IsLoggedIn = true` along with an `Identity`, `Method`, and a current `CheckedAt` timestamp.
- **Given** the same adapter for a CLI that is not logged in, **when** its auth status is checked, **then** `IsLoggedIn = false` and, where the CLI provides one, `Issue` contains a human-readable reason.
- Triggering the "Log in" action opens the provider's own CLI login flow inside the embedded-terminal fallback, and on completion of that flow, the agent picker's auth status for that agent is automatically re-checked and updated without requiring a manual "Check now."
- "Check now" always performs a fresh check and bypasses any cached status, even if called immediately after a previous check.
- **Given** an adapter that does not implement `IAuthStatusAdapter`, **when** the agent picker is rendered, **then** no auth badge is shown for that agent at all (not an "unknown" or default-false badge) — only its existing `IsAvailable()`-based installed/not-installed indicator appears.
- Non-regression: `IsAvailable()`'s existing behavior and meaning (installed and resolvable, independent of login state) is unchanged by this feature.

## Open questions

- For CLIs where detection is only partially reliable — say, a config file's presence is a reasonable but not certain signal of being logged in — is a middle-ground "last known status, checked N minutes ago" annotation worth supporting, or is the binary "implement the interface confidently, or don't implement it at all" rule from the Proposed design section the right line to hold? Leaning toward holding that line for v1 and revisiting only if a specific adapter's maintainers have a concrete, reasonably reliable signal that just isn't a clean yes/no.
