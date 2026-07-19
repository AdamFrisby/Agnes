using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>Lists, adds, updates and removes the MCP servers registered on a host (needs a token).</summary>
public static class McpManagement
{
    public static async Task<IReadOnlyList<McpServerInfo>> ListAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<IReadOnlyList<McpServerInfo>>(
                       hostUrl.TrimEnd('/') + "/mcp", cancellationToken).ConfigureAwait(false)
                   ?? [];
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task<McpServerInfo?> AddAsync(
        string hostUrl, string token, McpServerRequest request, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client
                .PostAsJsonAsync(hostUrl.TrimEnd('/') + "/mcp", request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<McpServerInfo>(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task<McpServerInfo?> UpdateAsync(
        string hostUrl, string token, string id, McpServerRequest request, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client
                .PutAsJsonAsync($"{hostUrl.TrimEnd('/')}/mcp/{id}", request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<McpServerInfo>(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task RemoveAsync(
        string hostUrl, string token, string id, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client
                .DeleteAsync($"{hostUrl.TrimEnd('/')}/mcp/{id}", cancellationToken).ConfigureAwait(false);
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
