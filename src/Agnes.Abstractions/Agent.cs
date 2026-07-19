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

    /// <summary>When set, the adapter launches the agent inside this sandbox instead of on the host.</summary>
    public ISandboxCommand? Sandbox { get; init; }

    /// <summary>
    /// When true, the user has opted into autonomous operation: the agent runs tool calls without
    /// asking for approval. Default is false — the agent must request permission for each tool call
    /// (surfaced to the user), which is the intended interactive behaviour for Agnes.
    /// </summary>
    public bool SkipPermissions { get; init; }
}

/// <summary>
/// Rewrites a host command so it runs inside a sandbox (e.g. <c>incus exec</c>). Kept in
/// Abstractions so agent adapters can wrap their launch without depending on a sandbox backend.
/// </summary>
public interface ISandboxCommand
{
    /// <summary>Wraps <paramref name="command"/>+<paramref name="arguments"/> to run in the sandbox.</summary>
    (string Command, IReadOnlyList<string> Arguments) WrapCommand(
        string command, IReadOnlyList<string> arguments, string workingDirectory);
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

    /// <summary>
    /// Whether this agent can actually be launched right now (its CLI is installed and resolvable).
    /// Surfaced to clients so the picker doesn't offer agents that will fail to start. Adapters that
    /// launch an external process should probe for it; the default assumes availability.
    /// </summary>
    bool IsAvailable() => true;
}

/// <summary>Resolves whether a launcher command exists on the host, like <c>which</c>/<c>where</c>.</summary>
public static class AgentCommand
{
    public static bool IsOnPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        // A command with a path separator is taken literally.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command);
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return false;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                if (File.Exists(Path.Combine(dir, command + ext)))
                {
                    return true;
                }
            }
        }

        return false;
    }
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

    /// <summary>The modes the agent offers for this session (e.g. Ask / Code), if any.</summary>
    IReadOnlyList<SessionMode> Modes => [];

    /// <summary>The currently active mode id, if the agent reports one.</summary>
    string? CurrentModeId => null;

    /// <summary>Switches the session mode (ACP <c>session/set_mode</c>).</summary>
    Task SetModeAsync(string modeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A selectable session mode offered by an agent (e.g. Ask, Code, Plan).</summary>
public sealed record SessionMode(string Id, string Name);
