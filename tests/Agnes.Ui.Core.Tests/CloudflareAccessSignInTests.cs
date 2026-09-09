using System.Net;
using System.Text;
using Agnes.Client;

namespace Agnes.Ui.Core.Tests;

public sealed class CloudflareAccessSignInTests
{
    [Fact]
    public async Task Exchange_posts_only_the_device_name_and_returns_the_device_token()
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://agnes.example/auth/cloudflare-access/exchange", request.RequestUri!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("Agnes browser", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Cf-Access-Jwt-Assertion", body, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, "{\"deviceId\":\"dev-1\",\"deviceName\":\"Agnes browser\",\"token\":\"secret-token\"}");
        }));

        var result = await CloudflareAccessSignIn.ExchangeAsync("https://agnes.example/", "Agnes browser", client);

        Assert.Equal("dev-1", result.DeviceId);
        Assert.Equal("secret-token", result.Token);
    }

    [Fact]
    public async Task Exchange_reports_a_gateway_denial_without_leaking_a_credential()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Forbidden, "{}")));

        var refusal = await Assert.ThrowsAsync<CloudflareAccessRefusedException>(
            () => CloudflareAccessSignIn.ExchangeAsync("https://agnes.example", "Agnes browser", client));

        Assert.Equal(HttpStatusCode.Forbidden, refusal.Status);
        Assert.Contains("did not admit", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
