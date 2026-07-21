using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>Lists and manages the host's sandbox VMs (list / delete / resume / reap). Requires a token.</summary>
public static class SandboxManagement
{
    public static async Task<IReadOnlyList<SandboxRecordDto>> ListAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<IReadOnlyList<SandboxRecordDto>>(
                hostUrl.TrimEnd('/') + "/sandboxes", cancellationToken).ConfigureAwait(false) ?? [];
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task<IReadOnlyList<SandboxRecordDto>> DeleteAsync(
        string hostUrl, string token, string sessionId, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client.DeleteAsync(
                hostUrl.TrimEnd('/') + "/sandboxes/" + Uri.EscapeDataString(sessionId), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IReadOnlyList<SandboxRecordDto>>(cancellationToken).ConfigureAwait(false) ?? [];
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
