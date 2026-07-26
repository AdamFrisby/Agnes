using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Agnes.Client;

/// <summary>
/// Certificate pinning for a self-signed Agnes host: trust exactly the certificate whose SHA-256 we were
/// given at pairing, and nothing else.
///
/// This is what lets a self-hosted host work with no CA, no installed certificate and no cleartext. The
/// pin arrives out of band — printed in the pairing QR on the host's own screen — so even the first
/// connection is verified rather than trusted on faith, which is stronger than plain trust-on-first-use.
/// It deliberately bypasses the OS trust store, and on Android that also puts it outside
/// <c>network_security_config</c>: .NET's managed TLS stack does the check, not the platform's.
///
/// Originally the relay transport's private business (<see cref="RelayClientTransport"/>), which needed it
/// because the relay is a blind byte-mover and TLS has to terminate end-to-end at the host. The direct
/// path needs exactly the same trust decision for exactly the same reason, so it lives here and both use
/// it — one implementation, one place to get the comparison right.
/// </summary>
public static class PinnedTls
{
    /// <summary>
    /// The certificate name used when pinning. The host's certificate is self-signed and validated by
    /// fingerprint rather than by name, so this is a placeholder that keeps the handshake well-formed —
    /// it is never checked against the address dialled.
    /// </summary>
    public const string SyntheticHostName = "agnes-host";

    /// <summary>
    /// Whether a presented certificate is the pinned one. Compared in constant time: the pin is public, but
    /// a timing-variable comparison over attacker-influenced input is a habit not worth having.
    /// </summary>
    public static bool Matches(X509Certificate? certificate, string expectedFingerprint)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            return false;
        }

        // Dispose only a certificate we created. The one handed to a TLS callback belongs to the SSL
        // stack, and disposing it invalidates the handle underneath its owner — which then throws
        // "m_safeCertContext is an invalid handle" the next time anyone touches it.
        string actual;
        if (certificate is X509Certificate2 already)
        {
            actual = Convert.ToHexStringLower(already.GetCertHash(HashAlgorithmName.SHA256));
        }
        else
        {
            using var loaded = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            actual = Convert.ToHexStringLower(loaded.GetCertHash(HashAlgorithmName.SHA256));
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expectedFingerprint.Trim().ToLowerInvariant()));
    }

    /// <summary>TLS options that accept only the pinned certificate.</summary>
    public static SslClientAuthenticationOptions Options(string pin, string? targetHost = null)
        => new()
        {
            TargetHost = targetHost ?? SyntheticHostName,
            RemoteCertificateValidationCallback = (_, cert, _, _) => Matches(cert, pin),
        };

    /// <summary>
    /// Applies the pin to the WebSocket the SignalR WebSockets transport opens. It dials its own socket and
    /// never goes through the message handler, so without this the negotiate would be pinned and the
    /// connection that carries every session event would not be.
    /// </summary>
    public static void Apply(ClientWebSocketOptions options, string pin)
        => options.RemoteCertificateValidationCallback = (_, cert, _, _) => Matches(cert, pin);

    /// <summary>
    /// A handler that trusts only the pinned certificate.
    ///
    /// A pinned connection must use one of these rather than a handler the platform supplied: on Android
    /// <c>UseNativeHttpHandler</c> defaults to true, so the default handler is the platform's and its TLS
    /// is decided by the OS trust store and <c>network_security_config</c> — where a self-signed host is
    /// rejected whatever callback we attach. Managed TLS is what makes the pin the thing that decides, on
    /// every platform. (It costs the cookie/proxy/credential settings SignalR would have applied; Agnes
    /// sets none of them, since the device token rides in the query string.)
    ///
    /// Unlike <see cref="Options"/> it leaves <c>TargetHost</c> alone so SNI follows the address actually
    /// dialled — the synthetic name exists for the relay tunnel, which has no real host name to send.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(string pin)
        => new()
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => Matches(cert, pin),
            },
        };

    /// <summary>
    /// An <see cref="HttpClient"/> pinned to one certificate, for the REST half of pairing. The scan-to-pair
    /// flow posts to <c>/pair</c> over the same self-signed HTTPS the hub will use, so without this the
    /// pairing call fails before the pin ever gets a chance to help.
    /// </summary>
    public static HttpClient CreateClient(string pin) => new(CreateHandler(pin));
}
