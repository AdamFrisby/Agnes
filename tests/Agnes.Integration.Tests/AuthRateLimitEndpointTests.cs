using System.Net;
using System.Net.Http.Json;
using Agnes.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>The auth endpoints are throttled: repeated attempts get 429, while discovery stays open.</summary>
public class AuthRateLimitEndpointTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:Auth:RateLimit:PerIpPerMinute"] = "3",  // small so the test is fast + deterministic
                    ["Agnes:Auth:RateLimit:GlobalPerMinute"] = "1000",
                }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Pair_is_throttled_but_discovery_is_not()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        // The first 3 wrong-code attempts are processed (401); the 4th is rate-limited (429).
        for (var i = 0; i < 3; i++)
        {
            var attempt = await http.PostAsJsonAsync("/pair", new PairRequest("WRONG-CODE", "x"));
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var blocked = await http.PostAsJsonAsync("/pair", new PairRequest("WRONG-CODE", "x"));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // /auth/methods is exempt — clients poll it while entering a host address.
        for (var i = 0; i < 6; i++)
        {
            using var methods = await http.GetAsync("/auth/methods");
            Assert.Equal(HttpStatusCode.OK, methods.StatusCode);
        }
    }
}
