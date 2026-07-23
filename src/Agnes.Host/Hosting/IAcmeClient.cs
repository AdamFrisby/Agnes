namespace Agnes.Host.Hosting;

/// <summary>The DNS-01 challenge for an ACME order: the exact TXT record to publish so the CA validates control.</summary>
public sealed record Dns01Challenge(string RecordName, string RecordValue);

/// <summary>
/// A single in-flight ACME order, exposed as discrete steps so the DNS-01 flow is driven — and tested — one
/// stage at a time: read the <see cref="Challenge"/>, publish it via an <see cref="IDnsChallengeProvider"/>,
/// then <see cref="SubmitAndWaitForValidationAsync"/> and <see cref="DownloadPfxAsync"/>. This is the seam a
/// FAKE ACME client stands in for offline (the real Let's Encrypt issuance is exercised only with a real domain).
/// </summary>
public interface IAcmeOrder
{
    /// <summary>The DNS-01 challenge to satisfy (the TXT record name + the CA-computed digest value).</summary>
    Dns01Challenge Challenge { get; }

    /// <summary>Tells the CA the TXT is published and blocks until the authorization reaches <c>Valid</c>
    /// (or throws with a clear reason on failure/timeout).</summary>
    Task SubmitAndWaitForValidationAsync(CancellationToken ct = default);

    /// <summary>Finalizes the order and returns the issued certificate + its private key as PKCS#12 bytes.</summary>
    Task<byte[]> DownloadPfxAsync(string friendlyName, CancellationToken ct = default);
}

/// <summary>
/// The ACME client seam Agnes codes against for the DNS-01 host-certificate flow. The real implementation
/// (<see cref="CertesAcmeClient"/>) wraps the Certes library; tests substitute a fake. Keeping this abstraction
/// means <see cref="AcmeDns01HostCertificateProvider"/> — and its whole order/validate/download flow — is
/// exercised offline with no network, no real CA, and no real DNS.
/// </summary>
public interface IAcmeClient
{
    /// <summary>Places a new order for <paramref name="hostName"/> and returns its DNS-01 challenge + handle.</summary>
    Task<IAcmeOrder> BeginOrderAsync(string hostName, CancellationToken ct = default);
}
