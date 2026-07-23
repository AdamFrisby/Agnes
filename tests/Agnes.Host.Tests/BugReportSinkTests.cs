using System.Net;
using Agnes.Abstractions;
using Agnes.Host.Ops;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class BugReportSinkTests
{
    private static BugReport Report(byte[]? payload = null)
        => new("Crash on prompt", "It exploded when I typed.", "Nothing rendered", "A response", payload);

    // ---- CustomEndpointSink: the byte cap is enforced locally, before any upload ----

    [Fact]
    public async Task Custom_endpoint_rejects_an_oversized_payload_before_sending()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var sink = new CustomEndpointSink(new HttpClient(handler), "https://example.test/report",
            maxPayloadBytes: 16, timeout: TimeSpan.FromSeconds(5), NullLogger<CustomEndpointSink>.Instance);

        var result = await sink.SubmitAsync(Report(new byte[1024]));

        Assert.False(result.Success);
        Assert.Contains("cap", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Calls); // never hit the network with an oversized body
    }

    [Fact]
    public async Task Custom_endpoint_posts_a_within_cap_report()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler((req, _) => { seen = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var sink = new CustomEndpointSink(new HttpClient(handler), "https://example.test/report",
            maxPayloadBytes: 1024, timeout: TimeSpan.FromSeconds(5), NullLogger<CustomEndpointSink>.Instance);

        var result = await sink.SubmitAsync(Report(new byte[8]));

        Assert.True(result.Success);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, seen!.Method);
    }

    // ---- GitHubIssueSink: browser fallback when there's no token ----

    [Fact]
    public async Task GitHub_sink_without_a_token_returns_a_prefilled_new_issue_url()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("should not call the API"));
        var sink = new GitHubIssueSink(new HttpClient(handler), "AdamFrisby/Agnes", token: null, NullLogger<GitHubIssueSink>.Instance);

        var result = await sink.SubmitAsync(Report());

        Assert.False(result.Success);
        Assert.NotNull(result.Url);
        Assert.StartsWith("https://github.com/AdamFrisby/Agnes/issues/new?", result.Url);
        Assert.Contains(Uri.EscapeDataString("Crash on prompt"), result.Url);      // title carried
        Assert.Contains(Uri.EscapeDataString("It exploded"), result.Url);          // summary carried
        Assert.Equal(0, handler.Calls);                                            // no network without a token
    }

    [Fact]
    public async Task GitHub_sink_fallback_url_never_carries_a_diagnostic_payload()
    {
        var sink = new GitHubIssueSink(new HttpClient(new FakeHandler((_, _) => new HttpResponseMessage())),
            "AdamFrisby/Agnes", token: null, NullLogger<GitHubIssueSink>.Instance);

        // Even if a payload were present, it must not leak into the public browser URL.
        var result = await sink.SubmitAsync(Report(payload: [1, 2, 3, 4, 5]));

        Assert.NotNull(result.Url);
        Assert.DoesNotContain("AQIDBAU", result.Url);   // base64 of the payload bytes
        Assert.DoesNotContain("payload", result.Url, StringComparison.OrdinalIgnoreCase);
    }

    // ---- GitHubIssueSink with a token: duplicate search, then create ----

    [Fact]
    public async Task GitHub_sink_with_a_token_surfaces_duplicates_from_the_search()
    {
        var handler = new FakeHandler((req, _) =>
        {
            Assert.Contains("/search/issues", req.RequestUri!.AbsoluteUri); // only searched; never created
            return Json("""{"items":[{"number":42,"title":"Crash on prompt (existing)","html_url":"https://github.com/AdamFrisby/Agnes/issues/42"}]}""");
        });
        var sink = new GitHubIssueSink(new HttpClient(handler), "AdamFrisby/Agnes", "gh_token", NullLogger<GitHubIssueSink>.Instance);

        var result = await sink.SubmitAsync(Report());

        Assert.False(result.Success);
        Assert.NotNull(result.Duplicates);
        var dup = Assert.Single(result.Duplicates!);
        Assert.Equal(42, dup.Number);
        Assert.Equal("https://github.com/AdamFrisby/Agnes/issues/42", dup.Url);
    }

    [Fact]
    public async Task GitHub_sink_with_a_token_creates_the_issue_when_no_duplicates()
    {
        var createdBody = false;
        var handler = new FakeHandler((req, body) =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/search/issues", StringComparison.Ordinal))
            {
                return Json("""{"items":[]}""");
            }

            // POST /repos/{repo}/issues — the create call.
            Assert.Equal(HttpMethod.Post, req.Method);
            createdBody = body.Contains("Crash on prompt", StringComparison.Ordinal);
            return Json("""{"number":7,"html_url":"https://github.com/AdamFrisby/Agnes/issues/7"}""");
        });
        var sink = new GitHubIssueSink(new HttpClient(handler), "AdamFrisby/Agnes", "gh_token", NullLogger<GitHubIssueSink>.Instance);

        var result = await sink.SubmitAsync(Report());

        Assert.True(result.Success);
        Assert.Equal("https://github.com/AdamFrisby/Agnes/issues/7", result.Url);
        Assert.Null(result.Duplicates);
        Assert.True(createdBody);
        Assert.Equal(2, handler.Calls); // search + create
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }
}
