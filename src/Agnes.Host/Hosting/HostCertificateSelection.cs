using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// Resolves which <see cref="IHostCertificateProvider"/> the relay path uses, from configuration. When a real-CA
/// cert is configured under <c>Agnes:Transport:Relay:Cert</c> (a domain + a DNS-01 provider) the ACME provider is
/// chosen; otherwise the persistent self-signed default is kept — so an unconfigured host keeps working exactly as
/// before (the client just pins the self-signed fingerprint). This is the single selection seam referenced by
/// startup and covered by the non-regression selection test.
/// </summary>
public static class HostCertificateSelection
{
    /// <summary>True when a real-CA cert is configured (a cert hostname is derivable under <c>…:Cert</c>).</summary>
    public static bool IsAcmeConfigured(IConfiguration configuration, string hostId)
        => ResolveHostName(configuration, hostId) is not null;

    /// <summary>Builds the configured provider — the ACME DNS-01 provider when configured, else self-signed.</summary>
    public static IHostCertificateProvider Create(
        IConfiguration configuration,
        string hostId,
        string agnesHome,
        HttpClient dnsHttpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        string selfSignedPfx = configuration["Agnes:Transport:Relay:CertFile"]
            ?? Path.Combine(agnesHome, "relay-host-cert.pfx");

        string? hostName = ResolveHostName(configuration, hostId);
        if (hostName is null)
        {
            return new SelfSignedHostCertificateProvider(
                selfSignedPfx, logger: loggerFactory.CreateLogger<SelfSignedHostCertificateProvider>());
        }

        IConfigurationSection cert = configuration.GetSection("Agnes:Transport:Relay:Cert");
        string email = cert["Email"]
            ?? throw new InvalidOperationException(
                "A real-CA relay certificate needs a contact email. Set Agnes:Transport:Relay:Cert:Email.");

        var acmeClient = new CertesAcmeClient(
            new CertesAcmeClientOptions
            {
                Email = email,
                AccountKeyPemPath = cert["AccountKeyFile"] ?? Path.Combine(agnesHome, "acme-account-key.pem"),
                UseStaging = cert.GetValue("Staging", false),
            },
            timeProvider,
            loggerFactory.CreateLogger<CertesAcmeClient>());

        IDnsChallengeProvider dns = CreateDnsProvider(cert, dnsHttpClient, loggerFactory);

        return new AcmeDns01HostCertificateProvider(
            new AcmeHostCertificateOptions
            {
                HostName = hostName,
                PfxPath = cert["CertFile"] ?? Path.Combine(agnesHome, "relay-host-acme-cert.pfx"),
                RenewBefore = TimeSpan.FromDays(cert.GetValue("RenewBeforeDays", 30)),
            },
            acmeClient,
            dns,
            timeProvider,
            loggerFactory.CreateLogger<AcmeDns01HostCertificateProvider>());
    }

    /// <summary>The FQDN the cert is issued for: explicit <c>Cert:Hostname</c>, else <c>&lt;hostId&gt;.&lt;Cert:Domain&gt;</c>.</summary>
    private static string? ResolveHostName(IConfiguration configuration, string hostId)
    {
        IConfigurationSection cert = configuration.GetSection("Agnes:Transport:Relay:Cert");
        if (cert["Hostname"] is { Length: > 0 } explicitName)
        {
            return explicitName;
        }

        if (cert["Domain"] is { Length: > 0 } domain)
        {
            return string.IsNullOrWhiteSpace(hostId) ? domain : $"{hostId}.{domain}";
        }

        return null;
    }

    private static IDnsChallengeProvider CreateDnsProvider(
        IConfigurationSection cert, HttpClient http, ILoggerFactory loggerFactory)
    {
        string provider = cert["Provider"]?.ToLowerInvariant()
            ?? InferProvider(cert);

        switch (provider)
        {
            case "duckdns":
                return new DuckDnsChallengeProvider(
                    http,
                    new DuckDnsOptions
                    {
                        Domains = Require(cert, "DuckDns:Domains"),
                        Token = Require(cert, "DuckDns:Token"),
                        BaseUrl = cert["DuckDns:BaseUrl"] ?? "https://www.duckdns.org/update",
                    },
                    loggerFactory.CreateLogger<DuckDnsChallengeProvider>());

            case "cloudflare":
            case "generic":
                var api = new CloudflareDnsTxtRecordApi(
                    http,
                    new CloudflareOptions
                    {
                        ApiToken = Require(cert, "Cloudflare:ApiToken"),
                        ZoneId = Require(cert, "Cloudflare:ZoneId"),
                        BaseUrl = cert["Cloudflare:BaseUrl"] ?? "https://api.cloudflare.com/client/v4",
                    });
                return new GenericDnsChallengeProvider(api, loggerFactory.CreateLogger<GenericDnsChallengeProvider>());

            default:
                throw new InvalidOperationException(
                    $"Unknown DNS-01 provider '{provider}'. Set Agnes:Transport:Relay:Cert:Provider to 'duckdns' or 'cloudflare'.");
        }
    }

    private static string InferProvider(IConfigurationSection cert)
    {
        if (cert["DuckDns:Token"] is { Length: > 0 })
        {
            return "duckdns";
        }

        if (cert["Cloudflare:ApiToken"] is { Length: > 0 })
        {
            return "cloudflare";
        }

        throw new InvalidOperationException(
            "A real-CA relay certificate needs a DNS-01 provider. Set Agnes:Transport:Relay:Cert:Provider " +
            "(duckdns|cloudflare) and the matching credential block.");
    }

    private static string Require(IConfigurationSection cert, string key)
        => cert[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing required setting Agnes:Transport:Relay:Cert:{key}.");
}
