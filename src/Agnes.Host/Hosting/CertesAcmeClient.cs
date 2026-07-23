using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Configuration for <see cref="CertesAcmeClient"/> (bound from <c>Agnes:Transport:Relay:Cert</c>).</summary>
public sealed record CertesAcmeClientOptions
{
    /// <summary>Contact email registered with the ACME account (Let's Encrypt requires one).</summary>
    public required string Email { get; init; }

    /// <summary>Where the ACME account key (PEM) is persisted so the same account is reused across restarts.</summary>
    public required string AccountKeyPemPath { get; init; }

    /// <summary>Use the Let's Encrypt STAGING directory (no rate limits, untrusted certs) — for testing a real domain.</summary>
    public bool UseStaging { get; init; }

    /// <summary>How long to wait for the DNS-01 authorization to become <c>Valid</c> after submitting.</summary>
    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How often to poll the authorization status while waiting.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The real <see cref="IAcmeClient"/>: a thin adapter over the Certes ACME library (a reputable, widely-used
/// .NET ACME client). Certes exposes the ACME order lifecycle as discrete, awaitable steps — place order, read
/// the DNS-01 challenge, validate, finalize/download — which is exactly what the DNS-01 host-cert flow needs and
/// why Certes (rather than LettuceEncrypt, whose DNS-01 support is coupled to its Kestrel-hosted renewal loop)
/// backs the host path. All key/CSR crypto is Certes' own BCL-backed implementation — nothing hand-rolled here.
/// <para>Exercised end-to-end only against a real domain + real Let's Encrypt; unit tests drive the flow through
/// the <see cref="IAcmeClient"/> seam with a fake.</para>
/// </summary>
public sealed class CertesAcmeClient : IAcmeClient
{
    private readonly CertesAcmeClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CertesAcmeClient>? _logger;

    public CertesAcmeClient(CertesAcmeClientOptions options, TimeProvider? timeProvider = null, ILogger<CertesAcmeClient>? logger = null)
    {
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAcmeOrder> BeginOrderAsync(string hostName, CancellationToken ct = default)
    {
        IAcmeContext acme = await CreateContextAsync(ct).ConfigureAwait(false);
        IOrderContext order = await acme.NewOrder([hostName]).ConfigureAwait(false);
        IAuthorizationContext authz = (await order.Authorizations().ConfigureAwait(false)).First();
        IChallengeContext challenge = await authz.Dns().ConfigureAwait(false);

        // Certes computes the RFC-8555 DNS-01 digest from the account key + token; we only ever publish it.
        string dnsTxt = acme.AccountKey.DnsTxt(challenge.Token);
        var dns01 = new Dns01Challenge($"_acme-challenge.{hostName}", dnsTxt);
        _logger?.LogInformation("Placed ACME order for '{Host}' (DNS-01 record {Record}).", hostName, dns01.RecordName);
        return new CertesOrder(authz, challenge, order, dns01, _options, _time, _logger);
    }

    private async Task<IAcmeContext> CreateContextAsync(CancellationToken ct)
    {
        Uri directory = _options.UseStaging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;

        if (File.Exists(_options.AccountKeyPemPath))
        {
            IKey accountKey = KeyFactory.FromPem(await File.ReadAllTextAsync(_options.AccountKeyPemPath, ct).ConfigureAwait(false));
            return new AcmeContext(directory, accountKey);
        }

        var acme = new AcmeContext(directory);
        await acme.NewAccount(_options.Email, termsOfServiceAgreed: true).ConfigureAwait(false);
        await PersistAccountKeyAsync(acme.AccountKey.ToPem(), ct).ConfigureAwait(false);
        return acme;
    }

    private async Task PersistAccountKeyAsync(string pem, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(_options.AccountKeyPemPath);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(_options.AccountKeyPemPath, pem, ct).ConfigureAwait(false);
    }

    private sealed class CertesOrder : IAcmeOrder
    {
        private readonly IAuthorizationContext _authz;
        private readonly IChallengeContext _challenge;
        private readonly IOrderContext _order;
        private readonly CertesAcmeClientOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger? _logger;

        public CertesOrder(
            IAuthorizationContext authz,
            IChallengeContext challenge,
            IOrderContext order,
            Dns01Challenge dns01,
            CertesAcmeClientOptions options,
            TimeProvider time,
            ILogger? logger)
        {
            _authz = authz;
            _challenge = challenge;
            _order = order;
            Challenge = dns01;
            _options = options;
            _time = time;
            _logger = logger;
        }

        public Dns01Challenge Challenge { get; }

        public async Task SubmitAndWaitForValidationAsync(CancellationToken ct = default)
        {
            await _challenge.Validate().ConfigureAwait(false);

            DateTimeOffset deadline = _time.GetUtcNow() + _options.ValidationTimeout;
            while (true)
            {
                Authorization resource = await _authz.Resource().ConfigureAwait(false);
                if (resource.Status == AuthorizationStatus.Valid)
                {
                    return;
                }

                if (resource.Status is AuthorizationStatus.Invalid or AuthorizationStatus.Expired
                    or AuthorizationStatus.Deactivated or AuthorizationStatus.Revoked)
                {
                    throw new InvalidOperationException(
                        $"The ACME server did not validate the DNS-01 challenge for '{Challenge.RecordName}' " +
                        $"(status: {resource.Status}). Verify the TXT record was published and has propagated.");
                }

                if (_time.GetUtcNow() >= deadline)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for the ACME server to validate the DNS-01 challenge for '{Challenge.RecordName}'.");
                }

                await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            }
        }

        public async Task<byte[]> DownloadPfxAsync(string friendlyName, CancellationToken ct = default)
        {
            IKey certKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            CertificateChain chain = await _order.Generate(new CsrInfo { CommonName = friendlyName }, certKey).ConfigureAwait(false);
            Certes.Pkcs.PfxBuilder pfx = chain.ToPfx(certKey);
            _logger?.LogInformation("Downloaded ACME certificate for '{Host}'.", friendlyName);
            // Empty password: the PFX is written to a local, host-owned file and re-imported immediately.
            return pfx.Build(friendlyName, string.Empty);
        }
    }
}
