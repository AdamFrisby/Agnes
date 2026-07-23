using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agnes.Relay.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Agnes.Client;

/// <summary>A parsed <c>agnes-relay://host:port/hostId?fp=&lt;sha256hex&gt;</c> address handed to a client at pairing.</summary>
public sealed record RelayClientAddress(string RelayHost, int RelayPort, string HostId, string Fingerprint);

/// <summary>
/// Client half of the relay tunnel (spec AC2). Given an <c>agnes-relay://</c> address, it dials the relay,
/// routes to the target host-id, then hands SignalR a transport that runs the client↔host TLS <b>end-to-end</b>
/// over the spliced stream — pinning the host's advertised self-signed cert fingerprint (from pairing) instead
/// of validating a CA chain. The per-device bearer token is unchanged and flows inside the tunnel (AC4): the
/// relay only ever forwards already-encrypted bytes and never sees the token or any payload.
/// <para>
/// SignalR is forced onto its long-polling transport so every physical connection goes through our
/// <see cref="SocketsHttpHandler.ConnectCallback"/> (the WebSocket transport would open its own socket and bypass
/// the tunnel). A WebSocket-over-tunnel transport is a possible later optimization.
/// </para>
/// </summary>
public static class RelayClientTransport
{
    /// <summary>The URL scheme a relay address uses.</summary>
    public const string Scheme = "agnes-relay";

    /// <summary>True if <paramref name="address"/> is an <c>agnes-relay://</c> address rather than a direct URL.</summary>
    public static bool IsRelayAddress(string address)
        => address.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses an <c>agnes-relay://host:port/hostId?fp=&lt;sha256hex&gt;</c> address.</summary>
    public static RelayClientAddress Parse(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Not an {Scheme}:// address: '{address}'.");
        }

        if (uri.Port <= 0)
        {
            throw new FormatException($"Relay address is missing a port: '{address}'.");
        }

        string hostId = uri.AbsolutePath.Trim('/');
        if (hostId.Length == 0)
        {
            throw new FormatException($"Relay address is missing a host-id: '{address}'.");
        }

        string fingerprint = ReadQueryValue(uri.Query, "fp");
        if (fingerprint.Length == 0)
        {
            throw new FormatException(
                $"Relay address is missing the pinned host-cert fingerprint (?fp=): '{address}'.");
        }

        return new RelayClientAddress(uri.Host, uri.Port, hostId, NormalizeFingerprint(fingerprint));
    }

    /// <summary>
    /// Builds the SignalR base URL and <see cref="HttpConnectionOptions"/> configuration for a relay address.
    /// The returned base URL is synthetic (routing happens in the connect callback); the pinned fingerprint is
    /// enforced during the TLS handshake to Kestrel.
    /// </summary>
    public static (string BaseUrl, Action<HttpConnectionOptions> Configure) Build(string address)
    {
        RelayClientAddress relay = Parse(address);
        // Synthetic host: the connect callback ignores it (bytes route via the relay), and cert validation pins
        // the fingerprint rather than the name. `.invalid` is reserved and never resolves — no accidental DNS.
        const string baseUrl = "https://agnes-relay.invalid";

        void Configure(HttpConnectionOptions options)
        {
            // Force long-polling so every physical connection flows through ConnectCallback (WebSockets would
            // open their own socket and bypass the tunnel).
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => CreateHandler(relay);
        }

        return (baseUrl, Configure);
    }

    /// <summary>The <see cref="SocketsHttpHandler"/> that tunnels through the relay and pins the host cert.</summary>
    private static SocketsHttpHandler CreateHandler(RelayClientAddress relay)
    {
        var handler = new SocketsHttpHandler
        {
            // Each HTTP connection: dial the relay, route to the host-id, then hand back the opaque spliced
            // stream. SocketsHttpHandler wraps it in TLS itself (below), so the handshake is end-to-end to Kestrel.
            ConnectCallback = async (_, ct) => await OpenTunnelAsync(relay, ct).ConfigureAwait(false),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "agnes-host",
                // Pin the host's advertised self-signed cert by fingerprint instead of a CA chain.
                RemoteCertificateValidationCallback = (_, cert, _, _) => MatchesPin(cert, relay.Fingerprint),
            },
        };
        return handler;
    }

    private static async Task<Stream> OpenTunnelAsync(RelayClientAddress relay, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(relay.RelayHost, relay.RelayPort, ct).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);

            await RelayFrameCodec.WriteFrameAsync(stream, new ClientRouteFrame(relay.HostId), ct).ConfigureAwait(false);
            RelayFrame? ack = await RelayFrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (ack is not RouteAckFrame { Ok: true })
            {
                string reason = (ack as RouteAckFrame)?.Reason ?? "no route";
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new IOException($"The relay could not route to host-id '{relay.HostId}': {reason}.");
            }

            // Opaque from here: SocketsHttpHandler runs the end-to-end TLS handshake over this stream.
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool MatchesPin(X509Certificate? certificate, string expectedFingerprint)
    {
        if (certificate is null)
        {
            return false;
        }

        using var cert = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        string actual = Convert.ToHexStringLower(cert.GetCertHash(HashAlgorithmName.SHA256));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expectedFingerprint));
    }

    /// <summary>Reads a single query-string value by key from a <c>?a=b&amp;c=d</c> query (leading '?' optional).</summary>
    private static string ReadQueryValue(string query, string key)
    {
        string trimmed = query.TrimStart('?');
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string name = eq < 0 ? pair : pair[..eq];
            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return string.Empty;
    }

    private static string NormalizeFingerprint(string fingerprint)
        => fingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
}
