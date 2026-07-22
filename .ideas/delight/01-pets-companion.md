# Pets / animated companion

| | |
|---|---|
| **Category** | Delight |
| **Plugin surface** | New `IPetPackProvider`, if pursued — see priority note below |
| **Priority** | P3 — genuinely optional; the lowest-stakes, most speculative item in the whole backlog |
| **Rough effort** | M |

## Background

This is included for completeness, not because there's a clear case for building it. It's worth being honest about that up front rather than manufacturing a justification: this is fundamentally a **product-tone question** ("should Agnes have a playful, character-driven presence?") more than an engineering one, and it's the one item in this backlog where "should we build this at all" matters more than "how would we build it." Agnes today reads as a developer tool — a remote interface to coding-agent CLIs, aimed at people who want to check on and steer sessions running elsewhere. An animated companion character is a different kind of product feel, and it's not obvious it belongs here. That's a judgment call for whoever owns Agnes's product direction, not something this doc should presume the answer to.

What *is* worth separating out, because it stands on its own merits independent of any companion-character branding: an always-visible, at-a-glance widget showing "what needs my attention across all my sessions" is a genuinely useful interaction pattern for a tool where sessions run in the background and a user wants ambient awareness without switching to the full app. That idea is worth considering regardless of whether Agnes ever ships anything shaped like a "pet."

## Current state in Agnes

No equivalent exists, and there's no existing product-tone precedent in Agnes either way — the app doesn't currently have any character-driven or overtly playful UI elements to extend or contradict.

## Proposed design

Not recommending detailed design work unless and until the product-tone question above is explicitly answered. If it is pursued, the shape would follow Agnes's established plugin pattern (see `../00-plugin-architecture.md`) so it doesn't require special-casing the core app:

```csharp
namespace Agnes.Abstractions;

public interface IPetPackProvider
{
    Task<IReadOnlyList<PetDescriptor>> ListAvailableAsync(CancellationToken ct = default);
}

public sealed record PetDescriptor(string Id, string DisplayName, Uri SpritesheetUri);
```

- A companion, if built, would be an **optional, off-by-default** UI element — never something that affects session behavior, notifications logic, or anything functional. It should be trivially disableable and ignorable by anyone who doesn't want it, given it's explicitly a taste-driven feature rather than a functional one.
- The **interaction pattern** worth taking seriously independent of the character skin is a small, persistent, at-a-glance status surface: something that shows session-activity state (e.g. prioritizing sessions waiting on input, then failed, then in review, then running, then idle) without requiring the user to open the full app. This overlaps functionally with `../notifications/02-inbox-and-approvals.md`'s global inbox — both answer "what needs my attention right now" — but rendered as a small persistent widget instead of a full inbox screen. If Agnes wants that ambient-awareness pattern without any companion-character branding, it's worth scoping as a plain "session status overlay" and building it on its own merits, rather than building a pet system and treating the overlay as a side effect of it.
- If a companion character *is* pursued, importing custom third-party art packs (rather than only built-in characters) is a reasonable extensibility point given `IPetPackProvider` already generalizes "where does the art come from" — but this is a nice-to-have on top of a feature that itself needs to clear the product-tone bar first, not something to prioritize ahead of that decision.

## Acceptance criteria

*(These only apply if the product-tone question above is resolved in favor of building this. They are not a commitment to build it.)*

- Given the companion feature is disabled (the default), when a user opens any client, then no companion UI element appears anywhere and no related background work runs.
- Given a user enables the companion feature, when a session transitions to a state needing attention (e.g. waiting on a permission response), then the companion's status indicator reflects that within a short, bounded delay — not requiring a manual refresh.
- Given multiple sessions are in different states simultaneously, when the companion or overlay renders its summary, then it reflects a sensible priority order (e.g. waiting-on-input surfaces before idle) rather than an arbitrary or most-recent-only view.
- Given a user disables the companion feature after having it enabled, when they do so, then any related UI, background polling, and downloaded art assets are fully removed or stopped — nothing keeps running invisibly.
- Given `IPetPackProvider` supports importing a custom pack, when an invalid or malformed pack is provided, then the import fails with a clear error rather than partially applying or crashing the client.

## Open questions

- The primary open question is not implementation but direction: does Agnes want any character-driven, playful UI surface at all, given its current tone as a developer-facing remote-session tool? This should be answered explicitly by whoever owns product direction before any further scoping work goes into this doc.
- If the answer to the above is "no, but the ambient status-overlay idea is worth keeping," should that be scoped as a standalone follow-up to `../notifications/02-inbox-and-approvals.md` instead of living under this doc at all?
