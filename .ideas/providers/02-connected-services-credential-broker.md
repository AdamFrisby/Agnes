# Connected services: reusable provider credentials across hosts

| | |
|---|---|
| **Category** | Providers |
| **Plugin surface** | New `ICredentialBroker` per credential type, host-side (see `../00-plugin-architecture.md`) |
| **Priority** | P1 |
| **Rough effort** | L |
| **Depends on** | `../security/01-end-to-end-encryption.md` (its `ISecureChannelProvider` model is reused for anything sent between paired devices) |

## Background

Agnes is built around one client managing agents across many hosts (per `../../docs/architecture.md`: "one client can connect to dozens of agents across multiple hosts"). That model has an awkward gap today: the credential that lets a coding-agent CLI actually talk to its model provider (a Claude subscription login, an API key, a Codex OAuth session) lives entirely on whichever machine's environment happens to have it configured. Pair a second host — a fresh cloud VM, a new laptop, a teammate's machine — and every provider CLI on it starts from zero: no login, no way to reuse the credential that's already sitting on the first host, short of manually re-running that CLI's own login flow on the new machine.

This also blocks a second real need: using more than one account with the same provider. A user might have a personal Claude subscription and a work one, or a personal API key and a company-issued one, and want to pick which applies per session — something a single "whatever's in the environment" credential can't express at all.

This doc proposes a credential broker for coding-agent provider credentials specifically (as opposed to the git credentials sandboxed agents already broker — see below), so a credential can be connected once, named, and reused — across profiles, and across any host paired to the same set of devices — without re-running a CLI login on every machine.

## Current state in Agnes

Agnes already has a credential broker, but it's scoped narrowly to sandboxed git operations: `ICredentialSource`, `IAgentCredentialProvider`, and `SandboxCredential` (`Agnes.Sandbox/Credentials/`) resolve short-lived, repo-scoped git credentials for an agent running inside a sandbox, with the host-side secret (a PAT, or a GitHub App installation) never leaving the broker process — only the resolved, ideally short-lived credential is handed to the sandbox at push time. That pattern (host holds the real secret; a narrow, purpose-built credential is materialized just-in-time for the thing that actually needs it) is exactly the shape this feature should reuse — it's just currently applied to one specific case (git over HTTPS from inside a sandbox).

There's no equivalent for the coding-agent CLI's own provider credential. Today that's whatever the host environment already has configured — for example, `ClaudeCredentialProvider` (`Agnes.Sandbox/Credentials/ClaudeCredentialProvider.cs`) reads the host's `~/.claude/.credentials.json` and forwards a sanitized access token (never the refresh token — Anthropic's refresh tokens are single-use, so shipping one elsewhere would race the host's own CLI and invalidate one side) plus `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN` from the environment. This works, but only because it assumes exactly one account per provider, permanently tied to whichever host happened to run that provider's login flow.

## Proposed design

Generalize the existing sandbox-credential pattern from "one git credential type, materialized into a sandbox" to "any provider credential, in named, multi-profile form, materialized into any launch":

```csharp
namespace Agnes.Abstractions;

public sealed record ConnectedServiceProfile
{
    public required string ServiceId { get; init; }     // "claude-subscription" | "anthropic-api-key" | "openai-codex" | ...
    public required string ProfileId { get; init; }      // "personal" | "work"
    public required string Label { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>One implementation per credential type/flow (OAuth device flow, API key, setup-token).
/// Mirrors IAgentAdapter's shape: a descriptor plus a live operation.</summary>
public interface ICredentialBroker
{
    string ServiceId { get; }
    Task<ConnectedServiceProfile> ConnectAsync(CredentialConnectOptions options, CancellationToken ct = default);

    /// <summary>Materializes this profile's credential (env vars and/or files) for a specific
    /// agent launch — the same "materialize just-in-time" mechanic Agnes.Sandbox already uses.</summary>
    Task MaterializeAsync(string profileId, ICredentialSink sink, CancellationToken ct = default);
}
```

A few design decisions worth calling out explicitly:

**`ServiceId` is keyed by credential type, not just provider.** A subscription OAuth login and a plain API key for the same underlying provider have different billing models, different materialization (one sets an OAuth bearer token env var, the other a static API key env var), and different renewal behavior (one needs a refresh flow, the other doesn't). Modeling them as distinct service ids rather than two flavors of one service keeps `MaterializeAsync` for each implementation simple — it only ever handles one shape of secret — instead of branching internally on which kind of thing it's holding.

**Where the OAuth exchange happens.** For provider OAuth flows, resist the temptation to build a separate trusted relay whose job is to perform the exchange on the client's behalf. That design solves a real problem for a multi-tenant SaaS backend, where the backend is *not* a machine the user owns and clients are rightly not trusted with server-side secrets. Agnes's architecture is different: the host is already a machine the user owns and controls, and the host is already the party trusted to hold and use credentials (it's what runs the actual agent process). So the simplest correct design is: the host currently handling the connect flow performs the OAuth exchange itself, using per-provider OAuth app configuration it already holds (the same pattern `Agnes:Auth:GitHub:ClientId` already uses for host sign-in, see `../../docs/deployment.md`). No new trusted third party needs to be invented, and no new attack surface (a relay that can mint credentials on a user's behalf) needs to be built for a threat Agnes's trust model doesn't actually have.

**How the credential is protected in transit and at rest — two separate concerns, two standard mechanisms, not one bespoke protocol:**
- *In transit* between paired devices (e.g. syncing a connected-service record from the host that connected it to a second host the user also owns): reuse the mutually-authenticated, pinned TLS 1.3 tunnel from `../security/01-end-to-end-encryption.md`'s `ISecureChannelProvider`. That already gives a confidential, tamper-evident channel between two trusted, paired endpoints — there's no reason to layer a second, custom encryption scheme on top of a channel that's already end-to-end secure.
- *At rest* (the record sitting in a host's SQLite event store, in case a disk is later stolen or backed up insecurely): encrypt with standard BCL primitives — `System.Security.Cryptography.AesGcm` for the credential payload, with the key derived via `ECDiffieHellman` + HKDF from the host's own device keypair. This is deliberately **not** a hand-rolled "seal to a public key" construction (e.g. a NaCl/libsodium-style box) — `ECDiffieHellman`+HKDF+`AesGcm` is the standard, well-analyzed way to achieve the same "encrypt to a recipient's key" property using primitives already in the .NET base class library, consistent with the cryptographic philosophy laid out in `../security/01-end-to-end-encryption.md`.

**Materialization stays just-in-time.** `AgentSessionOptions` (`Agnes.Abstractions/Agent.cs`) gains an optional `ConnectedServiceProfileId`; `IAgentAdapter.StartSessionAsync` implementations call `ICredentialBroker.MaterializeAsync` right before spawning the CLI — the same point in the flow `ClaudeCredentialProvider` already materializes sandbox credentials today. Extending an existing, already-reviewed mechanism to a second call site (non-sandboxed launches) and a richer data model (multiple profiles per service) is a smaller, safer change than introducing a parallel credential-handling path.

**Cross-host reuse falls out of where the encrypted record lives.** Once a connected-service record is stored (encrypted, as above) and reachable by more than one host paired to the same user, any host can request `MaterializeAsync` for a profile it hasn't itself logged into — the client, which is already relaying data between paired hosts, can move the *still-encrypted* record from host A to host B without ever needing to decrypt it itself, mirroring how Agnes already treats bearer tokens as things it moves around without inspecting.

## Acceptance criteria

- **Given** a connected-service profile created on host A (an OAuth-based provider), **when** the same profile is selected for a new session on host B (paired to the same user, never previously logged into that provider), **then** the session starts successfully without any interactive login on host B.
- Provider secrets (API keys, OAuth access/refresh tokens) never appear in plaintext in the SQLite event store or in ordinary application logs on any host — verified by inspecting the store and logs after connecting and using a profile.
- Two profiles for the same `ServiceId` (e.g. "personal" and "work") can be connected simultaneously, are independently selectable per session, and materializing one never leaks the other's credential into the launched process's environment.
- **Given** a profile whose stored token has expired and cannot be silently refreshed, **when** a session is started using that profile, **then** the session start fails with a clear, actionable error (not a silent fallback to an unauthenticated CLI invocation, and not a generic/opaque failure).
- Deleting a connected-service profile removes the host's ability to materialize it immediately; if the credential had been synced to another paired host, that host can no longer materialize it either after its next sync check.
- Non-regression: the existing sandboxed git credential broker (`ICredentialSource`/`IAgentCredentialProvider`/`SandboxCredential`) continues to work unmodified — this feature is additive, not a replacement of that pattern.

## Open questions

- Some providers' OAuth flows require a confidential client secret (unlike GitHub's device flow, which needs none); others may not. Does Agnes ship one shared OAuth app per provider (bundled centrally, like a public client), or does each self-hosted deployment register its own? Likely varies per provider rather than one policy fitting all — worth deciding provider by provider as adapters are built.
- Multi-profile-per-service selection UI (the picker shown at new-session time) is straightforward once the data model above exists, but is worth designing alongside `04-profiles.md` rather than in isolation, since "which connected-service profile" is one of several things a saved session profile bundles together.
- Sync cadence/mechanism for moving an encrypted record between paired hosts (on-demand pull when a host doesn't have a profile locally, vs. proactive push) — start with on-demand pull, since it avoids keeping every host's credential store fully in sync all the time for profiles that host may never actually use.
