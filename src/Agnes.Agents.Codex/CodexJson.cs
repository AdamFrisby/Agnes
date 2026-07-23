using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Agents.Codex;

/// <summary>
/// Shared JSON options for the Codex app-server protocol (newline-delimited JSON-RPC, camelCase
/// keys). Mirrors <c>AcpJson</c> — the two protocols share a transport (StreamJsonRpc +
/// <c>NewLineDelimitedMessageHandler</c>), differing only in method names and payloads.
/// </summary>
internal static class CodexJson
{
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Used by CodexMap to deserialize inbound notification payloads into the typed Wire records.
    public static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web);
}
