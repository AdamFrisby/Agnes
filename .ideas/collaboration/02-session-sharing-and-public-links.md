# Session sharing & public links

| | |
|---|---|
| **Category** | Collaboration |
| **Plugin surface** | New `ISharingBackend` (see `../00-plugin-architecture.md`) |
| **Priority** | P2 |
| **Rough effort** | M |
| **Depends on** | `../security/01-end-to-end-encryption.md` for how shared/relayed session traffic stays protected in transit; `01-friends-and-social.md` for resolving a "recipient" identity if the full direct-sharing path is built (public links do not need this) |

## Background

Every device paired to an Agnes host can currently see every session on that host — there is no concept of scoping a single session to a specific other person, and no way to let someone view a session without giving them a full device pairing to the whole host. Two real use cases fall out of that gap: showing a specific person one particular debugging session (a teammate, a friend helping out) without handing them the keys to every other session and agent on the machine, and sharing a read-only view of a session with someone who isn't going to install Agnes or pair a device at all — e.g. pasting a link into a chat so someone can watch an agent work. This doc specifies both.

## Current state in Agnes

No per-session, per-person access control exists. Authorization today is host-scoped: a paired device with a valid bearer token can subscribe to any session's event stream (`/work/docs/architecture.md`'s security model is "TLS + device-pairing tokens," not per-session permissions). There is no read-only mode, no revocable per-recipient grant, and no public/unauthenticated way to view a session at all.

## Proposed design

Two independent mechanisms, deliberately kept separate because they have very different trust models: sharing with a specific, identified person, and generating a link anyone with the URL can open.

```csharp
namespace Agnes.Abstractions;

public enum SessionAccessLevel { ViewOnly, CanEdit, CanManage }

public interface ISharingBackend
{
    Task<SessionShare> ShareWithAsync(
        string sessionId, string recipientAccountId, SessionAccessLevel level,
        bool allowPermissionApprovals, CancellationToken ct = default);

    Task RevokeAsync(string sessionId, string recipientAccountId, CancellationToken ct = default);

    Task<PublicSessionLink> CreatePublicLinkAsync(
        string sessionId, PublicLinkOptions options, CancellationToken ct = default);

    Task RevokePublicLinkAsync(string sessionId, CancellationToken ct = default);
}

public sealed record PublicLinkOptions(TimeSpan? Expiry, int? MaxUses, bool RequireConsent);
public sealed record PublicSessionLink(string TokenHash, Uri Url, PublicLinkOptions Options, int UseCount);
```

### Direct sharing: three access levels, one orthogonal capability

Modeling access as three ordered levels — **view only** (read the session, cannot send messages), **can edit** (can send messages, i.e. actually drive the agent), **can manage** (can additionally change *other* collaborators' access on this session) — covers the real range of "how much should this person be able to do here" with the minimum number of states that stay easy to reason about. Fewer levels would force awkward workarounds (e.g. no way to let someone drive the session without also letting them re-share it); more would add complexity without a clear corresponding use case.

**Permission approvals** — letting a specific collaborator respond to the target session's tool-permission prompts and thereby cause tool calls to actually execute on the host machine — is kept as an independent, explicitly-granted toggle rather than folded into "can edit." Sending a chat message and approving a tool call that can, say, delete files or run arbitrary commands are very different levels of trust, and Agnes's own permission model already treats them as separate concerns (`PermissionRequestedEvent`/`RespondToPermissionAsync` in `Agnes.Abstractions`, `IAgnesHost.RespondPermissionAsync` in `Agnes.Client`) — a collaborator with the flag set is simply added as an eligible responder to that session's permission requests, reusing the existing event flow rather than inventing a second one. The flag must be structurally impossible to enable for public links, view-only viewers, or inactive sessions — not merely defaulted off in the UI, since the failure mode (a link that can approve destructive tool calls on someone else's machine) is a serious vulnerability, not a cosmetic bug.

Direct sharing needs a recipient identity to share *with* — this is exactly the account-model dependency flagged in `01-friends-and-social.md`. If that full account model isn't ready, a narrower Agnes-specific alternative is worth considering: scope direct sharing to "another device already paired to this host" rather than an arbitrary external account. That covers the common case (a household or team that already shares a host) without waiting on cross-host identity, at the cost of not supporting sharing with someone who has never touched this host at all — a reasonable v1 trade-off to make explicitly rather than blocking on the account decision.

### Public links: read-only by construction, no identity required

A public link is a fundamentally different trust boundary from direct sharing — the recipient is anyone who has the URL, not a specific vetted person — so it should be *structurally* incapable of the things direct sharing allows, not just configured that way by default:

- **Always view-only.** No message-sending, no permission-approval eligibility, ever. This isn't a default that can be switched — the code path for public links should not have a branch that grants write access at all, so there's no configuration mistake that could turn a public link into a remote-control link.
- **Configurable expiration** (e.g. 7 days / 30 days / never) and **a maximum use count**, tracked as a simple counter, so a link can't be treated as a permanent, unbounded grant unless someone explicitly chose "never."
- **An optional consent gate**: if enabled, a viewer must actively click through an "accept and view" step before their IP address and user agent are logged — logging that access without any acknowledgment is worth avoiding by default given a public link can be shared somewhere the session owner doesn't control (a support forum, a group chat with strangers in it).
- **Tokens are stored hashed, never in recoverable form**, matching how Agnes already stores paired-device tokens (`/work/docs/deployment.md`: "Tokens are persisted hashed"). This is a straightforward consequence of treating a link token exactly like a bearer credential — if the token store were ever read (backup, compromise, misconfigured logging), a hashed store leaks nothing usable, while a recoverable one hands out the equivalent of a session password. The cost — a lost/forgotten link can't be recovered, only reissued — is small compared to that risk, and reissuing invalidates the old link automatically, which is also the right behavior for a link that may have leaked.

Because public links don't need any notion of recipient identity, they're the more self-contained half of this doc and a reasonable place to start if the account-model question in `01-friends-and-social.md` isn't resolved yet.

### Protecting shared content in transit — no bespoke crypto

Nothing in this feature should invent a new way to protect data in flight. Whatever confidentiality guarantee applies to ordinary session traffic (see `../security/01-end-to-end-encryption.md`: a mutually-authenticated TLS 1.3 tunnel, established via .NET's `SslStream`, with each peer's certificate pinned by fingerprint at pairing time) applies identically to a shared session's traffic — a share recipient is just another authenticated peer receiving that session's event stream once granted access, not a fundamentally different kind of recipient that needs its own protocol. In particular: a recipient without a registered device/certificate is a recipient without a secure channel to receive the session over, and the fix is to have them pair a device (getting them a pinned certificate the normal way) — not to encrypt a share "payload" to some public key belonging to them ahead of time. That kind of per-recipient asymmetric "seal to their key" scheme is exactly the hand-rolled cryptographic pattern `../security/01-end-to-end-encryption.md` rejects for the rest of Agnes, and there is no reason session sharing should be the one feature that reintroduces it — a viewer with a live, pinned, mutually-authenticated connection can simply receive the (already-protected) event stream like any other client.

### Enforcement point

"Public links are always read-only and never permission-approval-eligible," and "a revoked share stops working immediately," should both be hard invariants enforced in `SessionManager` (the host-side component that already gates session subscription), not merely UI-level defaults that a client could bypass by calling the underlying host methods directly. Anything security-relevant here needs to be true regardless of which client is asking.

## Acceptance criteria

- **AC1 — Access levels are enforced, not advisory.** Given a collaborator has `ViewOnly` access, when they attempt to send a message to the session, then the host rejects the request server-side (not just hides the compose UI) — verified by calling the underlying send-message method directly, bypassing any client UI.
- **AC2 — Permission approval requires an explicit, separate grant.** Given a collaborator has `CanEdit` access but `allowPermissionApprovals` was not set, when a permission request is raised on the shared session, then that collaborator is not offered as an eligible responder.
- **AC3 — Public links can never approve permissions or send messages, under any configuration.** An automated test attempting to construct a public link with write or permission-approval capability fails to do so — there is no reachable code path or configuration flag that grants a public link anything beyond read-only viewing.
- **AC4 — Revocation is immediate.** Given a session share or public link is revoked, when the previously-authorized party makes any further request against that session (including on an already-open connection), then it is rejected going forward — no residual access from a live connection opened before revocation.
- **AC5 — Public link limits are enforced.** Given a public link has `MaxUses` set, when the use count reaches that limit, then further attempts to open the link are rejected, and given `Expiry` has elapsed, the link is rejected regardless of remaining use count.
- **AC6 (edge case) — Losing a link token is a reissue, not a recovery.** Given a public link's token is lost (never displayed again after creation), there is no server-side path to retrieve the original token; the only remedy is revoking and creating a new link, which immediately invalidates the old one.
- **AC7 (non-regression) — Unshared sessions are unaffected.** A session that has never been shared behaves exactly as today (visible only to devices paired to its host) with zero behavior change, confirming this feature is additive rather than a change to the default access model.

## Open questions

- Direct sharing's dependency on a cross-host recipient identity (`01-friends-and-social.md`) is unresolved; the "share with another device already paired to this host" fallback described above is a real option if that stalls, and should be evaluated as a possible permanent v1 scope rather than only a stopgap.
- Should a session owner be able to see a list of everyone who has ever viewed a public link (from the consent-gate log), and for how long should that log be retained? Worth scoping deliberately rather than accumulating indefinitely by default.
- Does `CanManage` need finer-grained limits (e.g. can add other collaborators but not remove the original owner) or is "can do everything the owner can except delete the session" sufficient for v1?
