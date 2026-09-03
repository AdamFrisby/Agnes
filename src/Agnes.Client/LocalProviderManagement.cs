using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// Reads and writes the host's local model provider, and asks an endpoint what it serves.
///
/// <para>Discovery goes <b>through the host</b> rather than straight from the client. The model server is
/// usually on the host's network, not the client's, and routing it this way also means a paired device
/// never has to hold the provider's key to populate a picker.</para>
/// </summary>
public static class LocalProviderManagement
{
    public static async Task<LocalProviderInfo?> GetAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<LocalProviderInfo>(
                hostUrl.TrimEnd('/') + "/local-provider", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) { client.Dispose(); }
        }
    }

    public static async Task<LocalProviderInfo?> SaveAsync(
        string hostUrl, string token, LocalProviderRequest request,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            var response = await client.PutAsJsonAsync(
                hostUrl.TrimEnd('/') + "/local-provider", request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content
                .ReadFromJsonAsync<LocalProviderInfo>(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) { client.Dispose(); }
        }
    }

    /// <summary>
    /// Asks the endpoint for its models. A blank <see cref="LocalProviderRequest.ApiKey"/> means "use the
    /// key already stored on the host", so a saved provider can be tested without the client holding its
    /// credential.
    /// </summary>
    public static async Task<LocalProviderModels> ModelsAsync(
        string hostUrl, string token, LocalProviderRequest probe,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            var response = await client.PostAsJsonAsync(
                hostUrl.TrimEnd('/') + "/local-provider/models", probe, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<LocalProviderModels>(cancellationToken)
                       .ConfigureAwait(false)
                   ?? new LocalProviderModels(false, [], "The host didn't answer with a model list.");
        }
        catch (Exception ex)
        {
            // Reported rather than thrown: a settings screen wants to show why it could not ask, and an
            // unreachable endpoint is an ordinary outcome of typing a URL.
            return new LocalProviderModels(false, [], ex.Message);
        }
        finally
        {
            if (owned) { client.Dispose(); }
        }
    }

    private static (HttpClient Client, bool Owned) Client(HttpClient? provided, string token)
    {
        var client = provided ?? new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, provided is null);
    }
}
