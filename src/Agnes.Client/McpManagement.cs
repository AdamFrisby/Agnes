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

    /// <summary>The host's curated quick-install presets (aggregated across every IMcpPresetProvider).</summary>
    public static async Task<IReadOnlyList<McpServerInfo>> PresetsAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            return await client.GetFromJsonAsync<IReadOnlyList<McpServerInfo>>(
                       hostUrl.TrimEnd('/') + "/mcp/presets", cancellationToken).ConfigureAwait(false)
                   ?? [];
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>Preview the effective, scope-filtered MCP set that WOULD be active for a workspace (a pure
    /// read; pass null for the host-wide view). Answers "what will actually run if I start a session now."</summary>
    public static async Task<IReadOnlyList<McpServerInfo>> PreviewEffectiveAsync(
        string hostUrl, string token, string? workspaceId = null, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var (client, owned) = Client(httpClient, token);
        try
        {
            var url = hostUrl.TrimEnd('/') + "/mcp/effective";
            if (!string.IsNullOrEmpty(workspaceId))
            {
                url += "?workspaceId=" + Uri.EscapeDataString(workspaceId);
            }

            return await client.GetFromJsonAsync<IReadOnlyList<McpServerInfo>>(url, cancellationToken).ConfigureAwait(false) ?? [];
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>Quick-install a curated preset: turns its template into a normal add-server request (the
    /// exact same persistence path a hand-typed server uses), optionally scoped, so no config is retyped.</summary>
    public static Task<McpServerInfo?> InstallPresetAsync(
        string hostUrl, string token, McpServerInfo template,
        McpApplyScope scope = McpApplyScope.AllHosts, string? workspaceId = null,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => AddAsync(hostUrl, token, new McpServerRequest(
            template.Name, template.RunAt, template.Enabled, template.Transport,
            template.Command, template.Args, template.Env, template.Url, template.BearerTokenEnv,
            scope, scope == McpApplyScope.ThisWorkspace ? workspaceId : null), httpClient, cancellationToken);

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
