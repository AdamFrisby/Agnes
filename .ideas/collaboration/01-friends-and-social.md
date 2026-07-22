# Friends & social discovery

| | |
|---|---|
| **Category** | Collaboration |
| **Plugin surface** | Core host/protocol feature — foundational for [`02-session-sharing-and-public-links.md`](02-session-sharing-and-public-links.md); doesn't need a swappable-backend plugin interface of its own (see `../00-plugin-architecture.md`), because a contact directory isn't something an operator would reasonably want to substitute at deploy time the way a transport or auth method is |
| **Priority** | P2 — build only once `02-session-sharing-and-public-links.md` needs a "who can I share this session with" list |
| **Rough effort** | M |

## Background

Agnes today is a personal tool: one operator pairs their own devices to their own host and drives their own coding agents from any of them. Direct session sharing with another *person* (see `02-session-sharing-and-public-links.md`) changes that — the moment you can grant someone else access to a session, you need a way to say *who* that someone is, distinct from the mechanics of the grant itself. Two different concerns are easy to conflate here and shouldn't be: "which accounts exist and which ones I consider known collaborators" (this doc) versus "what access level does a given collaborator have on a given session" (the sharing doc). Keeping them separate means the read-only public-link half of sharing can ship without touching identity at all, and this doc can evolve its own UX (search, remove, block) without every change rippling into the access-control logic.

This is explicitly the most speculative document in this backlog. Agnes's current positioning (see `/work/docs/architecture.md`) is single-operator, multi-device — not a multi-user collaboration platform — and it isn't obvious that a personal dev tool needs a social graph at all. This doc specifies what the feature would look like *if* built, but the open question of whether to build it should be treated as genuinely open, not a formality.

## Current state in Agnes

Agnes has no notion of identity above a single paired device. Pairing (`/work/docs/deployment.md`) issues a per-device bearer token scoped to one host; devices are listed and revoked individually (`GET /devices`, `DELETE /devices/{id}`). When GitHub sign-in is used, the host records the authenticated GitHub login on the device record (`DeviceInfo.Subject` in `Agnes.Protocol`) — but that's a property of *one device's authentication to one host*, not a portable account a person carries across hosts. There is no username, no cross-host identity, and no way today for one person to be discoverable by another at all.

## Proposed design

### The account question comes first

Everything below depends on Agnes deciding whether it wants an account concept that exists independently of any single host pairing. Today's security model is deliberately host-centric (device tokens, not user accounts), and that's a reasonable, simple default for a tool whose whole job is "reach machines you own." A friends/discovery layer only makes sense once there's a place above an individual host where "this person" can be represented — most plausibly the relay described in `../connectivity/01-relay-and-tunneling.md`, since a relay already needs *some* way to route "this device belongs to this person" before it can do anything social with that fact. This doc assumes that decision is made elsewhere and describes what sits on top of it; it should not be read as arguing the account model into existence.

### Prefer an address book over a social graph

The original shape considered for this (bidirectional friend requests, accept/reject state, a public search index) is more product surface than a personal dev tool plausibly needs: every one of those pieces (pending-request state, notifications, blocking, spam handling) is a real design and moderation problem for what is, at bottom, "remember the three people I sometimes share a debugging session with." A simpler model covers the same real use case with much less surface: a one-directional **contact list**, address-book style. Adding someone to your contacts requires no action from them and grants them nothing by itself — it only pre-populates the picker when *you* go to share a session. The actual grant of access still happens explicitly in `02-session-sharing-and-public-links.md`; nothing here exposes session content to anyone.

```csharp
namespace Agnes.Abstractions;

// Lives at the relay/account layer, not on an individual host — an account is
// meaningful across every host a person's devices are paired to.
public sealed record AgnesAccount(string Id, string Username);

public interface IContactDirectory
{
    /// <summary>Exact or prefix match on username. Returns an empty list rather than an error
    /// when nothing matches, and never distinguishes "no match" from "match, but not searchable"
    /// to avoid leaking which usernames exist.</summary>
    Task<IReadOnlyList<AgnesAccount>> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>Adds <paramref name="targetAccountId"/> to <paramref name="accountId"/>'s own
    /// contact list. One-directional: does not require or notify the target.</summary>
    Task AddContactAsync(string accountId, string targetAccountId, CancellationToken ct = default);

    Task RemoveContactAsync(string accountId, string targetAccountId, CancellationToken ct = default);

    Task<IReadOnlyList<AgnesAccount>> ListContactsAsync(string accountId, CancellationToken ct = default);
}
```

An even smaller v1 than the full directory is worth naming explicitly: skip `SearchAsync`/the directory entirely, let a user share a session by typing the exact username or account id, and have Agnes auto-populate a "recent collaborators" list from accounts that have actually been shared with before. That covers the real workflow (share with the same two or three people repeatedly) with zero new moderation, privacy, or discoverability surface. Build the searchable directory only if usage shows people are repeatedly asking "how do I find someone" rather than already knowing who they mean.

### Identity linkage

For accounts backed by GitHub sign-in, auto-claim the GitHub login as the username the first time that GitHub identity authenticates — but only if the username is unclaimed, and never by overwriting an existing claimed username. This reuses identity Agnes already verifies (GitHub device-flow SSO, `/work/docs/deployment.md`) instead of inventing a second identity system; it costs nothing extra to check and avoids asking users to pick yet another handle. Self-hosted deployments that don't want any GitHub dependency at all — consistent with Agnes's existing keypair sign-in path, which exists specifically for operators who don't want a third-party identity provider in the loop — should be able to allow direct username claiming instead, gated by an explicit opt-in configuration flag, mirroring the existing `Agnes:Auth:GitHub:Enabled` toggle pattern in `/work/docs/deployment.md`.

## Acceptance criteria

- **AC1** — Given an account has claimed a unique username, when another account searches for that exact username (case-insensitive), then exactly one matching account is returned.
- **AC2** — Given account A adds account B to A's contact list, when A lists contacts, B appears; when B lists B's own contacts, A does not automatically appear (one-directional — B must separately add A to see A there).
- **AC3** — Removing a contact removes it only from the remover's own list, takes effect immediately for future share-target lookups, and does not revoke any session access already granted under `02-session-sharing-and-public-links.md` (revocation there is explicit and independent).
- **AC4** — GitHub-linked username claiming never overwrites an already-claimed username: if a GitHub login's default username is taken, the account is created without a username collision, not by silently reassigning the existing claim.
- **AC5** — A self-hosted deployment can disable the GitHub-linked identity path and allow direct username claiming instead, controlled by a single configuration flag, without requiring a GitHub App to be registered at all.
- **AC6 (edge case)** — A search for a query that matches no account returns an empty list, not an error, and the response time/shape for "no match" is indistinguishable from "match exists but is not searchable," to avoid using search as a username-enumeration side channel. Repeated searches from one account are rate-limited.
- **AC7 (non-regression)** — An Agnes deployment that never enables any collaboration feature is unaffected: host-pairing-only usage (today's behavior) continues to work with zero account, username, or contact-list concept required anywhere in that flow.

## Open questions

- Does Agnes want a social/discovery layer at all? Its current positioning is a single-operator, multi-device personal tool, not a multi-user collaboration platform. Confirm real demand — e.g. users actually asking to share sessions with named collaborators — before investing here; this is the most speculative item in the backlog and shouldn't be built on spec alone.
- The account-model decision this doc depends on (identity that exists above a single host pairing) is a bigger product decision than this doc should make unilaterally. It needs explicit discussion, most likely alongside `../connectivity/01-relay-and-tunneling.md`, before implementation starts.
- Should username search be opt-in (a user must explicitly make themselves discoverable) rather than on-by-default? Given this is a developer tool where some users may not want to be findable at all, defaulting to "discoverable" deserves scrutiny rather than being assumed.
- Full mutual friend-request flow (with accept/reject and notifications) vs. the one-directional address-book model proposed above — revisit only if the simpler model demonstrably fails to cover real usage.
