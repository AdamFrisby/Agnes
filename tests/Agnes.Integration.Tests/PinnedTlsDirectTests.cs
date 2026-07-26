using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Agnes.Client;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Integration.Tests;

/// <summary>
/// Bootstrapping TLS trust from pairing: a host with a self-signed certificate, a client that trusts it
/// because the pairing QR said which certificate to expect, and no CA anywhere.
///
/// These run against a <b>real</b> Kestrel listener over a real TLS handshake on a loopback port, not the
/// in-memory test server, because the thing under test is the handshake itself. The in-memory server never
/// does TLS, so it would pass no matter what the trust logic did.
/// </summary>
public sealed class PinnedTlsDirectTests : IAsyncLifetime
{
    private sealed class PingHub : Hub;

    private WebApplication? _app;
    private SelfSignedHostCertificateProvider? _certificates;
    private string _baseUrl = string.Empty;

    /// <summary>The fingerprint a pairing QR would advertise for this host.</summary>
    private string Fingerprint => _certificates!.Fingerprint;

    /// <summary>A well-formed pin that is not this host's — what an attacker's certificate would produce.</summary>
    private static string WrongPin => new('a', 64);

    public async Task InitializeAsync()
    {
        var pfx = Path.Combine(Path.GetTempPath(), $"agnes-pin-it-{Guid.NewGuid():n}.pfx");
        _certificates = new SelfSignedHostCertificateProvider(pfx);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Exactly what the host now does on the Direct path: serve the self-signed certificate.
            kestrel.ConfigureHttpsDefaults(https =>
                https.ServerCertificateSelector = (_, _) => _certificates.GetCertificate());
            kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps());
        });

        _app = builder.Build();
        _app.MapGet("/ping", () => Results.Ok("pong"));
        _app.MapHub<PingHub>(WireProtocol.HubPath);
        await _app.StartAsync();

        _baseUrl = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _certificates?.Dispose();
    }

    [Fact]
    public async Task The_advertised_fingerprint_is_the_certificate_the_host_actually_serves()
    {
        // If these ever diverge, every client pins a value the handshake can never produce and the whole
        // scheme fails closed — so this is the load-bearing assertion for everything below.
        using var client = PinnedTls.CreateClient(Fingerprint);

        var body = await client.GetStringAsync($"{_baseUrl}/ping");

        Assert.Contains("pong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_pin_fails_the_handshake()
    {
        using var client = PinnedTls.CreateClient(WrongPin);

        // Not "connects but warns": there is no path where a certificate we didn't expect is accepted.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync($"{_baseUrl}/ping"));
    }

    [Fact]
    public async Task Without_a_pin_a_self_signed_host_is_rejected_as_it_always_was()
    {
        // Proves the pin is what does the work: the same request through the OS trust store fails, which
        // is precisely the problem pinning exists to solve. If this ever passes, something is trusting
        // self-signed certificates wholesale.
        using var client = new HttpClient();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync($"{_baseUrl}/ping"));
    }

    [Fact]
    public async Task A_hub_connection_to_a_self_signed_host_succeeds_with_the_right_pin()
    {
        // The end-to-end claim: scan the QR, connect over HTTPS, no CA and nothing installed. This goes
        // through HostConnection itself, so it covers the WebSocket transport too — which opens its own
        // socket and would otherwise be unpinned even when the negotiate was.
        await using var connection = new HostConnection(_baseUrl, "a-token", pinnedFingerprint: Fingerprint);

        await connection.ConnectAsync();

        Assert.Equal(AgnesConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task A_hub_connection_with_the_wrong_pin_hard_fails()
    {
        await using var connection = new HostConnection(_baseUrl, "a-token", pinnedFingerprint: WrongPin);

        // SSH known_hosts semantics: a certificate that isn't the one we pinned is a refusal, never a
        // prompt-and-continue.
        await Assert.ThrowsAnyAsync<Exception>(() => connection.ConnectAsync());
        Assert.NotEqual(AgnesConnectionState.Connected, connection.State);
    }

    [Fact]
    public void A_pin_matches_only_the_certificate_it_was_taken_from()
    {
        var certificate = _certificates!.GetCertificate();

        Assert.True(PinnedTls.Matches(certificate, Fingerprint));
        Assert.True(PinnedTls.Matches(certificate, Fingerprint.ToUpperInvariant()), "case must not matter");
        Assert.True(PinnedTls.Matches(certificate, "  " + Fingerprint + "  "), "whitespace must not matter");

        Assert.False(PinnedTls.Matches(certificate, WrongPin));
        Assert.False(PinnedTls.Matches(certificate, Fingerprint[..^1]), "a truncated pin is not a match");
        Assert.False(PinnedTls.Matches(certificate, string.Empty));
        Assert.False(PinnedTls.Matches(null, Fingerprint));
    }

    [Fact]
    public void The_fingerprint_is_the_sha256_of_the_certificates_der_bytes()
    {
        // Pinned across host and client and printed into a QR, so the format is a contract: lower-case
        // hex SHA-256 of the DER, 64 characters.
        var certificate = _certificates!.GetCertificate();
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(certificate.Export(X509ContentType.Cert)));

        Assert.Equal(expected, Fingerprint);
        Assert.Equal(64, Fingerprint.Length);
        Assert.Equal(Fingerprint, Fingerprint.ToLowerInvariant());
    }
}
