using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agnes.Relay.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Agnes.Client;

/// <summary>
/// A parsed relay address handed to a client at pairing. Carries exactly one trust anchor: a self-signed
/// <see cref="Fingerprint"/> to pin (<c>?fp=</c>), or a real-CA <see cref="CaName"/> to validate the chain and
/// host name against (<c>?cn=</c>).
/// </summary>
public sealed record RelayClientAddress(string RelayHost, int RelayPort, string HostId, string? Fingerprint, string? CaName);

/// <summary>
/// Client half of the relay tunnel (spec AC2). Given an <c>agnes-relay://</c> address, it dials the relay,
/// routes to the target host-id, then hands SignalR a transport that runs the client↔host TLS <b>end-to-end</b>
/// over the spliced stream — validating the host's real-CA cert by chain+name when the address advertises one
/// (<c>?cn=</c>), or pinning the host's advertised self-signed fingerprint (<c>?fp=</c>) otherwise. The per-device
/// bearer token is unchanged and flows inside the tunnel (AC4): the
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

    /// <summary>Parses an <c>agnes-relay://host:port/hostId?fp=&lt;sha256hex&gt;</c> (pin) or
    /// <c>?cn=&lt;hostname&gt;</c> (CA-validated) address.</summary>
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
        string caName = ReadQueryValue(uri.Query, "cn");
        if (fingerprint.Length == 0 && caName.Length == 0)
        {
            throw new FormatException(
                $"Relay address must carry a pinned host-cert fingerprint (?fp=) or a CA-validated host name (?cn=): '{address}'.");
        }

        return new RelayClientAddress(
            uri.Host, uri.Port, hostId,
            fingerprint.Length > 0 ? NormalizeFingerprint(fingerprint) : null,
            caName.Length > 0 ? caName : null);
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

    /// <summary>The <see cref="SocketsHttpHandler"/> that tunnels through the relay and trusts the host cert.</summary>
    private static SocketsHttpHandler CreateHandler(RelayClientAddress relay)
    {
        var handler = new SocketsHttpHandler
        {
            // Each HTTP connection: dial the relay, route to the host-id, then hand back the opaque spliced
            // stream. SocketsHttpHandler wraps it in TLS itself (below), so the handshake is end-to-end to Kestrel.
            ConnectCallback = async (_, ct) => await OpenTunnelAsync(relay, ct).ConfigureAwait(false),
            SslOptions = BuildSslClientOptions(relay),
        };
        return handler;
    }

    /// <summary>
    /// Builds the end-to-end TLS options for the host handshake over the tunnel: for a real-CA host cert, default
    /// chain validation against the advertised host name (<see cref="RelayClientAddress.CaName"/>); for a
    /// self-signed host cert, pin the advertised fingerprint (<see cref="RelayClientAddress.Fingerprint"/>) with no
    /// chain check. Exposed for tests over the trust decision.
    /// </summary>
    public static SslClientAuthenticationOptions BuildSslClientOptions(RelayClientAddress relay)
    {
        if (relay.CaName is { Length: > 0 } caName)
        {
            // Real CA cert: validate the chain AND that the cert matches this name (default validation does both).
            return new SslClientAuthenticationOptions { TargetHost = caName };
        }

        string pin = relay.Fingerprint
            ?? throw new InvalidOperationException("Relay address has neither a CA host name nor a pinned fingerprint.");
        return new SslClientAuthenticationOptions
        {
            // Synthetic name: the cert is self-signed and validated by fingerprint, not by name.
            TargetHost = "agnes-host",
            RemoteCertificateValidationCallback = (_, cert, _, _) => MatchesPin(cert, pin),
        };
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
