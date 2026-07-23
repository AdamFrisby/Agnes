namespace Agnes.Host.Hosting;

/// <summary>
/// Sets (and clears) the <c>_acme-challenge</c> TXT record an ACME server reads to satisfy a DNS-01
/// challenge. This is the pluggable seam a NAT'd host uses to prove domain control WITHOUT any inbound
/// port (unlike HTTP-01/TLS-ALPN-01, which need an inbound listener the host doesn't have). Two shapes
/// ship: a DynDNS provider (<see cref="DuckDnsChallengeProvider"/>, the canonical home/NAT choice) and a
/// bring-your-own-domain generic provider (<see cref="GenericDnsChallengeProvider"/>).
/// </summary>
public interface IDnsChallengeProvider
{
    /// <summary>
    /// Publishes a TXT record so the ACME server can validate the challenge. <paramref name="recordName"/>
    /// is the fully-qualified record name (e.g. <c>_acme-challenge.host.example.com</c>);
    /// <paramref name="value"/> is the base64url challenge digest the ACME client computed.
    /// </summary>
    Task AddTxtRecordAsync(string recordName, string value, CancellationToken ct = default);

    /// <summary>Removes the TXT record after validation. Best-effort — failures must not fail issuance.</summary>
    Task RemoveTxtRecordAsync(string recordName, string value, CancellationToken ct = default);
}
