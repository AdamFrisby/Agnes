# Relay server & tunneling (reach your host from anywhere)

| | |
|---|---|
| **Category** | Connectivity |
| **Plugin surface** | New `ITransportProvider` (see `../00-plugin-architecture.md`) |
| **Priority** | P0 — the single biggest reachability gap in Agnes today |
| **Rough effort** | XL |

## Background

Agnes's whole value proposition is "run coding agents on a machine you control, reach them from any device." In practice, the machine that runs the agents is very often a home desktop or a laptop sitting behind a consumer router doing NAT (network address translation) — it has no public IP address and no port anyone outside the LAN can dial into. The devices that want to reach it — a phone on cellular data, a laptop on a coffee-shop Wi-Fi, a browser tab from a different network entirely — are, by construction, outside that LAN.

This is not a niche edge case for Agnes: the whole point of having mobile and web clients (see `/work/docs/architecture.md`) is to be able to check on or steer a running agent from wherever you are, not just from the same network as the host. Without a way to solve this, "remote interface to coding CLIs" quietly degrades to "same-LAN interface," and every user who wants true remote access is left to solve NAT traversal themselves with a port-forward, a dynamic-DNS setup, or a VPN — real operational work that has nothing to do with coding agents.

The standard solution to "two devices, neither with a public listener, need to reach each other" is a **relay**: a third machine with a public, stable address that both sides connect *out* to. Because both the host and the client dial outward, neither needs an open inbound port, and the relay just brokers the connection between them. This is the same shape of problem NAT-traversal relays solve in other well-established systems (e.g. a TURN server for WebRTC, or SSH port-forwarding through a bastion host) — it is a generic networking problem with a generic, well-understood answer, not something specific to coding agents.

## Current state in Agnes

Today, per `/work/docs/architecture.md`, the client connects **directly** to the host's TLS listener (Kestrel), authenticated with a per-device bearer token issued at pairing time. `/work/docs/deployment.md` documents two ways to get a real TLS certificate in front of that listener (reverse proxy, or handing Kestrel a certificate directly) and mentions, as a fallback for people who don't want to expose a port at all, putting the host on a private overlay network (Tailscale/WireGuard) and connecting over that.

All of these require the operator to already have a routable path to the host — a public DNS name plus a certificate, or a private overlay network they've separately set up and joined from every client device. There is no relay, no "host dials out" mode, and no built-in tunneling. Getting a phone on cellular data to reach a home dev machine is entirely the user's own problem today.

## Proposed design

Introduce a transport abstraction that both the host and client code against, so "connect directly" (today's only behavior) and "connect via a relay or overlay network" become interchangeable implementations of the same contract, following the existing plugin pattern from `../00-plugin-architecture.md` (a descriptor + a provider that produces live instances).

```csharp
namespace Agnes.Abstractions;

public sealed record TransportDescriptor
{
    public required string Id { get; init; }          // "direct" | "agnes-relay" | "tailscale"
    public required string DisplayName { get; init; }
    public bool RequiresOutboundOnly { get; init; }    // true for relay/overlay transports — no inbound port needed
}

/// <summary>Host-side: how the host makes itself reachable by clients.</summary>
public interface ITransportProvider
{
    TransportDescriptor Descriptor { get; }

    /// <summary>Starts exposing the host's hub endpoint via this transport; returns the
    /// address(es) clients should be given (a relay-assigned URL, an overlay-network hostname, etc.).</summary>
    Task<TransportEndpoint> ExposeAsync(TransportOptions options, CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}

public sealed record TransportEndpoint(IReadOnlyList<string> ClientAddresses, string? DisplayHint);
```

- **`DirectTransportProvider`** — today's behavior, made explicit as one provider among several rather than the only option: binds Kestrel, returns the host's own address(es). This is the default, so existing deployments need zero configuration changes to keep working.
- **`AgnesRelayTransportProvider`** — dials out to a relay (self-hosted, or an Agnes-operated one if that ever exists) over a persistent outbound connection, the relay assigns the host a stable routable id, and forwards `Agnes.Protocol` traffic between paired clients and this host without terminating or inspecting it. Because a relay is a new party sitting on the network path, it must not be trusted with plaintext by default — see `../security/01-end-to-end-encryption.md`, which defines an `ISecureChannelProvider`/`PinnedMtls` model specifically so the relay only ever forwards already-encrypted bytes. That doc should land before this transport carries real user traffic in a default-on configuration.
- **`TailscaleTransportProvider`** — shells out to `tailscale serve`/`tailscale funnel` to expose the host on the user's own private mesh network. This is worth building as a first-class option (not just documenting it as a manual step, as `docs/deployment.md` does today) because it requires zero new server-side component: a user who already has an overlay network set up gets a one-click "expose this host on my tailnet" button instead of hand-editing Tailscale commands, while still avoiding a public listener entirely.

### The relay server itself

Unlike the other plugin points in this backlog, "the relay" is a new deployable component, not just a host-side plugin. It needs its own minimal service (tentatively `Agnes.Relay`) that does *only* connection brokering: accept host connections, accept client connections, pair them by a routable host-id, and forward bytes. It should be designed to need to be trusted with as little as possible — ideally never with plaintext at all (see the secure-channel doc referenced above). Scope v1 to a single self-hostable relay binary, packaged the same way the rest of Agnes is (a Docker image), with no multi-region or high-availability story yet — that kind of infrastructure investment is premature for a project at this stage and should be revisited only once real usage demands it.

### How it plugs into the host

`Agnes.Host`'s startup picks a configured `ITransportProvider` (default: `Direct`, preserving today's behavior with zero config changes for existing deployments) and calls `ExposeAsync` instead of binding Kestrel directly. The returned `TransportEndpoint` is what gets shown in the pairing-code/QR flow (see `04-device-linking-and-restore.md`) so the client always gets the *right* address to dial regardless of which transport is active — the pairing UI doesn't need to know or care whether it's showing a direct LAN address, a relay-assigned URL, or a tailnet hostname.

## Acceptance criteria

- **AC1** — A host configured with the default `Direct` transport behaves identically to today's Agnes: no new configuration is required, and no existing deployment breaks.
- **AC2** — A host configured with `AgnesRelayTransportProvider`, running behind NAT with no inbound ports open, is successfully reachable by a client on a different network (verified with the host on a network with no port-forwarding and the client on a separate network, e.g. a mobile hotspot).
- **AC3** — A host configured with `TailscaleTransportProvider` is reachable only over the user's tailnet, with no listener exposed to the public internet — verified by confirming a port scan of the host's public IP shows no open Agnes port when this transport is active.
- **AC4** — Switching a running host's transport (e.g. `Direct` → relay) does not require re-pairing already-paired devices; the pairing/auth token model is transport-independent.
- **AC5** — When a relay is configured, the pairing/QR flow shows the relay-provided address, not a LAN-local address that would be unreachable from outside — this specifically prevents the class of bug where a client is handed an address only meaningful on the host's own network.
- **AC6** — If a relay is unreachable or misconfigured, the host fails to expose that transport with a clear, actionable error at startup (not a silent fallback to an insecure or unintended transport).

## Open questions

- Does Agnes want to run a public, Agnes-operated relay at all, or ship *only* the self-hostable relay binary plus the Tailscale option? A small, young project can reasonably scope v1 to self-host-only and treat an Agnes-operated hosted relay as a later, separate decision — one with real infrastructure, operations, and cost implications, not just code.
- Relay protocol: plain byte-forwarding (the relay never parses `Agnes.Protocol` at all) vs. relay-aware forwarding (letting the relay do presence/routing logic). Byte-forwarding is simpler, has less attack surface, and maximizes the "the relay never needs to understand your data" security story — it should be the default assumption unless a concrete feature needs otherwise.
- Should workspace/session transfer between hosts (see `03-session-handoff.md`) always go through the relay, or should Agnes support a direct host-to-host path when both hosts happen to be reachable from each other (same LAN/overlay network) for lower latency? Worth keeping both as distinct options rather than assuming relay-routed-always, but relay-routed-only is a reasonable, simpler starting point.
