using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The ACME DNS-01 host-certificate provider, driven against a FAKE ACME client + fake DNS provider (no real
/// Let's Encrypt, no real DNS). Proves the order flow runs in the right order — place order → publish the DNS-01
/// TXT → validate → download — and that the resulting cert + CA-validated name + fingerprint are exposed. The real
/// Let's Encrypt issuance is exercised only against a real domain.
/// </summary>
public sealed class AcmeDns01HostCertificateProviderTests
{
    private const string HostName = "host.example.com";

    [Fact]
    public async Task Drives_the_dns01_order_flow_and_returns_a_certificate()
    {
        var trace = new List<string>();
        var dns = new FakeDnsChallengeProvider(trace);
        var acme = new FakeAcmeClient(trace, HostName);
        string pfxPath = Path.Combine(Path.GetTempPath(), $"agnes-acme-{Guid.NewGuid():n}.pfx");

        try
        {
            using var provider = new AcmeDns01HostCertificateProvider(
                new AcmeHostCertificateOptions { HostName = HostName, PfxPath = pfxPath }, acme, dns);

            await provider.EnsureReadyAsync();

            // Order of operations: the challenge TXT MUST be published before validation is submitted, and cleaned
            // up after the cert is downloaded.
            Assert.Equal(["order", "dns:add", "acme:validate", "acme:download", "dns:remove"], trace);

            X509Certificate2 cert = provider.GetCertificate();
            Assert.True(cert.HasPrivateKey);
            Assert.Equal(HostName, provider.CaValidatedHostName); // client validates chain+name, not a pin
            Assert.Equal(
                Convert.ToHexStringLower(cert.GetCertHash(HashAlgorithmName.SHA256)), provider.Fingerprint);
            Assert.True(File.Exists(pfxPath)); // persisted for reuse across restarts

            // The DNS-01 TXT the provider published is exactly the value the ACME order computed.
            Assert.Equal($"_acme-challenge.{HostName}", dns.PublishedRecordName);
            Assert.Equal(acme.ExpectedTxtValue, dns.PublishedValue);
        }
        finally
        {
            if (File.Exists(pfxPath))
            {
                File.Delete(pfxPath);
            }
        }
    }

    [Fact]
    public async Task Reuses_the_persisted_certificate_without_reordering()
    {
        var acme = new FakeAcmeClient([], HostName);
        var dns = new FakeDnsChallengeProvider([]);
        string pfxPath = Path.Combine(Path.GetTempPath(), $"agnes-acme-{Guid.NewGuid():n}.pfx");

        try
        {
            using (var first = new AcmeDns01HostCertificateProvider(
                new AcmeHostCertificateOptions { HostName = HostName, PfxPath = pfxPath }, acme, dns))
            {
                await first.EnsureReadyAsync();
            }

            var acme2 = new FakeAcmeClient([], HostName);
            using var second = new AcmeDns01HostCertificateProvider(
                new AcmeHostCertificateOptions { HostName = HostName, PfxPath = pfxPath }, acme2, dns);
            await second.EnsureReadyAsync();

            // A still-valid persisted cert must be reused — no new ACME order.
            Assert.Equal(0, acme2.OrdersPlaced);
        }
        finally
        {
            if (File.Exists(pfxPath))
            {
                File.Delete(pfxPath);
            }
        }
    }

    private sealed class FakeDnsChallengeProvider(List<string> trace) : IDnsChallengeProvider
    {
        public string? PublishedRecordName { get; private set; }
        public string? PublishedValue { get; private set; }

        public Task AddTxtRecordAsync(string recordName, string value, CancellationToken ct = default)
        {
            PublishedRecordName = recordName;
            PublishedValue = value;
            trace.Add("dns:add");
            return Task.CompletedTask;
        }

        public Task RemoveTxtRecordAsync(string recordName, string value, CancellationToken ct = default)
        {
            trace.Add("dns:remove");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAcmeClient(List<string> trace, string expectedHost) : IAcmeClient
    {
        public int OrdersPlaced { get; private set; }
        public string ExpectedTxtValue => "txt-for-" + expectedHost;

        public Task<IAcmeOrder> BeginOrderAsync(string hostName, CancellationToken ct = default)
        {
            OrdersPlaced++;
            trace.Add("order");
            Assert.Equal(expectedHost, hostName);
            return Task.FromResult<IAcmeOrder>(new FakeOrder(trace, hostName, ExpectedTxtValue));
        }
    }

    private sealed class FakeOrder : IAcmeOrder
    {
        private readonly List<string> _trace;
        private readonly string _hostName;

        public FakeOrder(List<string> trace, string hostName, string txtValue)
        {
            _trace = trace;
            _hostName = hostName;
            Challenge = new Dns01Challenge($"_acme-challenge.{hostName}", txtValue);
        }

        public Dns01Challenge Challenge { get; }

        public Task SubmitAndWaitForValidationAsync(CancellationToken ct = default)
        {
            _trace.Add("acme:validate");
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadPfxAsync(string friendlyName, CancellationToken ct = default)
        {
            _trace.Add("acme:download");
            // A BCL-built self-signed cert stands in for the CA-issued cert (with an empty-password PFX, matching
            // how the real Certes download is loaded).
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={_hostName}", key, HashAlgorithmName.SHA256);
            using X509Certificate2 cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
            return Task.FromResult(cert.Export(X509ContentType.Pkcs12, string.Empty));
        }
    }
}
