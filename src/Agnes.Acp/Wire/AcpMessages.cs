using System.Text.Json;

namespace Agnes.Acp.Wire;

// Hand-modeled subset of the Agent Client Protocol (protocol v1) wire messages.
// We model only what Agnes uses, on Microsoft's StreamJsonRpc, rather than depend
// on low-reputation third-party ACP packages. JSON keys are camelCase; enum-like
// discriminators are snake_case strings mapped to Agnes types in AcpMap.

// ---- initialize ----

internal sealed record AcpInitializeParams
{
    public int ProtocolVersion { get; init; } = 1;
    public AcpClientCapabilities ClientCapabilities { get; init; } = new();
}

internal sealed record AcpClientCapabilities
{
    public AcpFsCapability Fs { get; init; } = new();
    public bool Terminal { get; init; }
}

internal sealed record AcpFsCapability
{
    public bool ReadTextFile { get; init; }
    public bool WriteTextFile { get; init; }
}

internal sealed record AcpInitializeResult
{
    public int ProtocolVersion { get; init; }
    public AcpAgentCapabilities? AgentCapabilities { get; init; }
    public IReadOnlyList<AcpAuthMethod>? AuthMethods { get; init; }
}

internal sealed record AcpAgentCapabilities
{
    public bool LoadSession { get; init; }
    public AcpPromptCapabilities? PromptCapabilities { get; init; }
}

internal sealed record AcpPromptCapabilities
{
    public bool Image { get; init; }
    public bool Audio { get; init; }
    public bool EmbeddedContext { get; init; }
}

internal sealed record AcpAuthMethod
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string? Description { get; init; }
}

// ---- session/new ----

internal sealed record AcpNewSessionParams
{
    public required string Cwd { get; init; }
    public IReadOnlyList<AcpMcpServer> McpServers { get; init; } = [];
}

internal sealed record AcpMcpServer
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
}

internal sealed record AcpNewSessionResult
{
    public required string SessionId { get; init; }
    public AcpSessionModeState? Modes { get; init; }
}

internal sealed record AcpSessionModeState
{
    public string? CurrentModeId { get; init; }
    public IReadOnlyList<AcpSessionMode> AvailableModes { get; init; } = [];
}

internal sealed record AcpSessionMode
{
    public required string Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

// ---- session/set_mode (notification) ----

internal sealed record AcpSetModeParams
{
    public required string SessionId { get; init; }
    public required string ModeId { get; init; }
}

// ---- session/prompt ----

internal sealed record AcpPromptParams
{
    public required string SessionId { get; init; }
    public required IReadOnlyList<AcpContentBlock> Prompt { get; init; }
}

internal sealed record AcpPromptResult
{
    public string StopReason { get; init; } = "end_turn";
}

// ---- session/cancel (notification) ----

internal sealed record AcpCancelParams
{
    public required string SessionId { get; init; }
}

// ---- content blocks (flat model covers text / image / resource_link) ----

internal sealed record AcpContentBlock
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? Data { get; init; }
    public string? MimeType { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
}

// ---- session/update (notification from agent) ----

internal sealed record AcpSessionNotification
{
    public required string SessionId { get; init; }
    public JsonElement Update { get; init; }
}

// ---- session/request_permission (request from agent) ----

internal sealed record AcpRequestPermissionParams
{
    public required string SessionId { get; init; }
    public AcpToolCall? ToolCall { get; init; }
    public required IReadOnlyList<AcpPermissionOption> Options { get; init; }
}

internal sealed record AcpToolCall
{
    public string? ToolCallId { get; init; }
    public string? Title { get; init; }
    public string? Kind { get; init; }
    public string? Status { get; init; }
}

internal sealed record AcpPermissionOption
{
    public required string OptionId { get; init; }
    public required string Name { get; init; }
    public string? Kind { get; init; }
}

internal sealed record AcpRequestPermissionResult
{
    public required AcpPermissionOutcome Outcome { get; init; }
}

internal sealed record AcpPermissionOutcome
{
    /// <summary>"selected" or "cancelled".</summary>
    public required string Outcome { get; init; }
    public string? OptionId { get; init; }
}
