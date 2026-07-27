using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Registries.McpRegistry;

/// <summary>
/// The official Model Context Protocol registry at <see href="https://registry.modelcontextprotocol.io"/> —
/// the community-run index the MCP project itself publishes, and the closest thing to a canonical answer to
/// "which MCP servers exist".
///
/// A registry entry describes several ways to obtain the same server: any number of <c>packages</c> (npm,
/// pypi, oci, …), each with its own transport, and any number of <c>remotes</c> (hosted endpoints). Agnes
/// installs exactly one server, so <see cref="Map"/> picks the way that needs the least of the user: a hosted
/// remote first — nothing to install, no runtime to have — then an npm or pypi package we know how to launch.
/// An entry offering only, say, an OCI image is skipped rather than mapped into a command that wouldn't run.
/// </summary>
public sealed class OfficialMcpRegistryProvider : IMcpCatalogProvider
{
    private const string DefaultBaseUrl = "https://registry.modelcontextprotocol.io";
    private const int PageSize = 30;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OfficialMcpRegistryProvider(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public string Id => "mcp-official";

    public string DisplayName => "Official MCP registry";

    public bool SupportsSearch => true;

    public Task<IReadOnlyList<McpCatalogEntry>> ListAsync(CancellationToken ct = default)
        => GetAsync($"{_baseUrl}/v0.1/servers?limit={PageSize}", ct);

    public Task<IReadOnlyList<McpCatalogEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? string.Empty;
        return q.Length == 0
            ? ListAsync(ct)
            : GetAsync($"{_baseUrl}/v0.1/servers?search={Uri.EscapeDataString(q)}&limit={PageSize}", ct);
    }

    private async Task<IReadOnlyList<McpCatalogEntry>> GetAsync(string url, CancellationToken ct)
    {
        var page = await _http.GetFromJsonAsync<ServerListResponse>(url, ct).ConfigureAwait(false);
        return (page?.Servers ?? [])
            .Select(s => s.Server)
            .Where(s => s is not null)
            .Select(s => Map(s!))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToArray();
    }

    /// <summary>
    /// Turns one registry server into a catalogue entry, or null when Agnes has no way to launch any of the
    /// forms it is published in. Public and pure so the mapping is testable without a registry.
    /// </summary>
    public static McpCatalogEntry? Map(RegistryServer server)
    {
        // A hosted endpoint is the cheapest thing for a user to adopt: no runtime, no install, no local process.
        if (server.Remotes?.FirstOrDefault(r => r.Url is { Length: > 0 }) is { } remote)
        {
            return Entry(server, McpCatalogTransport.Http, command: null, args: [], url: remote.Url, env: []);
        }

        foreach (var package in server.Packages ?? [])
        {
            if (Launch(package) is not { } launch)
            {
                continue;
            }

            return Entry(server, McpCatalogTransport.Stdio, launch.Command, launch.Args, url: null, Env(package));
        }

        return null;
    }

    /// <summary>How to start a published package, or null for a registry type Agnes can't run directly.</summary>
    private static (string Command, IReadOnlyList<string> Args)? Launch(RegistryPackage package)
    {
        var identifier = package.Identifier;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        // Registry-supplied runtime arguments come first (they're things like npx's -y), then the package
        // itself, then the server's own arguments.
        var prefix = Values(package.RuntimeArguments);
        var suffix = Values(package.PackageArguments);
        var versioned = package.Version is { Length: > 0 } v ? $"{identifier}@{v}" : identifier;

        return package.RegistryType?.ToLowerInvariant() switch
        {
            "npm" => ("npx", [.. prefix.DefaultIfEmpty("-y"), versioned, .. suffix]),
            "pypi" => ("uvx", [.. prefix, identifier, .. suffix]),
            "nuget" => ("dnx", [.. prefix, versioned, .. suffix]),
            _ => null, // oci, mcpb, … — real, but not something Agnes can spawn as a stdio child today.
        };
    }

    private static IReadOnlyList<string> Values(IReadOnlyList<RegistryArgument>? arguments)
        => arguments?.Where(a => a.Value is { Length: > 0 }).Select(a => a.Value!).ToArray() ?? [];

    private static IReadOnlyList<McpCatalogEnvVar> Env(RegistryPackage package)
        => package.EnvironmentVariables?
               .Where(v => v.Name is { Length: > 0 })
               .Select(v => new McpCatalogEnvVar(v.Name!, v.Description, v.IsRequired ?? false, v.IsSecret ?? false, v.Default))
               .ToArray()
           ?? [];

    private static McpCatalogEntry Entry(
        RegistryServer server,
        McpCatalogTransport transport,
        string? command,
        IReadOnlyList<string> args,
        string? url,
        IReadOnlyList<McpCatalogEnvVar> env)
        => new(
            Id: server.Name,
            // Registry names are reverse-DNS ("io.github.owner/thing"); the last segment is what a person
            // recognises, and the full name still travels as the id.
            Name: server.Title is { Length: > 0 } title ? title : ShortName(server.Name),
            Description: server.Description,
            Publisher: Publisher(server.Name),
            Homepage: server.Repository?.Url,
            Transport: transport,
            Command: command,
            Args: args,
            Url: url,
            EnvironmentVariables: env,
            Version: server.Version);

    private static string ShortName(string name)
    {
        var afterSlash = name[(name.LastIndexOf('/') + 1)..];
        return afterSlash.Length > 0 ? afterSlash : name;
    }

    private static string? Publisher(string name)
    {
        var slash = name.IndexOf('/');
        return slash > 0 ? name[..slash] : null;
    }

    // ---- the slice of the registry's schema we read, typed at the boundary ----

    private sealed record ServerListResponse(
        [property: JsonPropertyName("servers")] IReadOnlyList<ServerEnvelope>? Servers);

    private sealed record ServerEnvelope([property: JsonPropertyName("server")] RegistryServer? Server);

    /// <summary>One server as the official registry publishes it.</summary>
    public sealed record RegistryServer(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description = null,
        [property: JsonPropertyName("title")] string? Title = null,
        [property: JsonPropertyName("version")] string? Version = null,
        [property: JsonPropertyName("repository")] RegistryRepository? Repository = null,
        [property: JsonPropertyName("packages")] IReadOnlyList<RegistryPackage>? Packages = null,
        [property: JsonPropertyName("remotes")] IReadOnlyList<RegistryRemote>? Remotes = null);

    public sealed record RegistryRepository([property: JsonPropertyName("url")] string? Url = null);

    public sealed record RegistryRemote(
        [property: JsonPropertyName("type")] string? Type = null,
        [property: JsonPropertyName("url")] string? Url = null);

    public sealed record RegistryPackage(
        [property: JsonPropertyName("registryType")] string? RegistryType = null,
        [property: JsonPropertyName("identifier")] string? Identifier = null,
        [property: JsonPropertyName("version")] string? Version = null,
        [property: JsonPropertyName("runtimeHint")] string? RuntimeHint = null,
        [property: JsonPropertyName("runtimeArguments")] IReadOnlyList<RegistryArgument>? RuntimeArguments = null,
        [property: JsonPropertyName("packageArguments")] IReadOnlyList<RegistryArgument>? PackageArguments = null,
        [property: JsonPropertyName("environmentVariables")] IReadOnlyList<RegistryEnvVar>? EnvironmentVariables = null);

    public sealed record RegistryArgument(
        [property: JsonPropertyName("type")] string? Type = null,
        [property: JsonPropertyName("value")] string? Value = null);

    public sealed record RegistryEnvVar(
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("description")] string? Description = null,
        [property: JsonPropertyName("isRequired")] bool? IsRequired = null,
        [property: JsonPropertyName("isSecret")] bool? IsSecret = null,
        [property: JsonPropertyName("default")] string? Default = null);
}
