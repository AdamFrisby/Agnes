using Agnes.Host.Hosting;
using Microsoft.AspNetCore.Http;

namespace Agnes.Host.Tests;

public class AuthRateLimitTests
{
    [Theory]
    [InlineData("/pair", true)]
    [InlineData("/auth/github/exchange", true)]
    [InlineData("/auth/keypair", true)]
    [InlineData("/auth/keypair/challenge", true)]
    [InlineData("/auth/methods", false)]  // discovery — no secret, clients poll it
    [InlineData("/devices", false)]
    [InlineData("/hub", false)]
    public void Only_token_minting_paths_are_rate_limited(string path, bool expected)
        => Assert.Equal(expected, AuthRateLimit.IsSensitivePath(new PathString(path)));

    [Fact]
    public void Forwarded_for_is_only_trusted_when_configured()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.7, 10.0.0.1";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");

        Assert.Equal("10.0.0.1", AuthRateLimit.ClientKey(ctx, trustForwardedFor: false));
        Assert.Equal("203.0.113.7", AuthRateLimit.ClientKey(ctx, trustForwardedFor: true));
    }
}
