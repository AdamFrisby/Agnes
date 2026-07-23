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

    /// <summary>
    /// When set, resume the agent's prior conversation with this id (e.g. after a host restart)
    /// instead of starting fresh. Only used by adapters whose CLI supports resuming a session.
    /// </summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// Path to a generated MCP-server config file the agent should load (Agnes-managed, so it never
    /// touches the user's own config). Adapters whose CLI takes a config-file flag (e.g. Claude Code's
    /// <c>--mcp-config</c>) pass it; others that read a materialized config file ignore this.
    /// </summary>
    public string? McpConfigPath { get; init; }

    /// <summary>
    /// When set, the model the agent's CLI should use (e.g. Claude Code's <c>--model</c>). Threaded into the
    /// launch invocation by adapters that implement <see cref="IModelListingAdapter"/> in the form that CLI
    /// expects; adapters that don't understand a model axis ignore it. Null means "the CLI's own default".
    /// </summary>
    public string? ModelId { get; init; }
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

    /// <summary>
    /// Whether an agent-reported error message means this agent's credentials went stale mid-session (e.g.
    /// an expired/revoked OAuth token) and the fix is to re-materialize credentials and relaunch it. The
    /// host asks the adapter rather than pattern-matching error text itself, so this knowledge lives with
    /// the agent it's specific to. Default: agents whose credentials can't expire mid-session return false.
    /// </summary>
    bool IsRecoverableCredentialFault(string errorMessage) => false;

    /// <summary>
    /// The CLI's machine-local login state, when this adapter has an honestly reliable way to tell (e.g. a
    /// direct <c>auth status</c> command or a well-defined credentials file). This is distinct from
    /// <see cref="IsAvailable"/> (installed / resolvable) — a CLI can be installed but not logged in.
    /// Default: <c>null</c>, meaning "no reliable signal" — the picker shows no auth badge at all rather
    /// than a confidently-wrong "not logged in". Adapters that can answer confidently override this.
    /// </summary>
    Task<ProviderAuthStatus?> GetAuthStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ProviderAuthStatus?>(null);

    /// <summary>
    /// The interactive login command for this agent's CLI (e.g. a <c>… auth login</c> subcommand), when it
    /// has one — so the host can run it through the <b>same</b> <see cref="ICliFallback.OpenTerminalAsync"/>
    /// CLI-fallback terminal path as the in-session terminal (platform/03 reuse discipline), rather than a
    /// bespoke <c>Process.Start</c>. Default: <c>null</c> — no interactive login (the client shows no
    /// "Log in" action for this adapter).
    /// </summary>
    ProviderLoginCommand? GetInteractiveLoginCommand() => null;
}

/// <summary>
/// An interactive provider-login invocation an adapter exposes, run via the shared CLI-fallback terminal
/// (see <see cref="IAgentAdapter.GetInteractiveLoginCommand"/>). Kept minimal (command + arguments); the
/// host supplies a working directory and terminal dimensions when it opens the terminal.
/// </summary>
public sealed record ProviderLoginCommand(string Command, IReadOnlyList<string> Arguments);

/// <summary>
/// Whether a coding CLI is logged in to its provider on this host, as reported by an adapter that has a
/// reliable signal (see <see cref="IAgentAdapter.GetAuthStatusAsync"/>). <see cref="Identity"/> and
/// <see cref="Method"/> describe who / how when known; <see cref="Issue"/> carries a human-readable reason
/// when not logged in; <see cref="CheckedAt"/> records when the check ran.
/// </summary>
public sealed record ProviderAuthStatus(
    bool IsLoggedIn,
    string? Identity,
    string? Method,
    string? Issue,
    DateTimeOffset CheckedAt);

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

    /// <summary>Answers an outstanding <see cref="QuestionAskedEvent"/> with the user's structured selections;
    /// an empty list means the user dismissed it. Default no-op for adapters that never ask questions.</summary>
    Task AnswerQuestionAsync(string requestId, IReadOnlyList<QuestionAnswer> answers, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>The modes the agent offers for this session (e.g. Ask / Code), if any.</summary>
    IReadOnlyList<SessionMode> Modes => [];

    /// <summary>The currently active mode id, if the agent reports one.</summary>
    string? CurrentModeId => null;

    /// <summary>Switches the session mode (ACP <c>session/set_mode</c>).</summary>
    Task SetModeAsync(string modeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A selectable session mode offered by an agent (e.g. Ask, Code, Plan).</summary>
public sealed record SessionMode(string Id, string Name);
