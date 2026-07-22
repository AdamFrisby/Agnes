using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace Agnes.Host.Hosting;

/// <summary>Config for auth-endpoint rate limiting (bound from <c>Agnes:Auth:RateLimit</c>).</summary>
public sealed record AuthRateLimitOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Max sensitive-auth requests per client IP per minute.</summary>
    public int PerIpPerMinute { get; init; } = 10;

    /// <summary>Max sensitive-auth requests across ALL clients per minute (distributed-attack backstop).</summary>
    public int GlobalPerMinute { get; init; } = 100;

    /// <summary>Behind a trusted reverse proxy, take the client IP from the leftmost X-Forwarded-For entry.</summary>
    public bool TrustForwardedFor { get; init; }
}

/// <summary>
/// Rate-limits the token-minting / challenge endpoints (<c>/pair</c>, <c>/auth/github/exchange</c>,
/// <c>/auth/keypair</c>[/challenge]) both per client IP and globally — so no single IP can hammer them and
/// a distributed attempt is still capped overall. <c>/auth/methods</c> is deliberately exempt: it exposes
/// no secret and clients poll it while entering a host address. Uses the built-in ASP.NET rate limiter.
/// </summary>
public static class AuthRateLimit
{
    public static bool IsSensitivePath(PathString path) =>
        path.StartsWithSegments("/pair")
        || path.StartsWithSegments("/auth/github/exchange")
        || path.StartsWithSegments("/auth/keypair"); // also covers /auth/keypair/challenge

    /// <summary>The partition key for per-IP limiting (honours X-Forwarded-For only when trusted).</summary>
    public static string ClientKey(HttpContext context, bool trustForwardedFor)
    {
        if (trustForwardedFor && context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var first = forwarded.ToString().Split(',', 2)[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static void Configure(RateLimiterOptions options, AuthRateLimitOptions cfg)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return ValueTask.CompletedTask;
        };

        // A request to a sensitive path must pass BOTH the per-IP limiter and the global limiter; every
        // other request gets no limiter. Chaining means either limit can reject.
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                !cfg.Enabled || !IsSensitivePath(context.Request.Path)
                    ? RateLimitPartition.GetNoLimiter("none")
                    : RateLimitPartition.GetFixedWindowLimiter("ip:" + ClientKey(context, cfg.TrustForwardedFor),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = cfg.PerIpPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                !cfg.Enabled || !IsSensitivePath(context.Request.Path)
                    ? RateLimitPartition.GetNoLimiter("none")
                    : RateLimitPartition.GetFixedWindowLimiter("global-auth",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = cfg.GlobalPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })));
    }
}
