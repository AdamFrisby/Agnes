using System.Diagnostics;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Native;

/// <summary>Launch descriptor for a native stream-json CLI adapter.</summary>
public sealed record NativeLaunchSpec
{
    public required string Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public required AgentDescriptor Descriptor { get; init; }
    public required INativeStreamMapper Mapper { get; init; }
}

/// <summary>
/// A generic <see cref="IAgentAdapter"/> that launches a coding CLI in its native stream-json mode
/// and drives it via a <see cref="INativeStreamMapper"/>. Mirrors the ACP adapter's process handling
/// but reads the CLI's JSONL stdout itself. Reusable across CLIs (Claude Code today; others next).
/// </summary>
public sealed class NativeStreamAdapter : IAgentAdapter
{
    private readonly NativeLaunchSpec _spec;
    private readonly ILoggerFactory _loggerFactory;

    public NativeStreamAdapter(NativeLaunchSpec spec, ILoggerFactory loggerFactory)
    {
        _spec = spec;
        _loggerFactory = loggerFactory;
    }

    public AgentDescriptor Descriptor => _spec.Descriptor;

    public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<NativeStreamAdapter>();
        var process = StartProcess(options);
        var lifetime = new ProcessLifetime(process, logger);
        var session = new NativeAgentSession(process.StandardOutput, process.StandardInput, _spec.Mapper, logger, lifetime);
        return Task.FromResult<IAgentSession>(session);
    }

    private Process StartProcess(AgentSessionOptions options)
    {
        // When a sandbox is set, run the agent inside it (streams flow through the exec pipe).
        // The guest working directory travels inside the wrapped argv (e.g. `incus exec --cwd`);
        // the host launcher process must use a real host directory, not the guest path.
        var (command, arguments) = (_spec.Command, (IReadOnlyList<string>)_spec.Arguments);
        var hostWorkingDirectory = options.WorkingDirectory;
        if (options.Sandbox is { } sandbox)
        {
            (command, arguments) = sandbox.WrapCommand(command, arguments, options.WorkingDirectory);
            hostWorkingDirectory = Environment.CurrentDirectory;
        }

        var psi = new ProcessStartInfo(command)
        {
            WorkingDirectory = hostWorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (_spec.Environment is not null)
        {
            foreach (var (k, v) in _spec.Environment)
            {
                psi.Environment[k] = v;
            }
        }

        if (options.Environment is not null)
        {
            foreach (var (k, v) in options.Environment)
            {
                psi.Environment[k] = v;
            }
        }

        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{_spec.Command}'.");
    }

    /// <summary>Pumps stderr to the log and kills the process tree on dispose.</summary>
    private sealed class ProcessLifetime : IAsyncDisposable
    {
        private readonly Process _process;

        public ProcessLifetime(Process process, ILogger logger)
        {
            _process = process;
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    logger.LogDebug("[agent stderr] {Line}", line);
                }
            });
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
            catch
            {
                // already gone
            }

            return ValueTask.CompletedTask;
        }
    }
}
