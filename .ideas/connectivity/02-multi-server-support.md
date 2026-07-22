# Multi-server support (multiple relays/accounts in one client)

| | |
|---|---|
| **Category** | Connectivity |
| **Plugin surface** | Core client feature (consumes `ITransportProvider` from `01-relay-and-tunneling.md`) |
| **Priority** | P1 — depends on the relay work landing first |
| **Rough effort** | M |
| **Depends on** | `01-relay-and-tunneling.md` |

## Background

Once Agnes supports connecting through a relay (see `01-relay-and-tunneling.md`), a single relay is no longer necessarily "the" way a user reaches all of their hosts. A realistic user might have a self-hosted relay for their personal machines and, separately, a company-run relay used to reach work machines — both active from the same client at the same time. These two relays are administered by different people, have different trust boundaries (a work relay operator can plausibly see connection metadata for work traffic; that's not something a personal relay operator should ever see for personal traffic), and should never be able to see or interfere with each other's tokens, hosts, or sessions.

Without an explicit concept of "which relay/server am I talking to," a client that supports more than one relay risks real correctness and security bugs: a host discovered through one relay could be confused with a similarly-named host on another relay, or a device token minted for one relay could leak into a request meant for the other. This needs to be modeled explicitly, not left as an accident of however connection state happens to be stored.

## Current state in Agnes

`Agnes.Client` already has part of the right shape for this: per `/work/docs/architecture.md`, it maintains "a connection pool across multiple hosts." But today those are multiple **hosts** (machines running agents), all implicitly reached the same way — directly, with one flat device-token store. There is no concept yet of multiple **relays/servers** as independently-scoped identities, because Agnes has no relay concept at all until `01-relay-and-tunneling.md` lands. Once it does, the client needs a way to know, for any given host, which relay (or direct connection) it was reached through, and to keep each relay's authentication state completely separate from the others.

## Proposed design

This is mostly client-side state management, not a new plugin interface — but it composes directly with `ITransportProvider`, since each server profile is really "a relay/connection endpoint plus the transport used to reach it":

```csharp
public sealed record ServerProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required Uri RelayUrl { get; init; }
    public required string TransportProviderId { get; init; }  // which ITransportProvider this profile uses
}
```

`Agnes.Client` gains a `ServerProfileStore` (mirroring the shape of the existing device-token store) keyed by profile id. `HostConnection`/`AgnesClient` become profile-aware, so a host discovered via one relay is never conflated with a same-named host discovered via another. Each profile gets its own token-store entry — today's implicit single "the host(s) I'm paired with" token store needs to become keyed by profile id (`Dictionary<profileId, token>` at minimum), so that revoking or losing access to one relay can never affect tokens scoped to another.

Keeping this per-profile isolation strict (rather than, say, a single shared token cache with a profile-id tag bolted on) matters because the whole reason to support multiple relays is that they may not trust each other — a bug that let one profile's token be used against another profile's relay would defeat the purpose of separating them in the first place.

## Acceptance criteria

- **AC1** — A client can have two active server profiles simultaneously (e.g. a self-hosted relay and a separately-hosted one) and successfully list/interact with hosts reachable through each.
- **AC2** — A device token issued for profile A is never sent in a request to profile B's relay, even if both profiles happen to have a host with the same display name — verified by inspecting outbound requests under a test with two profiles configured, one host name collision, and confirming per-profile token scoping in the request headers.
- **AC3** — Revoking or deleting one server profile removes only that profile's stored token(s) and cached host list; other profiles are unaffected.
- **AC4** — A host is always attributable to the specific profile it was discovered through, even when two profiles' host lists are shown together in the UI (no cross-profile host-identity confusion).
- **AC5** — Existing single-relay/single-host usage (today's default `Direct` transport, effectively one implicit profile) continues to work with no visible change for users who never add a second profile.

## Open questions

- Should the client show all profiles' hosts merged into one list (with a profile badge per host), or require the user to switch between profiles one at a time? Given Agnes's existing design goal of "one client, dozens of agents across multiple hosts" (`/work/docs/architecture.md`), a merged view with per-host profile badges likely fits Agnes's existing UX better than a hard profile switcher — but this should be validated against how the desktop/mobile host-list UI actually looks once it exists.
- A CLI-based equivalent of "add/use/list server profiles" is low priority until Agnes has a CLI client at all, which it does not today.
