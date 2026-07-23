using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LettuceEncrypt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agnes.Relay;

/// <summary>Server-authenticates an accepted relay connection (wraps it in TLS) before any relay frame is read.</summary>
public interface IRelayConnectionSecurity
{
    /// <summary>Performs the server-side TLS handshake over <paramref name="raw"/> and returns the secured stream.</summary>
    Task<Stream> WrapAsync(Stream raw, CancellationToken ct = default);
}

/// <summary>
/// Config-gated (<c>Agnes:Relay:PublicDomain</c>) LettuceEncrypt wiring for the relay's OWN public control
/// endpoint. The relay has a public address, so standard ACME (HTTP-01/TLS-ALPN-01) works here — unlike the NAT'd
/// host, which must use DNS-01. Off by default: with no public domain the relay serves plain TCP (local/testing),
/// and none of this engages. This does not weaken the blind pipe — the secured connection carries only the
/// relay-protocol framing the relay already owns; the tunnelled client↔host TLS inside it stays opaque.
/// </summary>
public static class RelayEndpointTls
{
    /// <summary>True when the relay is configured with a public domain and should obtain + serve a real cert.</summary>
    public static bool IsEnabled(RelayOptions options) => !string.IsNullOrWhiteSpace(options.PublicDomain);
}

/// <summary>
/// Captures the certificate LettuceEncrypt obtains/renews for the relay (via <see cref="ICertificateRepository"/>)
/// and offers it back on restart (via <see cref="ICertificateSource"/>), persisting it to disk with the BCL. The
/// live cert is exposed to the TLS wrapper through <see cref="Current"/>.
/// </summary>
public sealed class RelayCertificateStore : ICertificateRepository, ICertificateSource
{
    private readonly string? _pfxPath;
    private readonly IRelayLog _log;
    private volatile X509Certificate2? _current;

    public RelayCertificateStore(string? pfxPath, IRelayLog log)
    {
        _pfxPath = string.IsNullOrWhiteSpace(pfxPath) ? null : pfxPath;
        _log = log;
    }

    /// <summary>The most recently obtained relay certificate, or <c>null</c> until ACME first succeeds.</summary>
    public X509Certificate2? Current => _current;

    /// <inheritdoc />
    public Task SaveAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        _current = certificate;
        _log.Info($"Relay endpoint certificate obtained (expires {certificate.NotAfter:u}).");

        if (_pfxPath is not null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_pfxPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                File.WriteAllBytes(_pfxPath, certificate.Export(X509ContentType.Pkcs12));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not persist the relay endpoint certificate: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IEnumerable<X509Certificate2>> GetCertificatesAsync(CancellationToken cancellationToken)
    {
        if (_current is null && _pfxPath is not null && File.Exists(_pfxPath))
        {
            try
            {
                _current = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(_pfxPath), null, X509KeyStorageFlags.Exportable);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                _log.Warn($"Could not load the persisted relay endpoint certificate: {ex.Message}");
            }
        }

        IEnumerable<X509Certificate2> result = _current is null ? [] : [_current];
        return Task.FromResult(result);
    }
}

/// <summary>Server-side TLS using the relay's live LettuceEncrypt certificate from a <see cref="RelayCertificateStore"/>.</summary>
public sealed class TlsRelayConnectionSecurity : IRelayConnectionSecurity
{
    private readonly RelayCertificateStore _store;

    public TlsRelayConnectionSecurity(RelayCertificateStore store) => _store = store;

    /// <inheritdoc />
    public async Task<Stream> WrapAsync(Stream raw, CancellationToken ct = default)
    {
        X509Certificate2 certificate = _store.Current
            ?? throw new InvalidOperationException(
                "The relay endpoint certificate has not been issued yet — the connection cannot be secured.");

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// The running LettuceEncrypt web host that obtains + renews the relay's public certificate. It is a tiny
/// ASP.NET Core host bound to the ACME challenge ports (80/443); the relay's own broker keeps running on its
/// configured TCP port and is TLS-wrapped with <see cref="Security"/> using the obtained cert.
/// </summary>
public sealed class RelayTlsEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RelayTlsEndpoint(WebApplication app, IRelayConnectionSecurity security)
    {
        _app = app;
        Security = security;
    }

    /// <summary>The TLS wrapper the broker applies to each accepted connection.</summary>
    public IRelayConnectionSecurity Security { get; }

    /// <summary>Builds and starts the LettuceEncrypt host, returning a wrapper the broker uses to secure connections.</summary>
    public static async Task<RelayTlsEndpoint> StartAsync(RelayOptions options, IRelayLog log, CancellationToken ct = default)
    {
        string certDir = string.IsNullOrWhiteSpace(options.AcmeCertificateDirectory)
            ? Path.Combine(Path.GetTempPath(), "agnes-relay-acme")
            : options.AcmeCertificateDirectory;

        var store = new RelayCertificateStore(Path.Combine(certDir, "relay-endpoint-cert.pfx"), log);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddLettuceEncrypt(o =>
        {
            o.DomainNames = [options.PublicDomain];
            o.EmailAddress = options.AcmeEmailAddress;
            o.AcceptTermsOfService = true;
            o.UseStagingServer = options.AcmeUseStagingServer;
        }).PersistDataToDirectory(new DirectoryInfo(certDir), null);
        // Our store captures the obtained cert in-memory (for the broker's TLS wrapper) and reloads it on restart.
        builder.Services.AddSingleton<ICertificateRepository>(store);
        builder.Services.AddSingleton<ICertificateSource>(store);
        builder.WebHost.UseUrls(
            $"http://0.0.0.0:{options.AcmeHttpPort}",   // HTTP-01 challenge
            $"https://0.0.0.0:{options.AcmeHttpsPort}"); // TLS-ALPN-01 challenge (LettuceEncrypt configures HTTPS)

        WebApplication app = builder.Build();
        await app.StartAsync(ct).ConfigureAwait(false);
        log.Info($"Relay endpoint ACME host started for '{options.PublicDomain}' (obtaining a Let's Encrypt certificate).");
        return new RelayTlsEndpoint(app, new TlsRelayConnectionSecurity(store));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);
}
