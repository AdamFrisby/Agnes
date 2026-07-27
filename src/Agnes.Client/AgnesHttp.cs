using System.Collections.Concurrent;
using System.Net.Http;

namespace Agnes.Client;

/// <summary>
/// The one place that answers "what <see cref="HttpClient"/> do I talk to this host with".
///
/// Agnes hosts are routinely self-signed and authenticated by a pinned certificate fingerprint rather than by
/// the OS trust store (see <see cref="PinnedTls"/>). The hub connection has always honoured that pin, but the
/// REST management calls behind the settings surface each built a bare <see cref="HttpClient"/>, whose default
/// validation rejects exactly the certificates Agnes is designed to work with — so sessions connected while
/// every device, MCP, project and sandbox call failed the handshake. Routing every one of them through here is
/// what stops that being a mistake anybody can make again.
///
/// Two details matter for correctness rather than tidiness:
///
/// The <em>handler</em> is pooled per pin and the <see cref="HttpClient"/> around it is not. Handlers own the
/// connection pool, so a fresh one per call exhausts sockets; clients are thin wrappers, and the management
/// helpers set an <c>Authorization</c> header on the client they're given. Sharing one client across hosts
/// would mean one call's bearer token could be sent with another's request — so each caller gets its own
/// wrapper over the shared plumbing, and the header stays private to that call.
///
/// A pinned handler deliberately bypasses the platform's TLS stack; see <see cref="PinnedTls.CreateHandler"/>
/// for why that is required rather than merely convenient on Android.
/// </summary>
public static class AgnesHttp
{
    private static readonly ConcurrentDictionary<string, SocketsHttpHandler> PinnedHandlers = new(StringComparer.Ordinal);
    private static readonly SocketsHttpHandler DefaultHandler = new();

    /// <summary>
    /// A client for a host authenticated by <paramref name="pinnedFingerprint"/>, or an ordinary one when
    /// there is no pin (a real-CA host, or plain http). Cheap enough to call per request.
    /// </summary>
    public static HttpClient For(string? pinnedFingerprint)
        => new(HandlerFor(pinnedFingerprint), disposeHandler: false);

    /// <summary>
    /// The pooled handler for a pin, exposed for the few callers that need to configure the client themselves
    /// (a base address, a timeout) while still reusing the connection pool.
    /// </summary>
    public static SocketsHttpHandler HandlerFor(string? pinnedFingerprint)
        => string.IsNullOrWhiteSpace(pinnedFingerprint)
            ? DefaultHandler
            : PinnedHandlers.GetOrAdd(pinnedFingerprint.Trim().ToLowerInvariant(), PinnedTls.CreateHandler);
}
