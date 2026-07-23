using System.Net;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The DuckDNS DynDNS DNS-01 provider: it must issue the exact documented TXT-set GET
/// (<c>update?domains=&amp;token=&amp;txt=</c>) for a challenge value, and surface a clear error when DuckDNS
/// rejects the update. Fully offline — the HttpClient is backed by a capturing stub handler.
/// </summary>
public sealed class DuckDnsChallengeProviderTests
{
    private static DuckDnsOptions Options() => new()
    {
        Domains = "myhost",
        Token = "secret-token",
        BaseUrl = "https://www.duckdns.org/update",
    };

    [Fact]
    public async Task Add_issues_the_documented_duckdns_txt_set_call()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") });
        using var http = new HttpClient(handler);
        var provider = new DuckDnsChallengeProvider(http, Options());

        await provider.AddTxtRecordAsync("_acme-challenge.myhost.duckdns.org", "the-challenge-digest");

        Uri url = Assert.Single(handler.Requests);
        Assert.Equal("https://www.duckdns.org/update", url.GetLeftPart(UriPartial.Path));
        string query = url.Query;
        Assert.Contains("domains=myhost", query, StringComparison.Ordinal);
        Assert.Contains("token=secret-token", query, StringComparison.Ordinal);
        Assert.Contains("txt=the-challenge-digest", query, StringComparison.Ordinal);
        Assert.Contains("clear=false", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_clears_the_txt_record()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") });
        using var http = new HttpClient(handler);
        var provider = new DuckDnsChallengeProvider(http, Options());

        await provider.RemoveTxtRecordAsync("_acme-challenge.myhost.duckdns.org", "the-challenge-digest");

        Uri url = Assert.Single(handler.Requests);
        Assert.Contains("clear=true", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duckdns_ko_response_surfaces_a_clear_error()
    {
        // DuckDNS answers 'KO' with HTTP 200 for a bad token/domain — the provider must not treat that as success.
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("KO") });
        using var http = new HttpClient(handler);
        var provider = new DuckDnsChallengeProvider(http, Options());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.AddTxtRecordAsync("_acme-challenge.myhost.duckdns.org", "digest"));
        Assert.Contains("DuckDNS rejected", ex.Message, StringComparison.Ordinal);
        Assert.Contains("myhost", ex.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
