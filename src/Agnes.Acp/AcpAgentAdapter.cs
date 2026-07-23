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

    /// <summary>Builds the CLI arguments that select a model id (e.g. <c>--model &lt;id&gt;</c>). Null means
    /// this CLI doesn't take a model flag, so a requested <see cref="AgentSessionOptions.ModelId"/> is ignored.</summary>
    public Func<string, IReadOnlyList<string>>? ModelArguments { get; init; }
}

/// <summary>
/// Generic <see cref="IAgentAdapter"/> for any ACP-compliant CLI. Agent plugins are
/// typically just an <see cref="AcpLaunchSpec"/> passed to this adapter. Not sealed so a plugin can subclass
/// it to add an optional capability its CLI supports (e.g. Claude Code adds <see cref="IMcpDiscoveryAdapter"/>)
/// without re-implementing the ACP launch/session plumbing.
/// </summary>
public class AcpAgentAdapter : IAgentAdapter, IModelListingAdapter
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
        if (options.ModelId is { Length: > 0 } modelId && spec.ModelArguments is { } build)
        {
            return [.. spec.Arguments, .. build(modelId)];
        }

        return spec.Arguments;
    }

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
            await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var session = await connection.NewSessionAsync(options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            return new ConnectionOwningSession(session, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
