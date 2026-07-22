# Enterprise auth (OIDC, mTLS, org/team gating)

| | |
|---|---|
| **Category** | Security |
| **Plugin surface** | New `IAuthMethodProvider` implementations (see `../00-plugin-architecture.md`, and `../connectivity/04-device-linking-and-restore.md`) |
| **Priority** | P3 — low priority until Agnes has organizational deployments |
| **Rough effort** | M once `IAuthMethodProvider` exists, each new method is incremental |

## Background

Agnes's existing auth mechanisms (pairing code, GitHub SSO device flow, offline P-256 keypair challenge) work well for an individual or a small team self-hosting their own instance, where "who's allowed to pair" is effectively "whoever has physical or account access to approve it." That model doesn't scale to an organization that wants to self-host Agnes centrally for a whole engineering team: they typically need to plug into an identity provider they already run (Okta, Azure AD, Google Workspace, or any standards-compliant OIDC provider), scope access to a specific group rather than approving individuals one at a time, and in some environments require certificate-based device authentication as a matter of policy rather than choice.

## Current state in Agnes

Three auth mechanisms exist, each a bespoke flow rather than a pluggable implementation of a common interface. None support scoping access to an organizational group, and none integrate with a third-party identity provider beyond GitHub's own device-flow login.

## Proposed design

This is the most direct consumer of the `IAuthMethodProvider` interface proposed in `../00-plugin-architecture.md` — each new mechanism is simply a new implementation registered alongside the three that exist today:

```csharp
namespace Agnes.Abstractions;

public sealed class OidcAuthMethodProvider(OidcOptions options) : IAuthMethodProvider
{
    // Standard OIDC authorization-code flow against a configured issuer;
    // relies entirely on a mainstream, audited OIDC client library rather
    // than a hand-rolled implementation of the protocol.
}

public sealed class MtlsAuthMethodProvider(MtlsOptions options) : IAuthMethodProvider
{
    // Validates an incoming client certificate against a configured trust
    // anchor (a specific CA, or a pinned set of certificates) as the sole
    // proof of identity — useful for environments with an existing internal
    // PKI. This is a distinct concern from the transport-level mTLS tunnel
    // in ../security/01-end-to-end-encryption.md (that one authenticates a
    // *device* to establish confidentiality; this one authenticates a
    // *user or device* as a login/authorization decision) — the same
    // certificate material can reasonably serve both purposes where an
    // organization already has one, but the two should remain independently
    // implementable so neither is required to adopt the other.
}
```

GitHub org/team gating is narrower in scope — an extension of the *existing* GitHub device-flow login rather than a new provider: add an allowlist option (specific organizations and/or teams) checked against the GitHub API immediately after a successful device-flow login, before a device token is minted. A login that succeeds with GitHub but whose account isn't a member of an allowed org/team is rejected with a clear reason, not a generic auth failure.

## Acceptance criteria

- **AC1** — An administrator can configure an OIDC issuer (client id, client secret, issuer URL) and a user can complete a full pairing flow by authenticating against that issuer, ending with the same per-device revocable bearer token the existing mechanisms produce.
- **AC2** — An administrator can configure an mTLS trust anchor (CA certificate or explicit allowlist) and a device presenting a valid certificate signed by/matching that anchor can pair without any other credential.
- **AC3** — An administrator can restrict GitHub SSO pairing to one or more specific organizations and/or teams; a GitHub login that succeeds but whose account isn't in an allowed org/team is rejected with a distinguishable error, and this is covered by an automated test using a mocked GitHub API response.
- **AC4** — Existing pairing code, unrestricted GitHub SSO, and keypair mechanisms continue to work unmodified when no enterprise auth method is configured — none of this is a breaking change for existing self-hosted deployments.
- **AC5** — Each new `IAuthMethodProvider` implementation ships with its own focused test suite covering both the happy path and rejected/invalid-credential paths; none of them are exempted from the review bar applied to the existing three mechanisms.

## Open questions

- Legitimately low priority relative to the rest of the backlog while Agnes remains an early-stage, largely individual-user project — reasonable to defer until an actual organizational deployment requests it, since the plugin interface this depends on makes adding it later cheap rather than something that needs to be front-loaded speculatively.
- "Keyless SSO"-style flows (avoiding any long-lived shared secret) are worth a follow-up doc of their own if there's real demand, rather than folding an under-specified fourth mechanism into this one.
