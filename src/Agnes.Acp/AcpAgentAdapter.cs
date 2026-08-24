using System.Diagnostics;
using System.Threading.Channels;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Acp;

/// <summary>Describes how to launch an ACP agent process.</summary>
public sealed record AcpLaunchSpec
{
    /// <summary>Executable to run (resolved on PATH or an absolute path).</summary>
    public required string Command { get; init; }

    /// <summary>Arguments that put the CLI into ACP mode.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Extra environment variables for the agent process.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Identity advertised for this agent kind.</summary>
    public required AgentDescriptor Descriptor { get; init; }

    /// <summary>Static model catalog surfaced via <see cref="IModelListingAdapter.StaticModels"/> (empty when
    /// this CLI has no model axis Agnes knows about).</summary>
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];

    /// <summary>Optional live model probe. Null (the default) means "no live listing" — resolution falls back
    /// to <see cref="Models"/>.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ModelInfo>?>>? LiveModelProbe { get; init; }

    /// <summary>Argv that makes this CLI print the models it can reach (e.g. <c>["models"]</c>), for probing
    /// inside the environment the agent runs in. Null means the CLI can't be asked, so no verification.</summary>
    public IReadOnlyList<string>? ModelProbeArguments { get; init; }

    /// <summary>Parses <see cref="ModelProbeArguments"/> output. Null falls back to an empty catalogue,
    /// which is treated as "couldn't determine" rather than "no models".</summary>
    public Func<string, IReadOnlyList<ModelInfo>>? ModelProbeParser { get; init; }

    /// <summary>Builds the CLI arguments that select a model id (e.g. <c>--model &lt;id&gt;</c>). Null means
    /// this CLI doesn't take a model flag, so a requested <see cref="AgentSessionOptions.ModelId"/> is ignored.</summary>
    public Func<string, IReadOnlyList<string>>? ModelArguments { get; init; }

    /// <summary>Builds the environment carrying this CLI's inline configuration — model and MCP servers
    /// together (e.g. OpenCode's <c>OPENCODE_CONFIG_CONTENT</c>). Null means this CLI isn't configured
    /// through the environment. Independent of the argv hooks: a CLI whose ACP mode takes no model flag sets
    /// only this one, and Agnes then materializes it into the sandbox rather than appending to a command.</summary>
    public Func<string?, IReadOnlyList<InlineMcpServer>, IReadOnlyDictionary<string, string>>? InlineConfig { get; init; }

    /// <summary>Builds the CLI arguments that inject extra system-prompt text (e.g. Claude Code's
    /// <c>--append-system-prompt &lt;text&gt;</c>). Null means this CLI has no system-prompt flag Agnes knows,
    /// so a requested <see cref="AgentSessionOptions.SystemPrompt"/> is ignored.</summary>
    public Func<string, IReadOnlyList<string>>? SystemPromptArguments { get; init; }

    /// <summary>
    /// Builds the CLI arguments that select the permission model, given
    /// <see cref="AgentSessionOptions.SkipPermissions"/>. The ACP default is the safe one — the agent asks
    /// per tool call over <c>session/request_permission</c> and Agnes surfaces it — so most CLIs need no
    /// flag at all and leave this null. A CLI that only runs unattended behind an explicit blanket-allow
    /// flag (Copilot's <c>--allow-all-tools</c>) states it here, and it is reached <b>only</b> when the user
    /// has opted into autonomous operation. Mirrors <c>INativeStreamMapper.PermissionLaunchArguments</c>.
    /// </summary>
    public Func<bool, IReadOnlyList<string>>? PermissionArguments { get; init; }

    /// <summary>Builds the CLI arguments that load the Agnes-managed MCP config file at
    /// <see cref="AgentSessionOptions.McpConfigPath"/> (e.g. Copilot's
    /// <c>--additional-mcp-config @&lt;path&gt;</c>). Null means this CLI takes no such flag, so a supplied
    /// path is ignored.</summary>
    public Func<string, IReadOnlyList<string>>? McpConfigArguments { get; init; }
}

/// <summary>
/// Generic <see cref="IAgentAdapter"/> for any ACP-compliant CLI. Agent plugins are
/// typically just an <see cref="AcpLaunchSpec"/> passed to this adapter. Not sealed so a plugin can subclass
/// it to add an optional capability its CLI supports (e.g. Claude Code adds <see cref="IMcpDiscoveryAdapter"/>)
/// without re-implementing the ACP launch/session plumbing.
/// </summary>
public class AcpAgentAdapter : IAgentAdapter, IModelListingAdapter, IModelEnvironmentAdapter, IModelProbeAdapter
{
    private readonly AcpLaunchSpec _spec;
    private readonly ILoggerFactory _loggerFactory;

    public AcpAgentAdapter(AcpLaunchSpec spec, ILoggerFactory loggerFactory)
    {
        _spec = spec;
        _loggerFactory = loggerFactory;
    }

    public AgentDescriptor Descriptor => _spec.Descriptor;

    public bool IsAvailable() => AgentCommand.IsOnPath(_spec.Command);

    public IReadOnlyList<ModelInfo> StaticModels => _spec.Models;

    public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default)
        => _spec.LiveModelProbe?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<ModelInfo>?>(null);

    /// <summary>The agent argv for a launch: the base ACP arguments plus the model-selection flag when a
    /// model was requested and this CLI takes one. Pure, so the model-threading rule is unit-testable
    /// without spawning a process.</summary>
    public static IReadOnlyList<string> BuildAgentArguments(AcpLaunchSpec spec, AgentSessionOptions options)
    {
        var args = new List<string>(spec.Arguments);
        if (options.ModelId is { Length: > 0 } modelId && spec.ModelArguments is { } buildModel)
        {
            args.AddRange(buildModel(modelId));
        }

        if (options.SystemPrompt is { Length: > 0 } systemPrompt && spec.SystemPromptArguments is { } buildSystem)
        {
            args.AddRange(buildSystem(systemPrompt));
        }

        // The permission model. Asked for unconditionally (not only when skipping) so a CLI that needs a
        // flag for BOTH stances can state both; the flag for the attended case is the default one.
        if (spec.PermissionArguments is { } buildPermissions)
        {
            args.AddRange(buildPermissions(options.SkipPermissions));
        }

        if (options.McpConfigPath is { Length: > 0 } mcpConfig && spec.McpConfigArguments is { } buildMcp)
        {
            args.AddRange(buildMcp(mcpConfig));
        }

        return args;
    }

    /// <summary>The inline-config environment for a launch, or empty when this CLI has no such axis. Pure,
    /// and shared by the two paths it has to reach: the host process launched here, and the guest agent-env
    /// file the host materializes for a sandboxed session.</summary>
    public static IReadOnlyDictionary<string, string> BuildAgentEnvironment(AcpLaunchSpec spec, AgentSessionOptions options)
        => spec.InlineConfig is { } build
            ? build(options.ModelId, [])
            : new Dictionary<string, string>();

    /// <inheritdoc />
    public IReadOnlyList<string>? ProbeArguments => _spec.ModelProbeArguments;

    /// <inheritdoc />
    public IReadOnlyList<ModelInfo> ParseProbeOutput(string stdout)
        => _spec.ModelProbeParser is { } parse ? parse(stdout) : [];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> InlineConfigEnvironment(string? modelId, IReadOnlyList<InlineMcpServer> mcpServers)
        => _spec.InlineConfig is { } build
            ? build(modelId, mcpServers)
            : new Dictionary<string, string>();

    public async Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        var process = StartProcess(options);
        var lifetime = new ProcessLifetime(process, _loggerFactory.CreateLogger<ProcessLifetime>());
        var connection = new AcpConnection(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            _loggerFactory.CreateLogger<AcpConnection>(),
            lifetime);
        try
        {
            var init = await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var session = await OpenSessionAsync(connection, init, options, cancellationToken).ConfigureAwait(false);
            return new ConnectionOwningSession(session, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resumes the agent's prior conversation when one was asked for and the agent says it can
    /// (<c>agentCapabilities.loadSession</c>), else starts a fresh one. Resuming is best-effort: an agent
    /// that advertises the capability but rejects this particular id (expired, pruned, or from another
    /// machine) must still yield a working session, so a failed load falls back to <c>session/new</c> —
    /// losing the history is bad, failing to open at all is worse.
    /// </summary>
    private static async Task<AcpAgentSession> OpenSessionAsync(
        AcpConnection connection,
        Wire.AcpInitializeResult init,
        AgentSessionOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ResumeSessionId is { Length: > 0 } resumeId && init.AgentCapabilities?.LoadSession == true)
        {
            try
            {
                return await connection.LoadSessionAsync(resumeId, options.WorkingDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to a new session; the connection is still healthy (a rejected load is an
                // ordinary JSON-RPC error response, not a transport fault).
            }
        }

        return await connection.NewSessionAsync(options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
    }

    private Process StartProcess(AgentSessionOptions options)
    {
        // When a sandbox is set, run the agent inside it (e.g. `incus exec … -- agent`) instead of
        // on the host; the agent's stdin/stdout flow through the exec pipe unchanged. The guest
        // working directory travels inside the wrapped argv, so the host launcher must use a real
        // host directory, not the guest path.
        var (command, arguments) = (_spec.Command, BuildAgentArguments(_spec, options));
        var hostWorkingDirectory = options.WorkingDirectory;
        if (options.Sandbox is { } sandbox)
        {
            (command, arguments) = sandbox.WrapCommand(command, arguments, options.WorkingDirectory);
            hostWorkingDirectory = Environment.CurrentDirectory;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = hostWorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ApplyEnvironment(startInfo, _spec.Environment);
        // Model-selection env for the host path. A sandboxed launch scrubs this (the run wrapper's
        // `env -i`), so there the same variables are materialized into the guest agent-env file instead —
        // see SessionManager.AddSandboxModel.
        ApplyEnvironment(startInfo, BuildAgentEnvironment(_spec, options));
        ApplyEnvironment(startInfo, options.Environment);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start agent process '{_spec.Command}'.");
        return process;
    }

    private static void ApplyEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? env)
    {
        if (env is null)
        {
            return;
        }

        foreach (var (key, value) in env)
        {
            startInfo.Environment[key] = value;
        }
    }

    /// <summary>Owns the agent process: pumps its stderr to the log and kills it on dispose.</summary>
    private sealed class ProcessLifetime : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ILogger _logger;

        public ProcessLifetime(Process process, ILogger logger)
        {
            _process = process;
            _logger = logger;
            _ = PumpStandardErrorAsync();
        }

        private async Task PumpStandardErrorAsync()
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    _logger.LogDebug("[agent stderr] {Line}", line);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "stderr pump ended");
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error terminating agent process");
            }

            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Ties the agent session's lifetime to the underlying connection/process.</summary>
    private sealed class ConnectionOwningSession(IAgentSession inner, IAsyncDisposable owner) : IAgentSession
    {
        public string AgentSessionId => inner.AgentSessionId;
        public ChannelReader<SessionEvent> Events => inner.Events;

        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
            => inner.PromptAsync(content, cancellationToken);

        public Task CancelAsync(CancellationToken cancellationToken = default)
            => inner.CancelAsync(cancellationToken);

        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
            => inner.RespondToPermissionAsync(requestId, optionId, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
