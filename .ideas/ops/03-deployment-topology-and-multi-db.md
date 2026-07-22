# Deployment topology & multi-database support

| | |
|---|---|
| **Category** | Operations |
| **Plugin surface** | Formalize the existing `IEventStore` split (`SqliteEventStore` / `InMemoryEventStore` in `Agnes.Host.Events`) as a proper provider abstraction |
| **Priority** | P3 — real, but scale-driven; premature ahead of an actual multi-tenant deployment need |
| **Rough effort** | L |
| **Depends on** | `../connectivity/01-relay-and-tunneling.md` (this doc only becomes relevant once a relay component exists) |

## Background

"Deployment topology" questions — do we support Postgres, do we ship Kubernetes manifests, do we need a multi-database story — only make sense relative to what's actually being deployed. Agnes's architecture (`docs/architecture.md`) is explicit about this: **one host is one daemon on one machine**, running that machine's coding-agent CLIs and serving the clients paired to it. It is not a shared multi-tenant service that many unrelated users' data flows through. That's a fundamentally different deployment shape from a typical SaaS backend, and it means most of the "scale the database" and "run this in a cluster" questions that apply to multi-tenant server software simply don't apply to an Agnes host.

Where those questions *do* start to matter is a different, not-yet-built component: a **relay** that lets a host be reached from outside its own LAN without the user managing port-forwarding or dynamic DNS (`../connectivity/01-relay-and-tunneling.md`). A relay is, by design, a shared service that many hosts and clients connect through — that's the piece that could plausibly need centralized storage, high availability, and a real "what's our deployment topology" conversation. This doc exists to draw that boundary clearly, so storage/deployment effort goes toward the component that will actually need it, instead of being spent hardening the per-machine host against scale problems it doesn't have.

## Current state in Agnes

Agnes ships one Docker image (`Agnes.Host`), one `compose.yaml` service, and SQLite as the only event-store backend (`Microsoft.Data.Sqlite`, per `docs/architecture.md`'s dependency list). `IEventStore` already exists as an interface in `Agnes.Host.Events` with two implementations, `SqliteEventStore` (durable) and `InMemoryEventStore` (used by default when no database path is configured, and in tests) — so the shape of a pluggable store already exists internally, it's just not exposed as a first-class plugin point the way agent adapters and sandbox providers are, and there is no Postgres (or any other) backend, and no Kubernetes deployment story.

## Proposed design

The per-machine host doesn't need a new database option — SQLite is the right store for a single-machine daemon with one writer and no cross-machine coordination requirement, and introducing Postgres here would add operational complexity (a database server to run, credentials to manage, network dependency for something that's currently a single file) to solve a scaling problem that doesn't exist at this layer. The useful work is formalizing what already exists as a real plugin point, so a future relay component *can* plug in a different backend without the host's storage code needing to change:

```csharp
namespace Agnes.Abstractions;

// Promote today's Agnes.Host.Events.IEventStore to Agnes.Abstractions so both
// the per-machine host and a future relay component can implement/consume it
// through the same contract.
public interface IEventStoreProvider
{
    Task<SessionEvent> AppendAsync(string sessionId, SessionEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<SessionEvent>> ReadSinceAsync(string sessionId, long sinceSequence, CancellationToken ct = default);
}
```

- **Per-host storage (`Agnes.Host`)**: keep SQLite as the only supported store. It already fits the actual requirement — durable, ordered, single-writer, zero operational overhead beyond a file on disk — and the case for anything else hasn't materialized. Moving the existing `IEventStore` up into `Agnes.Abstractions` (without changing its behavior) is worth doing on its own merits, independent of any multi-database goal: it turns an internal implementation detail into a real plugin point consistent with how the rest of Agnes's plugin surface is organized, which is what a future relay implementation would need to build against.
- **Relay storage (once `../connectivity/01-relay-and-tunneling.md`'s relay exists)**: this is where a second backend option could genuinely earn its place, *if* the relay needs to track pairing/routing state (which host is reachable where, which client is paired to which host) at a scale or availability level SQLite can't comfortably provide. Even then, the relay's storage needs should stay much lighter than a typical multi-tenant application database: the relay doc's own design goal is that the relay forwards bytes between paired hosts and clients without needing to see decrypted session content, so it only ever needs to persist routing/pairing metadata, not session data. That's a small, well-bounded schema — worth confirming this stays true before assuming the relay needs anything as heavy as Postgres.
- **Kubernetes manifests**: only relevant once the relay is a real, separately-deployed service that operators might run at meaningful scale. The per-machine host runs on a developer's own machine by definition — packaging it for a Kubernetes cluster doesn't match how it's meant to be used, and building that story now would be speculative effort with no current deployment target.

## Acceptance criteria

*(Applies once this work is picked up — currently blocked on the relay existing; see priority note above.)*

- Given `Agnes.Host` is run with its default configuration, when it starts, then it continues to use SQLite (or in-memory, if no database path is configured) exactly as it does today — promoting `IEventStore` to `IEventStoreProvider` in `Agnes.Abstractions` must not change default host behavior.
- Given the relay component exists and needs to persist pairing/routing state, when it's implemented, then it does so against `IEventStoreProvider` (or a comparably-scoped new interface for routing state) rather than a bespoke, one-off storage layer.
- Given no relay-scale requirement has been demonstrated, when this doc is revisited, then a Postgres (or other) backend for the relay is only built if there's a concrete operational reason SQLite can't serve the relay's actual (metadata-only) storage needs — not added speculatively.
- Given the per-machine host, when this doc's proposals are implemented, then no Kubernetes manifests or multi-database support are added to `Agnes.Host` itself — that scope stays confined to the relay.

## Open questions

- This entire doc is arguably premature relative to most of the rest of the backlog. It's documented here for completeness, but the concrete recommendation is to leave it unstarted until `../connectivity/01-relay-and-tunneling.md`'s relay exists and there's a real, measured reason SQLite can't serve its (metadata-only) storage needs.
- If/when the relay does need a second backend, should that be Postgres specifically, or is a lighter embedded option (e.g. staying on SQLite with WAL mode and read replicas, if the relay's read/write pattern allows it) sufficient? Worth revisiting against the relay's actual traffic pattern once it exists, rather than assuming Postgres is the default answer.
