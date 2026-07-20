using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// Manages a host's projects (needs a token): the per-repo bundles (sandbox + MCP + GitHub account +
/// defaults) a session inherits. Lists them, previews which one a working directory resolves to, and
/// saves/removes them.
/// </summary>
public static class ProjectManagement
{
    public static async Task<IReadOnlyList<ProjectDto>> ListAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<IReadOnlyList<ProjectDto>>(
                       hostUrl.TrimEnd('/') + "/projects", cancellationToken).ConfigureAwait(false)
                   ?? [];
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>The project a working directory would use (non-creating preview).</summary>
    public static async Task<ProjectDto?> ResolveAsync(
        string hostUrl, string token, string workingDirectory, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            var url = $"{hostUrl.TrimEnd('/')}/projects/resolve?dir={Uri.EscapeDataString(workingDirectory)}";
            return await client.GetFromJsonAsync<ProjectDto>(url, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task<ProjectDto?> SaveAsync(
        string hostUrl, string token, ProjectDto project, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client.PutAsJsonAsync(
                $"{hostUrl.TrimEnd('/')}/projects/{project.Id}", project, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProjectDto>(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    public static async Task DeleteAsync(
        string hostUrl, string token, string id, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            using var response = await client.DeleteAsync($"{hostUrl.TrimEnd('/')}/projects/{id}", cancellationToken).ConfigureAwait(false);
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
