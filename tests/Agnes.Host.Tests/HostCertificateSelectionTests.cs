using Agnes.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The host-certificate selection seam: when a real-CA cert is configured under <c>Agnes:Transport:Relay:Cert</c>
/// the ACME DNS-01 provider is chosen (client validates the CA chain+name); with nothing configured the
/// self-signed default is kept (client pins the fingerprint) — the non-regression guarantee for existing hosts.
/// Construction is offline: neither provider touches the network until acquisition.
/// </summary>
public sealed class HostCertificateSelectionTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"agnes-cert-sel-{Guid.NewGuid():n}");

    [Fact]
    public void Chooses_the_acme_provider_when_a_cert_is_configured()
    {
        IConfiguration config = Config(new Dictionary<string, string?>
        {
            ["Agnes:Transport:Relay:Cert:Domain"] = "example.com",
            ["Agnes:Transport:Relay:Cert:Email"] = "ops@example.com",
            ["Agnes:Transport:Relay:Cert:Provider"] = "duckdns",
            ["Agnes:Transport:Relay:Cert:DuckDns:Domains"] = "myhost",
            ["Agnes:Transport:Relay:Cert:DuckDns:Token"] = "tok",
        });

        Assert.True(HostCertificateSelection.IsAcmeConfigured(config, "hostid"));

        using var http = new HttpClient();
        IHostCertificateProvider provider = HostCertificateSelection.Create(
            config, "hostid", TempDir(), http, TimeProvider.System, NullLoggerFactory.Instance);

        var acme = Assert.IsType<AcmeDns01HostCertificateProvider>(provider);
        Assert.Equal("hostid.example.com", acme.CaValidatedHostName); // <hostId>.<domain>
    }

    [Fact]
    public void Keeps_the_self_signed_default_when_no_cert_is_configured()
    {
        IConfiguration config = Config([]);

        Assert.False(HostCertificateSelection.IsAcmeConfigured(config, "hostid"));

        using var http = new HttpClient();
        IHostCertificateProvider provider = HostCertificateSelection.Create(
            config, "hostid", TempDir(), http, TimeProvider.System, NullLoggerFactory.Instance);

        Assert.IsType<SelfSignedHostCertificateProvider>(provider);
        Assert.Null(provider.CaValidatedHostName); // signals the client to pin the fingerprint
        (provider as IDisposable)?.Dispose();
    }

    [Fact]
    public void An_explicit_hostname_overrides_the_derived_one()
    {
        IConfiguration config = Config(new Dictionary<string, string?>
        {
            ["Agnes:Transport:Relay:Cert:Hostname"] = "relay.example.net",
            ["Agnes:Transport:Relay:Cert:Email"] = "ops@example.com",
            ["Agnes:Transport:Relay:Cert:Provider"] = "cloudflare",
            ["Agnes:Transport:Relay:Cert:Cloudflare:ApiToken"] = "cf-token",
            ["Agnes:Transport:Relay:Cert:Cloudflare:ZoneId"] = "zone-1",
        });

        using var http = new HttpClient();
        var acme = (AcmeDns01HostCertificateProvider)HostCertificateSelection.Create(
            config, "hostid", TempDir(), http, TimeProvider.System, NullLoggerFactory.Instance);

        Assert.Equal("relay.example.net", acme.CaValidatedHostName);
    }
}
