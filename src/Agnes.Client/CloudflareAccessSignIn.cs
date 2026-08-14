using System.Net;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// The host answered the Cloudflare Access exchange endpoint, but did not issue a device token.
/// The browser has already authenticated at Cloudflare; this exception describes the host-side
/// configuration or allowlist problem without exposing an assertion or other credential.
/// </summary>
public sealed class CloudflareAccessRefusedException : Exception
{
    public CloudflareAccessRefusedException(HttpStatusCode status, string message)
        : base(message) => Status = status;

    public HttpStatusCode Status { get; }
}

/// <summary>
/// Exchanges the signed Cloudflare Access assertion that a trusted proxy injects into a browser request
/// for a normal, individually revocable Agnes device token. The assertion is never read, handled, or
/// stored by this client; it stays in the request header between Cloudflare, cloudflared, and the host.
/// </summary>
public static class CloudflareAccessSignIn
{
    /// <summary>
    /// Uses a same-origin browser request to mint a device token after Cloudflare Access has admitted the
    /// browser. Native clients can use this only when their request path is likewise behind a proxy that
    /// injects the signed assertion.
    /// </summary>
    /// <exception cref="CloudflareAccessRefusedException">The host rejected or has not configured Cloudflare Access.</exception>
    /// <exception cref="HttpRequestException">The host could not be reached.</exception>
    public static async Task<PairResponse> ExchangeAsync(
        string hostUrl,
        string deviceName,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            var url = hostUrl.TrimEnd('/') + "/auth/cloudflare-access/exchange";
            using var response = await client
                .PostAsJsonAsync(url, new CloudflareAccessExchangeRequest(deviceName), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new CloudflareAccessRefusedException(response.StatusCode, response.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                        "Cloudflare Access did not admit this browser to Agnes. Sign in at the gateway with an allowed account.",
                    HttpStatusCode.BadRequest =>
                        "Cloudflare Access sign-in is not configured on this Agnes host.",
                    HttpStatusCode.NotFound =>
                        "That address answered, but it is not an Agnes host with Cloudflare Access sign-in.",
                    _ => $"The host refused Cloudflare Access sign-in ({(int)response.StatusCode} {response.StatusCode}).",
                });
            }

            return await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken).ConfigureAwait(false)
                   ?? throw new CloudflareAccessRefusedException(response.StatusCode, "Cloudflare Access returned no device token.");
        }
        finally
        {
            if (httpClient is null)
            {
                client.Dispose();
            }
        }
    }
}
