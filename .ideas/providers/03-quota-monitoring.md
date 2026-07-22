# Quota and usage monitoring for connected provider accounts

| | |
|---|---|
| **Category** | Providers |
| **Plugin surface** | New optional capability interface on `ICredentialBroker` (see `02-connected-services-credential-broker.md`) |
| **Priority** | P2 |
| **Rough effort** | S |
| **Depends on** | `02-connected-services-credential-broker.md` (this feature reports usage for connected-service profiles, which don't exist without it) |

## Background

Coding-agent sessions can burn through usage quickly — long autonomous runs, large contexts, and multi-turn tool use all consume tokens or request budget far faster than typical chat usage. A user running several sessions in parallel, possibly against several connected accounts (see `02-connected-services-credential-broker.md`), has no visibility today into how close any of those accounts is to a limit until a session fails mid-task with a provider-side quota error — a bad time to discover it, especially for a long-running autonomous session where the failure might not surface for a while.

Surfacing quota/usage information up front lets a user make an informed choice before starting a session ("this account is nearly out for the month, use the other one") rather than after a failure. It's a small, purely informational feature, but it directly prevents a frustrating and avoidable failure mode.

## Current state in Agnes

Nothing today — this is a natural follow-on once `02-connected-services-credential-broker.md`'s multi-profile credential model exists. Without named, persistent connected-service profiles, there's no stable thing to attach a quota reading to.

## Proposed design

An optional capability interface, following the same pattern Agnes already uses for optional sandbox behavior (`IPausableSandbox`/`IStoppableSandbox` in `Agnes.Sandbox`, see `../00-plugin-architecture.md`): not every `ICredentialBroker` implementation can report usage — some providers don't expose a usable API for it at all — so this is an interface a broker implements only when it genuinely can, rather than a method every broker must stub out:

```csharp
/// <summary>Optional capability a ICredentialBroker implements if the provider exposes usage/quota data.</summary>
public interface IQuotaReportingBroker
{
    Task<QuotaSnapshot?> GetQuotaAsync(string profileId, CancellationToken ct = default);
}

public sealed record QuotaSnapshot(string PlanLabel, IReadOnlyList<QuotaMeter> Meters, DateTimeOffset FetchedAt);
public sealed record QuotaMeter(string Name, double? Used, double? Limit, string? Unit);
```

The host caches the last snapshot per profile behind a configurable staleness window rather than fetching on every UI paint — quota data changes slowly relative to how often a client might redraw a badge, and hitting a provider's usage endpoint on every render risks tripping that provider's own rate limits for an endpoint that has nothing to do with actually running the agent. `Agnes.Protocol` gains a `GetQuotaSnapshot`/`OnQuotaChanged` pair on the hub interface so a client can display a badge and receive updates without polling.

## Acceptance criteria

- **Given** a connected-service profile whose broker implements `IQuotaReportingBroker`, **when** a client requests its quota snapshot, **then** it receives plan label, per-meter usage/limit, and a `FetchedAt` timestamp reflecting when the underlying data was actually retrieved (not necessarily "now," if served from cache).
- **Given** a connected-service profile whose broker does not implement `IQuotaReportingBroker`, **when** a client requests its quota snapshot, **then** it receives a clear "not supported" response rather than an error or a stale/fabricated value.
- Repeated quota requests for the same profile within the configured staleness window are served from cache and do not make a redundant call to the provider's usage API — verified by asserting call count against a mock provider across multiple requests inside the window.
- A quota fetch failure (provider API error, network failure) surfaces as a distinguishable "unavailable" state to the client, not as a crash or an indefinitely-spinning UI element, and does not block or fail the underlying agent session.
- Non-regression: a provider that doesn't implement `IQuotaReportingBroker` continues to connect and materialize credentials normally — quota reporting is additive and never gates whether a profile can actually be used.

## Open questions

- Which providers actually expose a usage API stable and accessible enough to be worth wrapping (versus one that's undocumented, likely to change, or effectively requires scraping a web dashboard)? Worth a short spike per provider before committing to build support for it, rather than assuming every connected-service provider has an equally good answer here.
- Should a stale-but-present snapshot be shown with a visible "as of" indicator, or hidden once past some hard expiry? Leaning toward always showing the last known value with an explicit staleness indicator — a slightly-stale number is more useful than no number, as long as it's honestly labeled.
