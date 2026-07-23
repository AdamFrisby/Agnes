using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Host.Mcp;

/// <summary>
/// The config-assembly + connection seam for OpenAI's Realtime voice endpoint. It builds the realtime session
/// configuration that points the model at THIS host's Agnes MCP endpoint (OpenAI Realtime natively supports
/// MCP connectors), so a spoken instruction becomes an MCP tool call back into Agnes — the same authorized
/// path a client uses.
///
/// The live audio loop (WebRTC/WebSocket transport carrying microphone + speaker audio) needs a real OpenAI
/// key and a runtime and is out of scope here; this service assembles the session config and exposes the
/// connection parameters. The OpenAI API key is a secret: it authenticates the OpenAI connection (an
/// Authorization header) and is deliberately kept OUT of the assembled session config and never logged.
/// </summary>
public sealed class OpenAiRealtimeVoiceService
{
    private static readonly JsonSerializerOptions SessionJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly VoiceRealtimeOptions _options;

    public OpenAiRealtimeVoiceService(VoiceRealtimeOptions options) => _options = options;

    /// <summary>Whether the realtime path is usable (an OpenAI key is configured). False → a no-op seam.</summary>
    public bool IsAvailable => _options.IsUsable;

    /// <summary>
    /// Assembles the realtime session configuration referencing the Agnes MCP endpoint + configured model.
    /// Throws when no key is configured (callers should gate on <see cref="IsAvailable"/> first). The OpenAI
    /// API key is NOT part of this object — it belongs in the connection's Authorization header, not the body.
    /// </summary>
    public RealtimeSessionConfig BuildSessionConfig()
    {
        if (!_options.IsUsable)
        {
            throw new InvalidOperationException(
                "OpenAI realtime voice is not configured — set Agnes:Voice:OpenAI:ApiKey in host settings.");
        }

        var connector = new RealtimeMcpConnector(
            ServerLabel: "agnes",
            ServerUrl: _options.McpEndpointUrl,
            // The device token the realtime session presents back to the Agnes MCP endpoint. Null (rather than
            // an empty "Bearer ") when unset, so an unauthenticated connector isn't silently assembled.
            Authorization: string.IsNullOrWhiteSpace(_options.McpAuthToken) ? null : $"Bearer {_options.McpAuthToken}",
            RequireApproval: "never");

        return new RealtimeSessionConfig(
            Model: _options.Model,
            Voice: _options.Voice,
            Tools: [connector]);
    }

    /// <summary>The connection parameters for the live audio loop: the model, the JSON body to POST/negotiate,
    /// and the secret API key to send as a bearer token. The key is returned here (for the runtime to place in
    /// the Authorization header) but is never serialized into <see cref="RealtimeConnection.SessionConfigJson"/>
    /// and never logged.</summary>
    public RealtimeConnection BuildConnection()
    {
        var config = BuildSessionConfig();
        return new RealtimeConnection(
            Model: _options.Model,
            SessionConfigJson: JsonSerializer.Serialize(config, SessionJson),
            ApiKey: _options.ApiKey);
    }
}

/// <summary>The realtime session configuration handed to OpenAI (contains NO secret): the model, optional
/// voice, and the MCP connectors the model may call — here, the Agnes MCP endpoint.</summary>
public sealed record RealtimeSessionConfig(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("voice")] string? Voice,
    [property: JsonPropertyName("tools")] IReadOnlyList<RealtimeMcpConnector> Tools);

/// <summary>An OpenAI Realtime MCP connector entry — how the realtime session is told to reach an MCP server.
/// <see cref="Authorization"/> is the bearer credential the session presents to that MCP server (the Agnes
/// device token), independent of the OpenAI API key.</summary>
public sealed record RealtimeMcpConnector(
    [property: JsonPropertyName("server_label")] string ServerLabel,
    [property: JsonPropertyName("server_url")] string ServerUrl,
    [property: JsonPropertyName("authorization")] string? Authorization,
    [property: JsonPropertyName("require_approval")] string RequireApproval)
{
    [JsonPropertyName("type")]
    public string Type => "mcp";
}

/// <summary>Everything the (out-of-scope) live audio runtime needs to open a realtime connection. The
/// <see cref="ApiKey"/> is the secret bearer for the OpenAI connection and is deliberately absent from
/// <see cref="SessionConfigJson"/>.</summary>
public sealed record RealtimeConnection(string Model, string SessionConfigJson, string ApiKey);
