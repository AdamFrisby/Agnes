using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// Manages a host's git-credential linking (needs a token): reads the GitHub App connection status,
/// starts the Connect-GitHub flow (returns the loopback URL to open in a browser), and stores a
/// fine-grained-PAT fallback source.
/// </summary>
public static class CredentialManagement
{
    public static async Task<CredentialStatus?> GetStatusAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<CredentialStatus>(
                hostUrl.TrimEnd('/') + "/credentials/status", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>Starts the Connect-GitHub flow; returns the loopback URL to open in the user's browser.</summary>
    public static async Task<string?> ConnectGitHubAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client.PostAsync(
                hostUrl.TrimEnd('/') + "/credentials/github/connect", null, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ConnectResponse>(cancellationToken).ConfigureAwait(false);
            return result?.Url;
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>Stores a token credential source (a fine-grained PAT) for a host.</summary>
    public static async Task StoreTokenAsync(
        string hostUrl, string token, string targetHost, string pat, string? username = null,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client.PostAsJsonAsync(hostUrl.TrimEnd('/') + "/credentials/token",
                new StoreCredentialRequest(targetHost, pat, username), cancellationToken).ConfigureAwait(false);
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

    private sealed record ConnectResponse(string Url);
}
