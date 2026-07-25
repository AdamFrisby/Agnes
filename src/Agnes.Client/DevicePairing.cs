using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// The host answered, and refused. Carries the status so a client can tell "you typed the wrong code"
/// apart from "this host doesn't do pairing" — and, crucially, apart from never having reached the host
/// at all, which surfaces as an <see cref="HttpRequestException"/> instead.
/// </summary>
public sealed class PairingRefusedException : Exception
{
    public PairingRefusedException(HttpStatusCode status, string message)
        : base(message) => Status = status;

    public HttpStatusCode Status { get; }

    /// <summary>Whether the host specifically rejected the code (as opposed to refusing to pair at all).</summary>
    public bool IsBadCode => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>
/// Pairs this client with a host: presents the host's pairing code and receives a durable
/// per-device token to connect with. The token is what the caller stores and passes to
/// <see cref="IAgnesConnector.ConnectAsync"/>.
/// </summary>
public static class DevicePairing
{
    /// <summary>
    /// Exchanges a pairing code for a per-device token.
    /// </summary>
    /// <exception cref="PairingRefusedException">The host answered and refused.</exception>
    /// <exception cref="HttpRequestException">The host could not be reached.</exception>
    public static async Task<PairResponse> PairAsync(
        string hostUrl, string code, string deviceName,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            var url = hostUrl.TrimEnd('/') + "/pair";
            using var response = await client
                .PostAsJsonAsync(url, new PairRequest(code.Trim(), deviceName), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new PairingRefusedException(response.StatusCode, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        "The host rejected that pairing code.",
                    HttpStatusCode.NotFound =>
                        "That address answered, but it isn't an Agnes host (no /pair endpoint).",
                    _ => $"The host refused to pair ({(int)response.StatusCode} {response.StatusCode}).",
                });
            }

            return await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken).ConfigureAwait(false)
                   ?? throw new PairingRefusedException(response.StatusCode, "Pairing returned no token.");
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
