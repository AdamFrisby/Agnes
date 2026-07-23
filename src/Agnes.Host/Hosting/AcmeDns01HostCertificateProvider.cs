using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Configuration for <see cref="AcmeDns01HostCertificateProvider"/>.</summary>
public sealed record AcmeHostCertificateOptions
{
    /// <summary>The host's public relay hostname the certificate is issued for (its SAN + CA-validated name).</summary>
    public required string HostName { get; init; }

    /// <summary>Where the issued certificate (PKCS#12) is persisted so a restart reuses it until renewal.</summary>
    public required string PfxPath { get; init; }

    /// <summary>Renew this far before expiry (Let's Encrypt certs live 90 days; default renews at 30 remaining).</summary>
    public TimeSpan RenewBefore { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// A real-CA <see cref="IHostCertificateProvider"/>: obtains and auto-renews a Let's Encrypt certificate for the
/// host's relay hostname via the <b>DNS-01</b> challenge — the only ACME challenge a NAT'd host can answer, since
/// it needs no inbound port (HTTP-01/TLS-ALPN-01 both require an inbound listener the host doesn't have). The
/// order flow runs through the injected <see cref="IAcmeClient"/> (Certes) and publishes the challenge TXT through
/// the injected <see cref="IDnsChallengeProvider"/> (DuckDNS or a BYO domain). The issued cert is persisted and
/// renewed before expiry by <see cref="HostCertificateRenewalService"/>.
/// <para>
/// Because this cert chains to a public CA and matches <see cref="HostName"/>, the client validates the chain +
/// name (<see cref="CaValidatedHostName"/> is non-null) instead of pinning a self-signed fingerprint — the clean
/// upgrade the <see cref="IHostCertificateProvider"/> seam was designed for.
/// </para>
/// </summary>
public sealed class AcmeDns01HostCertificateProvider : IHostCertificateProvider, IDisposable
{
    private readonly AcmeHostCertificateOptions _options;
    private readonly IAcmeClient _acme;
    private readonly IDnsChallengeProvider _dns;
    private readonly TimeProvider _time;
    private readonly ILogger<AcmeDns01HostCertificateProvider>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private X509Certificate2? _certificate;
    private string? _fingerprint;

    public AcmeDns01HostCertificateProvider(
        AcmeHostCertificateOptions options,
        IAcmeClient acme,
        IDnsChallengeProvider dns,
        TimeProvider? timeProvider = null,
        ILogger<AcmeDns01HostCertificateProvider>? logger = null)
    {
        _options = options;
        _acme = acme;
        _dns = dns;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? CaValidatedHostName => _options.HostName;

    /// <inheritdoc />
    public string Fingerprint => _fingerprint
        ?? throw new InvalidOperationException("The ACME host certificate has not been acquired yet — call EnsureReadyAsync first.");

    /// <inheritdoc />
    public X509Certificate2 GetCertificate() => _certificate
        ?? throw new InvalidOperationException("The ACME host certificate has not been acquired yet — call EnsureReadyAsync first.");

    /// <inheritdoc />
    public Task EnsureReadyAsync(CancellationToken ct = default) => AcquireIfNeededAsync(ct);

    /// <summary>Acquires the cert if none is loaded or the current one is within its renewal window.</summary>
    public async Task AcquireIfNeededAsync(CancellationToken ct = default)
    {
        if (_certificate is not null && !IsWithinRenewalWindow(_certificate))
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_certificate is not null && !IsWithinRenewalWindow(_certificate))
            {
                return;
            }

            X509Certificate2? persisted = TryLoadPersisted();
            if (persisted is not null && !IsWithinRenewalWindow(persisted))
            {
                Adopt(persisted);
                return;
            }

            X509Certificate2 fresh = await OrderAsync(ct).ConfigureAwait(false);
            persisted?.Dispose();
            Adopt(fresh);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsWithinRenewalWindow(X509Certificate2 certificate)
        => _time.GetUtcNow() >= certificate.NotAfter.ToUniversalTime() - _options.RenewBefore;

    private X509Certificate2? TryLoadPersisted()
    {
        if (!File.Exists(_options.PfxPath))
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(_options.PfxPath), string.Empty, X509KeyStorageFlags.Exportable);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger?.LogWarning(ex, "Persisted ACME certificate at {Path} could not be loaded — reordering.", _options.PfxPath);
            return null;
        }
    }

    private async Task<X509Certificate2> OrderAsync(CancellationToken ct)
    {
        _logger?.LogInformation("Ordering a Let's Encrypt certificate for '{Host}' via DNS-01.", _options.HostName);
        IAcmeOrder order = await _acme.BeginOrderAsync(_options.HostName, ct).ConfigureAwait(false);

        await _dns.AddTxtRecordAsync(order.Challenge.RecordName, order.Challenge.RecordValue, ct).ConfigureAwait(false);
        try
        {
            await order.SubmitAndWaitForValidationAsync(ct).ConfigureAwait(false);
            byte[] pfx = await order.DownloadPfxAsync(_options.HostName, ct).ConfigureAwait(false);
            Persist(pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, string.Empty, X509KeyStorageFlags.Exportable);
        }
        finally
        {
            try
            {
                await _dns.RemoveTxtRecordAsync(order.Challenge.RecordName, order.Challenge.RecordValue, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                // Best-effort cleanup: a leftover challenge TXT is harmless and must not fail a valid issuance.
                _logger?.LogWarning(ex, "Could not clean up the ACME challenge TXT for '{Record}'.", order.Challenge.RecordName);
            }
        }
    }

    private void Persist(byte[] pfx)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_options.PfxPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(_options.PfxPath, pfx);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not persist the ACME certificate to {Path}; using it in-memory this run.", _options.PfxPath);
        }
    }

    private void Adopt(X509Certificate2 certificate)
    {
        X509Certificate2? previous = _certificate;
        _fingerprint = SelfSignedHostCertificateProvider.ComputeFingerprint(certificate);
        _certificate = certificate;
        previous?.Dispose();
        _logger?.LogInformation(
            "ACME host certificate for '{Host}' ready (expires {Expiry:u}, SHA-256 {Fingerprint}).",
            _options.HostName, certificate.NotAfter.ToUniversalTime(), _fingerprint);
    }

    public void Dispose()
    {
        _certificate?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// Renews the ACME host certificate before it expires. Only registered when the ACME provider is active; it
/// polls at a modest cadence and lets <see cref="AcmeDns01HostCertificateProvider.AcquireIfNeededAsync"/> decide
/// whether a renewal is actually due (the check is a cheap expiry comparison until inside the renewal window).
/// </summary>
public sealed class HostCertificateRenewalService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly AcmeDns01HostCertificateProvider _provider;
    private readonly TimeProvider _time;
    private readonly ILogger<HostCertificateRenewalService>? _logger;

    public HostCertificateRenewalService(
        AcmeDns01HostCertificateProvider provider,
        TimeProvider timeProvider,
        ILogger<HostCertificateRenewalService>? logger = null)
    {
        _provider = provider;
        _time = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, _time, stoppingToken).ConfigureAwait(false);
                await _provider.AcquireIfNeededAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
            {
                // A failed renewal attempt must not crash the host — the existing cert keeps serving; retry next tick.
                _logger?.LogWarning(ex, "ACME certificate renewal attempt failed; will retry.");
            }
        }
    }
}
