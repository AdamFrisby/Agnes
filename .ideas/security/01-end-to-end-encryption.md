# Secure channel: end-to-end protection for relayed traffic

| | |
|---|---|
| **Category** | Security |
| **Plugin surface** | New `ISecureChannelProvider` (see `../00-plugin-architecture.md`) |
| **Priority** | P0 — should land alongside or before any relay/tunneling transport carries real traffic through a third party |
| **Rough effort** | XL |
| **Depends on** | `../connectivity/01-relay-and-tunneling.md` |

## Background

Agnes today connects a client directly to a host over TLS: the host *is* the trust boundary, because it's a machine the user owns and controls. That model breaks the moment a **relay** sits between client and host to solve NAT traversal (see `../connectivity/01-relay-and-tunneling.md`) — a relay, whether self-hosted by the user's own organization or run by a third party, is a new party in the connection that can, by default, see and potentially tamper with everything it forwards: source code, prompts, model output, tool-call arguments, file contents, and any credentials in transit.

The goal of this feature is narrow and specific: **traffic between a paired client and a host must be unreadable and unmodifiable by anything sitting on the path between them, including the relay itself**, without weakening the properties Agnes already has for direct (non-relayed) connections.

It's worth being explicit that this is a genuinely optional layer, not a universal requirement: a tool that never introduces a third-party relay at all — every connection stays a direct, ordinary TLS session between a client and a host the user themselves controls — has no real need for anything beyond standard TLS, because there's no additional party in the connection to distrust. Some comparable tools take exactly that simpler path (plain TLS in transit, server/relay can read content, no end-to-end layer at all) as a deliberate complexity tradeoff. That's a legitimate design point, and it's effectively what Agnes's own `DirectTlsChannelProvider` default already is for the common case. The distinction that matters is narrow: the moment a relay operated by someone other than the code's owner is introduced into the path — which is exactly what `../connectivity/01-relay-and-tunneling.md` proposes, to solve NAT traversal — "the relay can read your code" stops being an acceptable default, and that's the specific, narrow case this feature exists to close. Nothing here argues for encrypting things that don't need it.

## A note on cryptographic design philosophy

It's tempting, when building a feature like this, to reach for an assembly of low-level cryptographic primitives (an AEAD cipher here, a key-wrapping scheme there, a custom "seal to a recipient's public key" construction) and call the result "end-to-end encryption." **This is an anti-pattern worth naming explicitly**: a protocol built by combining primitives without formal security analysis is exactly how real-world cryptographic systems fail — subtle composition bugs (nonce reuse, missing authentication of associated data, confusable message framing) are the norm, not the exception, and they're invisible until someone attacks the system in production.

The standard, boring, and correct answer to "how do I get a confidential, authenticated channel between two parties across an untrusted network" already exists, is formally analyzed, has decades of cryptanalysis behind it, and ships in the .NET base class library: **TLS 1.3**, specifically used with **mutual authentication (mTLS)** so both ends — not just the client — verify who they're talking to. This document deliberately does not propose a bespoke encryption scheme. It proposes reusing TLS twice: once for the ordinary transport hop to the relay (which Agnes already does), and once more, end-to-end, tunneled through whatever the relay forwards, so the relay is cryptographically incapable of reading the inner traffic regardless of what happens to it.

## Current state in Agnes

Host-to-client connections use a standard TLS listener (Kestrel), authenticated via a per-device bearer token issued at pairing time (pairing code, GitHub device-flow SSO, or an offline P-256 keypair challenge). This is sound for direct connections, where the TLS endpoint the client talks to *is* the host. There is no mechanism today for establishing confidentiality between two endpoints when a third party terminates or forwards the connection in between.

## Proposed design

### Core idea: tunnel a mutually-authenticated TLS session through the relay

The relay, once it exists, should be treated as what it structurally is: an untrusted byte-forwarding rendezvous point, not a trusted endpoint. It never terminates the security-relevant TLS session — it just relays already-encrypted bytes between two sockets, the same role a NAT-traversal relay (e.g. a TURN server in WebRTC, or `ssh -W`-style port forwarding) plays in other well-established systems.

On top of whatever raw byte stream the relay provides, host and client establish a **second, inner TLS 1.3 session directly with each other**, authenticated with **mutual client certificates** rather than a server-only certificate:

- At pairing time, each device (the host, and each client device) already establishes trust material — extend the existing pairing flows (pairing code / GitHub device flow / offline keypair) so each side also generates a self-signed X.509 certificate bound to its existing device keypair.
- Each peer **pins the other's certificate by its SHA-256 fingerprint** at pairing time — a trust-on-first-use model, exactly analogous to how SSH host keys work: no certificate authority is required, trust is established once, in a context the user already controls (the pairing flow), and any subsequent mismatch is a hard failure, not a warning to click through.
- Every connection — whether direct or relayed — performs this inner mTLS handshake using .NET's own `SslStream`, which is backed by the operating system's TLS implementation (SChannel on Windows, OpenSSL on Linux, Secure Transport on macOS) — code that is independently audited, patched, and maintained far more rigorously than anything Agnes could realistically build and vet itself.
- The relay sees only the outer transport-layer TLS (its own ordinary TLS listener, terminated normally, the same as any web service) wrapping an opaque, already-encrypted inner stream it cannot decrypt, inspect, or modify without both peers detecting a certificate-fingerprint mismatch.

```csharp
namespace Agnes.Abstractions;

/// <summary>Establishes a mutually-authenticated, end-to-end encrypted channel between a host
/// and a client over an arbitrary underlying byte stream — a direct socket, or one forwarded
/// through an untrusted relay. The underlying transport never needs to be trusted.</summary>
public interface ISecureChannelProvider
{
    string Id { get; }   // "direct-tls" (today's default — no inner tunnel needed) | "pinned-mtls"

    /// <summary>Wraps a raw stream in an inner TLS 1.3 session authenticated by the local
    /// device's certificate, verifying the remote peer's certificate against its pinned
    /// fingerprint. Throws if the fingerprint doesn't match — never falls back silently.</summary>
    Task<Stream> WrapAsync(
        Stream rawStream,
        DeviceCertificate localCertificate,
        string expectedPeerFingerprint,
        CancellationToken ct = default);
}

public sealed record DeviceCertificate(X509Certificate2 Certificate, string PrivateKeyId);
```

A note on more elaborate alternatives worth pre-empting: it's possible to build a more sophisticated hand-assembled scheme than a flat pre-shared-key cipher — for example, an asymmetric "envelope" construction where a per-session symmetric data key is generated, used for bulk content encryption, and then separately wrapped (encrypted) for each recipient using their public key. This is a real improvement over a single flat shared key, but it does not change the underlying objection: it is still a protocol *composed* from individual primitives by hand, rather than a complete, standard, formally-analyzed protocol — and composition is exactly where these schemes tend to go wrong in ways that don't show up until they're attacked. The recommendation in this doc stands regardless of how many layers a primitive-composition approach adds: use TLS 1.3, mutually authenticated, end to end. It already solves the "different key per session, established fresh, forward secrecy" property an envelope scheme is reaching for, without requiring anyone to get the composition right by hand.

- `DirectTlsChannelProvider` — today's behavior: the outer TLS session (Kestrel to the host directly) already provides everything needed; `WrapAsync` is a no-op passthrough.
- `PinnedMtlsChannelProvider` — used automatically whenever the active `ITransportProvider` (see `../connectivity/01-relay-and-tunneling.md`) isn't `Direct`: performs the inner `SslStream` handshake described above over whatever raw stream the transport hands it.

### Credentials that the relay itself must act on

One deliberate exception: if a future feature needs the relay (or a service acting on the user's behalf, such as an OAuth token exchange) to hold and use a secret rather than merely forward encrypted bytes, that secret cannot be protected by the end-to-end channel above by definition — the party using it has to be able to read it. Any such case (there is no confirmed use case for this in Agnes today) must be treated as an explicitly separate, narrowly-scoped exception, encrypted at rest with a standard authenticated cipher (`System.Security.Cryptography.AesGcm`, with keys derived via `ECDiffieHellman` + HKDF — again, BCL primitives used per their documented construction, not a novel scheme), and called out in its own design doc rather than folded silently into "the encryption feature."

### Recovery

Because trust is pinned per device rather than issued by a recoverable central authority, losing every paired device is a real, foreseeable failure mode. Two options, not mutually exclusive:

1. **Accept the loss as a documented outcome** — matches the security property directly (nothing that never leaves a lost device can be recovered by definition) and is the simplest to implement and reason about.
2. **A recovery credential** — a separate, high-entropy secret shown once at first setup, stored by the user out-of-band (password manager, printed page), that can bootstrap a new device's certificate into the existing trust set without weakening the pinning model for ordinary use. If built, this itself is new attack surface and needs its own threat-model writeup and review — do not bolt it on as an afterthought.

## Acceptance criteria

- **AC1 — Relay cannot read content.** With a test relay configured to log every byte it forwards, a full session (prompts, tool-call output, file diffs) conducted over a relayed connection produces no recoverable plaintext, credentials, or session content in the relay's logs — verified by an automated test that runs the relay, captures its forwarded bytes, and asserts they fail to decrypt without a paired device's private key.
- **AC2 — Relay compromise doesn't retroactively expose past traffic.** Simulate full compromise of the relay process (attacker has root) and confirm previously-captured relayed traffic remains undecryptable without a paired device's private key material (i.e., no scheme where the relay could have derived or cached a usable session key).
- **AC3 — No silent fallback on trust failure.** A connection where the peer's certificate fingerprint doesn't match the pinned value fails the connection outright, surfaces a clear security warning to the user (equivalent to a browser's "certificate doesn't match" or SSH's "host key changed" warning), and does not fall back to an unauthenticated or unencrypted channel under any configuration.
- **AC4 — No regression for direct connections.** Direct (non-relayed) connections continue to work exactly as today, with connection setup latency increasing only by the cost of the additional inner handshake when a relay is actually in use (i.e., zero added overhead for direct-mode users).
- **AC5 — Standard cryptography only.** The implementation introduces no custom or home-grown cryptographic protocol or primitive. All confidentiality/authentication is provided by `SslStream` (TLS 1.3, mutually authenticated) and, for any narrowly-scoped at-rest exception, BCL primitives (`AesGcm`, `ECDiffieHellman`) used per their documented standard constructions. A design or code review that finds a hand-rolled cryptographic construction anywhere in this feature is a blocking finding, not a style nit.
- **AC6 — Independent review before default-on.** The design and implementation receive an independent security review (internal security-minded engineer at minimum; external audit preferred given the stakes) before `PinnedMtlsChannelProvider` is enabled by default for any relay-routed deployment. High/critical findings are resolved, not just filed, before general availability.
- **AC7 — Deliberate recovery story.** The behavior when every paired device is lost is explicit, documented, and tested (either "this data is permanently unrecoverable, and the user is told so clearly during setup" or a working, reviewed recovery-credential flow) — it is not left as an accidental gap discovered by a user during an actual data-loss incident.

## Open questions

- Trust-on-first-use (per-device pinning) vs. an optional CA-backed model for organizations that already run internal PKI and would rather issue and revoke certificates centrally — worth supporting the latter as an alternative `ISecureChannelProvider` configuration for enterprise self-hosters, without making it required for the common case.
- Whether host-side at-rest storage (the existing SQLite event store) should also be encrypted at rest independent of this feature — a related but distinct concern (protects a stolen/backed-up disk, not network traffic) that deserves its own scoping rather than being bundled in here by default.
- Certificate/key rotation and revocation (e.g. a lost or compromised device) — needs a defined process (analogous to the existing `DELETE /devices/{id}` revocation for bearer tokens) before this ships broadly.
