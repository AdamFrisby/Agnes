using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>Lists and revokes the devices paired with a host (requires a valid device token).</summary>
public static class DeviceManagement
{
    public static async Task<IReadOnlyList<DeviceInfo>> ListAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<IReadOnlyList<DeviceInfo>>(
                       hostUrl.TrimEnd('/') + "/devices", cancellationToken).ConfigureAwait(false)
                   ?? [];
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task RevokeAsync(
        string hostUrl, string token, string deviceId, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client
                .DeleteAsync($"{hostUrl.TrimEnd('/')}/devices/{deviceId}", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    private static (HttpClient Client, bool Owned) Client(HttpClient? provided, string token)
    {
        var client = provided ?? new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, provided is null);
    }
}
