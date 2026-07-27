using System.Net.Http;
using Agnes.Client;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Integration.Tests;

/// <summary>
/// The REST management calls behind the settings surface, against a real self-signed host over a real TLS
/// handshake.
///
/// This is the shape of a bug that shipped: the hub connection honoured the pinned fingerprint, so sessions
/// connected and looked healthy, while every management call built itself a default <see cref="HttpClient"/>
/// and failed the handshake — a settings page of TLS errors on a host that was working fine. The tests below
/// pin the two halves of that: an unpinned call must fail (which is why the pin has to be threaded through),
/// and the same call with <see cref="AgnesHttp.For"/> must succeed.
///
/// Real Kestrel on a loopback port, not the in-memory test server: the handshake is the thing under test, and
/// the in-memory server never performs one.
/// </summary>
public sealed class PinnedManagementCallTests : IAsyncLifetime
{
    private WebApplication? _app;
    private SelfSignedHostCertificateProvider? _certificates;
    private string _baseUrl = string.Empty;

    private string Fingerprint => _certificates!.Fingerprint;

    private static readonly DeviceInfo[] Devices =
    [
        new("d1", "laptop", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "pairing"),
    ];

    /// <summary>The bearer token each request presented, so a test can prove the header still arrives.</summary>
    private readonly List<string?> _seenTokens = [];

    public async Task InitializeAsync()
    {
        var pfx = Path.Combine(Path.GetTempPath(), $"agnes-mgmt-pin-{Guid.NewGuid():n}.pfx");
        _certificates = new SelfSignedHostCertificateProvider(pfx);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ConfigureHttpsDefaults(https =>
                https.ServerCertificateSelector = (_, _) => _certificates.GetCertificate());
            kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps());
        });

        _app = builder.Build();
        _app.MapGet("/devices", (HttpContext ctx) =>
        {
            lock (_seenTokens)
            {
                _seenTokens.Add(ctx.Request.Headers.Authorization.ToString());
            }

            return Results.Ok(Devices);
        });
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
    public async Task A_management_call_with_no_pinned_client_cannot_reach_a_self_signed_host()
    {
        // The regression itself. If this ever starts passing, the host stopped being self-signed — not that
        // the problem went away.
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => DeviceManagement.ListAsync(_baseUrl, "token"));
    }

    [Fact]
    public async Task The_same_call_through_the_pinned_client_succeeds()
    {
        var devices = await DeviceManagement.ListAsync(_baseUrl, "token", AgnesHttp.For(Fingerprint));

        Assert.Equal("laptop", Assert.Single(devices).Name);
    }

    [Fact]
    public async Task A_wrong_pin_still_fails_the_handshake_rather_than_falling_back()
    {
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => DeviceManagement.ListAsync(_baseUrl, "token", AgnesHttp.For(new string('a', 64))));
    }

    [Fact]
    public async Task The_bearer_token_still_reaches_the_host_through_a_pooled_client()
    {
        // AgnesHttp pools the handler but not the client, precisely so the Authorization header the helpers
        // set stays private to one call. Prove the header survives that arrangement.
        lock (_seenTokens) { _seenTokens.Clear(); }

        await DeviceManagement.ListAsync(_baseUrl, "first-token", AgnesHttp.For(Fingerprint));
        await DeviceManagement.ListAsync(_baseUrl, "second-token", AgnesHttp.For(Fingerprint));

        lock (_seenTokens)
        {
            Assert.Equal(["Bearer first-token", "Bearer second-token"], _seenTokens);
        }
    }

    [Fact]
    public async Task Concurrent_calls_with_different_tokens_do_not_leak_one_anothers_credentials()
    {
        // The reason a pooled *client* would have been wrong: two settings panels refreshing at once against
        // different hosts must not be able to send each other's bearer token.
        lock (_seenTokens) { _seenTokens.Clear(); }

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            DeviceManagement.ListAsync(_baseUrl, $"token-{i}", AgnesHttp.For(Fingerprint))));

        lock (_seenTokens)
        {
            Assert.Equal(12, _seenTokens.Count);
            Assert.Equal(12, _seenTokens.Distinct().Count());
        }
    }

    [Fact]
    public void One_handler_is_shared_per_pin_so_connections_are_reused()
    {
        Assert.Same(AgnesHttp.HandlerFor(Fingerprint), AgnesHttp.HandlerFor(Fingerprint));
        Assert.Same(AgnesHttp.HandlerFor(Fingerprint), AgnesHttp.HandlerFor(Fingerprint.ToUpperInvariant()));
        Assert.NotSame(AgnesHttp.HandlerFor(Fingerprint), AgnesHttp.HandlerFor(new string('b', 64)));
        Assert.Same(AgnesHttp.HandlerFor(null), AgnesHttp.HandlerFor("   "));
    }

    [Fact]
    public void Each_caller_gets_its_own_client_so_a_header_is_never_shared()
        => Assert.NotSame(AgnesHttp.For(Fingerprint), AgnesHttp.For(Fingerprint));

    [Fact]
    public async Task A_rejected_certificate_is_explained_as_a_certificate_problem()
    {
        var ex = await Record.ExceptionAsync(() => DeviceManagement.ListAsync(_baseUrl, "token"));

        Assert.NotNull(ex);
        Assert.True(AuthDiscovery.IsCertificateFailure(ex), "a TLS failure must be recognisable as one");
        // Not "install the host's CA": for a pinned host there is no CA, and re-pairing is the actual fix.
        Assert.Contains("pair again", AuthDiscovery.DescribeFailure(ex), StringComparison.OrdinalIgnoreCase);
    }
}
