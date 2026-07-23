using System.Text.Json.Serialization;

namespace Agnes.Relay.Protocol;

/// <summary>
/// The relay's tiny, self-contained wire contract used <b>only</b> for connection setup
/// (register / route / ban). It is deliberately separate from <c>Agnes.Protocol</c>: once a
/// client and host are spliced, the relay copies opaque bytes and never speaks these frames
/// on the payload path again. Frames are length-prefixed JSON (see <see cref="RelayFrameCodec"/>).
///
/// Immutable records, discriminated by the polymorphic <c>"t"</c> tag so a reader can decode a
/// frame without knowing its type in advance (the first frame on a connection declares intent).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HostHelloFrame), "host_hello")]
[JsonDerivedType(typeof(ChallengeFrame), "challenge")]
[JsonDerivedType(typeof(HostRegisterFrame), "host_register")]
[JsonDerivedType(typeof(RegisterAckFrame), "register_ack")]
[JsonDerivedType(typeof(ClientWaitingFrame), "client_waiting")]
[JsonDerivedType(typeof(BanSourceFrame), "ban_source")]
[JsonDerivedType(typeof(ClientRouteFrame), "client_route")]
[JsonDerivedType(typeof(RouteAckFrame), "route_ack")]
[JsonDerivedType(typeof(HostDataFrame), "host_data")]
public abstract record RelayFrame;

/// <summary>Host → relay, first frame of a control connection: "I want to register."</summary>
public sealed record HostHelloFrame(int ProtocolVersion) : RelayFrame;

/// <summary>Relay → host: a single-use challenge nonce (base64) for the host to sign.</summary>
public sealed record ChallengeFrame(string Nonce) : RelayFrame;

/// <summary>
/// Host → relay: claim <paramref name="HostId"/> by proving possession of a per-host key.
/// <paramref name="PublicKey"/> is base64 SPKI (P-256); <paramref name="Signature"/> is a base64
/// ECDSA/SHA-256 DER signature over <c>UTF8(Nonce + "\n" + HostId)</c> — binding the key to the
/// exact host-id it is claiming, so a captured signature can't be replayed onto another id.
/// </summary>
public sealed record HostRegisterFrame(string HostId, string PublicKey, string Signature) : RelayFrame;

/// <summary>Relay → host: outcome of a registration attempt.</summary>
public sealed record RegisterAckFrame(bool Ok, string? Reason) : RelayFrame;

/// <summary>
/// Relay → host (over the control connection): a client is waiting for this host. The host must
/// open a NEW outbound data connection and present <paramref name="Token"/>. <paramref name="SourceIp"/>
/// is provided so the host — the auth authority — can decide to ban an abusive source.
/// </summary>
public sealed record ClientWaitingFrame(string Token, string SourceIp) : RelayFrame;

/// <summary>
/// Host → relay (over the control connection): drop / ban this source IP. The relay enforces it
/// blindly — the host never has to reveal <i>why</i>. Mirrors the host-side auth-authority model.
/// </summary>
public sealed record BanSourceFrame(string SourceIp) : RelayFrame;

/// <summary>
/// Client → relay, first (and only) relay-protocol frame a client ever sends: the target host-id
/// and nothing else. Everything after the resulting <see cref="RouteAckFrame"/> is opaque bytes
/// (the client↔host TLS handshake and all app traffic) that the relay forwards without inspection.
/// </summary>
public sealed record ClientRouteFrame(string HostId) : RelayFrame;

/// <summary>Relay → client: whether the target host is reachable. On success, opaque forwarding begins.</summary>
public sealed record RouteAckFrame(bool Ok, string? Reason) : RelayFrame;

/// <summary>
/// Host → relay, first frame of a data connection opened in response to a <see cref="ClientWaitingFrame"/>.
/// Presents the <paramref name="Token"/> the relay just handed out; the relay splices this connection to
/// the matching waiting client and blind-forwards both directions.
/// </summary>
public sealed record HostDataFrame(string Token) : RelayFrame;
