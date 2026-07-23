using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Mcp;

/// <summary>
/// Host-settings-sourced configuration for the OpenAI Realtime voice path (<c>Agnes:Voice:OpenAI:*</c>). The
/// operator plugs in their own OpenAI API key later; everything here comes from host settings, and the API key
/// is treated as a secret (never logged, never placed in the assembled session config that is handed to
/// OpenAI). The service is only usable once a key is present — an unconfigured host runs identically to today.
/// </summary>
/// <param name="ApiKey">The OpenAI API key (secret). Empty when unconfigured.</param>
/// <param name="Model">The realtime model id (e.g. <c>gpt-realtime</c>).</param>
/// <param name="McpEndpointUrl">The absolute URL of THIS host's Agnes MCP endpoint, handed to the realtime
/// session as an MCP connector so the model can drive Agnes.</param>
/// <param name="McpAuthToken">The Agnes device token the realtime session presents back to the Agnes MCP
/// endpoint (a bearer token, authenticated on every call). Distinct from the OpenAI API key.</param>
/// <param name="Voice">Optional synthesis voice id, or null for the model default.</param>
public sealed record VoiceRealtimeOptions(
    string ApiKey,
    string Model,
    string McpEndpointUrl,
    string McpAuthToken,
    string? Voice = null)
{
    /// <summary>The default realtime model when none is configured.</summary>
    public const string DefaultModel = "gpt-realtime";

    /// <summary>Usable only once an API key is configured. Gates whether the realtime service does anything.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Binds options from configuration. <paramref name="defaultMcpEndpointUrl"/> is the host's own Agnes MCP
    /// endpoint URL, used unless an explicit <c>Agnes:Voice:OpenAI:McpEndpointUrl</c> override is set (handy
    /// when the host is reached through a relay/tunnel and the URL OpenAI must dial differs from the local one).
    /// </summary>
    public static VoiceRealtimeOptions FromConfiguration(IConfiguration configuration, string defaultMcpEndpointUrl)
    {
        var section = configuration.GetSection("Agnes:Voice:OpenAI");
        var endpoint = section["McpEndpointUrl"];
        return new VoiceRealtimeOptions(
            ApiKey: section["ApiKey"] ?? string.Empty,
            Model: string.IsNullOrWhiteSpace(section["Model"]) ? DefaultModel : section["Model"]!,
            McpEndpointUrl: string.IsNullOrWhiteSpace(endpoint) ? defaultMcpEndpointUrl : endpoint!,
            McpAuthToken: section["McpAuthToken"] ?? string.Empty,
            Voice: section["Voice"]);
    }
}
