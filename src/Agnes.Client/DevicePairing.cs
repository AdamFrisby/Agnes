using System.Net.Http;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// Pairs this client with a host: presents the host's pairing code and receives a durable
/// per-device token to connect with. The token is what the caller stores and passes to
/// <see cref="IAgnesConnector.ConnectAsync"/>.
/// </summary>
public static class DevicePairing
{
    /// <summary>Exchanges a pairing code for a per-device token, or throws with the host's reason.</summary>
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
                throw new InvalidOperationException(
                    $"Pairing failed ({(int)response.StatusCode}). Check the pairing code shown on the host.");
            }

            return await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Pairing returned no token.");
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
