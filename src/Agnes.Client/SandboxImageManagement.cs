using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>Reads and updates the host's baked sandbox-image manifest (requires a token).</summary>
public static class SandboxImageManagement
{
    public static async Task<SandboxImageView?> GetAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<SandboxImageView>(
                hostUrl.TrimEnd('/') + "/sandbox/image", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task<SandboxImageStatusDto?> GetStatusAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<SandboxImageStatusDto>(
                hostUrl.TrimEnd('/') + "/sandbox/image/status", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>Saves the manifest and starts a background rebuild; returns the (building) status.</summary>
    public static async Task<SandboxImageStatusDto?> SaveAsync(
        string hostUrl, string token, SandboxImageDto manifest, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client
                .PutAsJsonAsync(hostUrl.TrimEnd('/') + "/sandbox/image", manifest, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SandboxImageStatusDto>(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task<SandboxImageStatusDto?> RebuildAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client
                .PostAsync(hostUrl.TrimEnd('/') + "/sandbox/image/rebuild", null, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SandboxImageStatusDto>(cancellationToken).ConfigureAwait(false);
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
