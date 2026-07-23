using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// Supplies the TLS certificate the host's Kestrel listener presents on the relay path, plus the
/// fingerprint clients pin to it. On the relay path TLS terminates at Kestrel end-to-end — the relay
/// and the host's own loopback pump are blind byte-movers — so the client can't rely on a public CA
/// chain and instead pins <b>this</b> certificate's fingerprint (advertised at pairing time).
/// <para>
/// This wave ships one implementation, <see cref="SelfSignedHostCertificateProvider"/> (a persistent
/// self-signed P-256 cert). The interface is deliberately small so a later wave can add a real-CA
/// implementation (e.g. Let's Encrypt via DNS-01) without touching the transport or the client.
/// </para>
/// </summary>
public interface IHostCertificateProvider
{
    /// <summary>The certificate (with its private key) Kestrel presents on the relay path.</summary>
    X509Certificate2 GetCertificate();

    /// <summary>Lower-case hex SHA-256 of the certificate's DER encoding — the value a client pins.</summary>
    string Fingerprint { get; }

    /// <summary>
    /// When this provider supplies a certificate issued by a public CA (e.g. Let's Encrypt via DNS-01),
    /// the DNS name the client should validate the CA chain against — the host's relay hostname. For a
    /// self-signed provider this is <c>null</c>, signalling the client to pin <see cref="Fingerprint"/>
    /// instead of validating a chain. Default: <c>null</c> (pin), so existing providers are unaffected.
    /// </summary>
    string? CaValidatedHostName => null;

    /// <summary>
    /// Ensures a usable certificate is available (acquiring/renewing it if necessary) before the listener
    /// starts. A self-signed provider generates lazily and needs nothing here; an ACME provider performs its
    /// DNS-01 order. Default: nothing to do.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Default <see cref="IHostCertificateProvider"/>: a persistent self-signed P-256 certificate generated
/// once and reused across restarts (so the pinned fingerprint a client learned at pairing stays valid).
/// All crypto is BCL (<see cref="ECDsa"/> + <see cref="CertificateRequest"/>) — never hand-rolled.
/// </summary>
public sealed class SelfSignedHostCertificateProvider : IHostCertificateProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly string _pfxPath;
    private readonly string _subject;
    private readonly ILogger<SelfSignedHostCertificateProvider>? _logger;
    private X509Certificate2? _certificate;
    private string? _fingerprint;

    /// <param name="pfxPath">Where the generated cert (PKCS#12) is persisted so it survives restarts.</param>
    /// <param name="subject">Certificate subject/SAN DNS name. The client pins by fingerprint, so this name
    /// is not validated against a hostname — it exists only to make a well-formed certificate.</param>
    public SelfSignedHostCertificateProvider(
        string pfxPath,
        string subject = "agnes-host",
        ILogger<SelfSignedHostCertificateProvider>? logger = null)
    {
        _pfxPath = pfxPath;
        _subject = subject;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Fingerprint
    {
        get
        {
            EnsureLoaded();
            return _fingerprint!;
        }
    }

    /// <inheritdoc />
    public X509Certificate2 GetCertificate()
    {
        EnsureLoaded();
        return _certificate!;
    }

    /// <summary>Computes the lower-case hex SHA-256 of a certificate's DER bytes — the pin value.</summary>
    public static string ComputeFingerprint(X509Certificate2 certificate)
        => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private void EnsureLoaded()
    {
        if (_certificate is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_certificate is not null)
            {
                return;
            }

            _certificate = LoadOrCreate();
            _fingerprint = ComputeFingerprint(_certificate);
            _logger?.LogInformation(
                "Relay host certificate ready (pin this SHA-256 fingerprint on clients): {Fingerprint}", _fingerprint);
        }
    }

    private X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(_pfxPath))
        {
            try
            {
                // Re-import so the private key is usable for the TLS handshake on every platform.
                return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(_pfxPath), null,
                    X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException ex)
            {
                _logger?.LogWarning(ex, "Existing relay host certificate at {Path} could not be loaded — regenerating.", _pfxPath);
            }
        }

        X509Certificate2 created = Create(_subject);
        try
        {
            string? dir = Path.GetDirectoryName(_pfxPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(_pfxPath, created.Export(X509ContentType.Pkcs12));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not persist the relay host certificate to {Path}; using an in-memory cert this run.", _pfxPath);
        }

        return created;
    }

    private static X509Certificate2 Create(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(subject);
        request.CertificateExtensions.Add(san.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Long-lived: the client pins the fingerprint, so rotation is a deliberate re-pair, not an expiry event.
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    public void Dispose() => _certificate?.Dispose();
}
