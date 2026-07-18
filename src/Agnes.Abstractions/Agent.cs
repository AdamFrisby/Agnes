using System.Threading.Channels;

namespace Agnes.Abstractions;

/// <summary>Capabilities an agent negotiated during initialization.</summary>
public sealed record AgentCapabilities
{
    /// <summary>The agent can resume prior sessions via <c>session/load</c>.</summary>
    public bool LoadSession { get; init; }

    /// <summary>The agent accepts image content in prompts.</summary>
    public bool PromptImage { get; init; }

    /// <summary>The agent accepts audio content in prompts.</summary>
    public bool PromptAudio { get; init; }

    /// <summary>Mode ids the agent exposes (may be empty).</summary>
    public IReadOnlyList<string> Modes { get; init; } = [];
}

/// <summary>Identity and negotiated capabilities of an agent instance.</summary>
public sealed record AgentDescriptor
{
    /// <summary>Stable id for the agent kind, e.g. <c>claude-code</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-friendly name, e.g. <c>Claude Code</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Agent-reported version, if known.</summary>
    public string? Version { get; init; }

    public AgentCapabilities Capabilities { get; init; } = new();
}

/// <summary>Options for starting a new agent session.</summary>
public sealed record AgentSessionOptions
{
    /// <summary>Absolute working directory the agent should operate in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Extra environment variables to set for the agent process.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// A plugin that knows how to launch and describe one kind of coding agent.
/// Implementations are typically thin configuration over the generic ACP client.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>Describes the agent kind this adapter launches.</summary>
    AgentDescriptor Descriptor { get; }

    /// <summary>Launches the agent and opens a new session.</summary>
    Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// A live conversation with an agent. Emits <see cref="SessionEvent"/>s to a single
/// consumer (the host's session manager), which persists and fans them out to clients.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>The agent-assigned session id.</summary>
    string AgentSessionId { get; }

    /// <summary>The ordered stream of events produced by this session.</summary>
    ChannelReader<SessionEvent> Events { get; }

    /// <summary>Sends a user prompt and completes when the resulting turn ends.</summary>
    Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation of the in-flight turn, if any.</summary>
    Task CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>Answers an outstanding <see cref="PermissionRequestedEvent"/>.</summary>
    Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default);
}
