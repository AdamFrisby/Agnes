using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Agnes.Client;

/// <summary>
/// First contact with a host whose certificate we do not yet pin: look at what it presents, without
/// trusting it and without sending anything.
/// </summary>
/// <remarks>
/// <see cref="PinnedTls"/> answers "is this the certificate I was promised", which presumes the promise
/// already arrived — in the pairing QR, on the host's own screen. When it hasn't, a client cannot reach the
/// host at all: the handshake fails before any request is sent, so pairing cannot even be attempted and the
/// failure looks like the host being down rather than untrusted. That is not a hypothetical. It locked a
/// live deployment out of its own daemon, and the symptom ("host is unreachable", zero requests in the host
/// log) pointed away from the cause.
///
/// SSH solved this long ago and we copy it deliberately: show the fingerprint, make a human compare it with
/// what the server itself reports, and only then remember it. This class is the "show" half — it performs a
/// TLS handshake purely to observe the certificate, sends no bytes of its own, carries no credential, and
/// returns the SHA-256 the rest of Agnes pins on. The comparison and the remembering belong to the caller,
/// because the point of trust-on-first-use is that a person makes that decision, not a library.
/// </remarks>
public static class HostFingerprint
{
    /// <summary>
    /// The lower-case hex SHA-256 of the certificate <paramref name="hostUrl"/> currently presents.
    /// </summary>
    /// <remarks>
    /// The certificate is captured in the validation callback and the connection is dropped immediately.
    /// The callback returns <c>false</c>: we never complete the handshake, because completing it would be
    /// indistinguishable from trusting the certificate, and nothing here is entitled to make that call.
    /// </remarks>
    public static async Task<string> ProbeAsync(string hostUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostUrl);

        var uri = new Uri(hostUrl, UriKind.Absolute);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{hostUrl} is not https, so it presents no certificate to pin. A plain-http host needs no pin.");
        }

        string? captured = null;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, cancellationToken).ConfigureAwait(false);

        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            captured = certificate is null ? null : Fingerprint(certificate);
            return false; // observe only — never complete a handshake we have not been asked to trust
        });

        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = PinnedTls.SyntheticHostName },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException) when (captured is not null)
        {
            // Expected: our own callback refused. We have what we came for.
        }

        return captured
               ?? throw new InvalidOperationException($"{hostUrl} completed a handshake without presenting a certificate.");
    }

    /// <summary>The lower-case hex SHA-256 of a certificate, in the form every pin in Agnes is written.</summary>
    public static string Fingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (certificate is X509Certificate2 already)
        {
            return Convert.ToHexStringLower(already.GetCertHash(HashAlgorithmName.SHA256));
        }

        using var loaded = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        return Convert.ToHexStringLower(loaded.GetCertHash(HashAlgorithmName.SHA256));
    }

    /// <summary>
    /// Groups a fingerprint for reading aloud or comparing by eye — 64 hex characters compared as one run is
    /// how a mismatched pin gets waved through.
    /// </summary>
    public static string ForDisplay(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var clean = fingerprint.Trim().ToLowerInvariant();
        return string.Join(' ', Enumerable.Range(0, (clean.Length + 7) / 8)
            .Select(i => clean.Substring(i * 8, Math.Min(8, clean.Length - (i * 8)))));
    }
}
