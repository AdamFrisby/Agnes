using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Host config for mTLS client-certificate auth (bound from <c>Agnes:Auth:Mtls</c>).</summary>
/// <remarks>
/// This authenticates a <em>user/device</em> as a login decision (does this certificate prove an allowed
/// identity?), distinct from any transport-level mTLS tunnel that merely establishes confidentiality. A
/// certificate is accepted if it either chains to a configured trust anchor (an internal-PKI CA) or
/// matches a pinned SHA-256 thumbprint; nothing else is accepted.
/// </remarks>
public sealed record MtlsOptions
{
    public bool Enabled { get; init; }

    /// <summary>PEM containing one or more trust-anchor (CA) certificates the client certificate must chain
    /// to. Any of them terminating the chain is sufficient.</summary>
    public string? TrustAnchorPem { get; init; }

    /// <summary>Explicit allowlist of pinned leaf-certificate SHA-256 thumbprints (hex, with or without
    /// separators/case). A presented certificate whose SHA-256 thumbprint matches is accepted directly,
    /// without any chain building — for pinning individual devices without a CA.</summary>
    public string[] PinnedThumbprints { get; init; } = [];

    /// <summary>Human-friendly label shown to clients.</summary>
    public string DisplayName { get; init; } = "Client certificate";

    /// <summary>Enabled and configured with at least one way to establish trust (a CA anchor or a pin) —
    /// fail-closed: an enabled but empty configuration is unusable rather than accepting any certificate.</summary>
    public bool IsUsable => Enabled
        && (!string.IsNullOrWhiteSpace(TrustAnchorPem) || PinnedThumbprints.Length > 0);
}

/// <summary>Outcome of validating a client certificate: accepted (with a subject) or rejected (reason).</summary>
public sealed record MtlsResult(bool Ok, string? Subject, string? Reason)
{
    public static MtlsResult Reject(string reason) => new(false, null, reason);
    public static MtlsResult Accept(string subject) => new(true, subject, null);
}

/// <summary>
/// Validates an incoming client certificate against the configured trust anchor / pin allowlist as the sole
/// proof of identity. Pure over its inputs — no ambient state, no network (revocation checking is disabled;
/// trust is decided entirely by the operator-supplied anchors/pins). A valid certificate yields a subject
/// (its subject/common name) to record on the minted device token.
/// </summary>
public sealed class MtlsIdentity(MtlsOptions options, ILogger<MtlsIdentity>? logger = null)
{
    private readonly string[] _pins = NormalizePins(options.PinnedThumbprints);

    public MtlsOptions Options { get; } = options;

    public MtlsResult Validate(X509Certificate2? clientCertificate)
    {
        if (!Options.IsUsable)
        {
            return MtlsResult.Reject("Client-certificate sign-in is not configured on this host.");
        }

        if (clientCertificate is null)
        {
            return MtlsResult.Reject("No client certificate was presented.");
        }

        // 1) Pinned-thumbprint path — an exact leaf match needs no chain.
        if (_pins.Length > 0)
        {
            var thumbprint = clientCertificate.GetCertHashString(HashAlgorithmName.SHA256);
            if (_pins.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
            {
                return MtlsResult.Accept(SubjectOf(clientCertificate));
            }
        }

        // 2) Trust-anchor path — the certificate must chain to one of the configured CAs.
        if (!string.IsNullOrWhiteSpace(Options.TrustAnchorPem) && ChainsToConfiguredAnchor(clientCertificate))
        {
            return MtlsResult.Accept(SubjectOf(clientCertificate));
        }

        logger?.LogWarning("mTLS: certificate '{Subject}' is neither pinned nor chains to a trusted anchor", clientCertificate.Subject);
        return MtlsResult.Reject("The client certificate is not trusted by this host.");
    }

    private bool ChainsToConfiguredAnchor(X509Certificate2 clientCertificate)
    {
        X509Certificate2Collection anchors = [];
        try
        {
            anchors.ImportFromPem(Options.TrustAnchorPem);
        }
        catch (CryptographicException ex)
        {
            // A malformed anchor PEM is a misconfiguration; fail closed rather than trust nothing-as-anything.
            logger?.LogWarning(ex, "mTLS: the configured trust-anchor PEM could not be parsed");
            return false;
        }

        if (anchors.Count == 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // trust is anchor-based, not CRL/OCSP
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(anchors);
        chain.ChainPolicy.ExtraStore.AddRange(anchors);

        try
        {
            return chain.Build(clientCertificate);
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                element.Certificate.Dispose();
            }
        }
    }

    private static string SubjectOf(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? certificate.Subject : name;
    }

    private static string[] NormalizePins(string[] pins) =>
        pins.Select(p => p.Replace(":", string.Empty, StringComparison.Ordinal)
                          .Replace(" ", string.Empty, StringComparison.Ordinal)
                          .Trim())
            .Where(p => p.Length > 0)
            .ToArray();
}

/// <summary>Built-in: mTLS client-certificate sign-in, backed by <see cref="MtlsIdentity"/>.</summary>
public sealed class MtlsAuthMethodProvider(MtlsIdentity mtls) : IAuthMethodProvider
{
    public string MethodId => "mtls";
    public string DisplayName => string.IsNullOrWhiteSpace(mtls.Options.DisplayName) ? "Client certificate" : mtls.Options.DisplayName;
    public bool IsEnabled => mtls.Options.IsUsable;
    public IReadOnlyDictionary<string, string> ClientMetadata => new Dictionary<string, string>();
}
