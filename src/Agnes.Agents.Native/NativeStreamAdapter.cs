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

    /// <summary>Static model catalog surfaced via <see cref="IModelListingAdapter.StaticModels"/> (empty when
    /// this CLI has no selectable model axis, which suppresses the picker).</summary>
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];

    /// <summary>Builds the CLI arguments that select a model (e.g. <c>id =&gt; ["--model", id]</c>). Null when
    /// this CLI takes no model flag, so a requested <see cref="AgentSessionOptions.ModelId"/> is ignored.</summary>
    public Func<string, IReadOnlyList<string>>? ModelArguments { get; init; }

    /// <summary>Optional live model probe, for a CLI that can be asked what it can reach
    /// (see <see cref="IModelListingAdapter.ListModelsAsync"/>). Null means "no live listing" — resolution
    /// falls back to <see cref="Models"/>.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ModelInfo>?>>? LiveModelProbe { get; init; }

    /// <summary>Builds the CLI arguments that resume a prior conversation by id. Defaults to
    /// <c>--resume &lt;id&gt;</c>, which is what Claude Code takes; a CLI that spells it differently
    /// (Pi's <c>--session-id &lt;id&gt;</c>) states its own.</summary>
    public Func<string, IReadOnlyList<string>> ResumeArguments { get; init; } = id => ["--resume", id];

    /// <summary>CLI flag that loads an MCP config file (e.g. "--mcp-config"), or null if unsupported.</summary>
    public string? McpConfigFlag { get; init; }

    /// <summary>Classifies whether an agent error message is a recoverable credential fault for this CLI
    /// (see <see cref="IAgentAdapter.IsRecoverableCredentialFault"/>). Null when this CLI's credentials
    /// can't expire mid-session.</summary>
    public Func<string, bool>? CredentialFaultClassifier { get; init; }

    /// <summary>Probes the CLI's machine-local login state (see <see cref="IAgentAdapter.GetAuthStatusAsync"/>).
    /// Null when this CLI has no reliable login signal — the adapter then reports no auth status (no badge).</summary>
    public Func<CancellationToken, Task<ProviderAuthStatus?>>? AuthStatusProbe { get; init; }
}

/// <summary>
/// A generic <see cref="IAgentAdapter"/> that launches a coding CLI in its native stream-json mode
/// and drives it via a <see cref="INativeStreamMapper"/>. Mirrors the ACP adapter's process handling
/// but reads the CLI's JSONL stdout itself. Reusable across CLIs (Claude Code today; others next).
/// </summary>
public class NativeStreamAdapter : IAgentAdapter, IModelListingAdapter
{
    private readonly NativeLaunchSpec _spec;
    private readonly ILoggerFactory _loggerFactory;

    public NativeStreamAdapter(NativeLaunchSpec spec, ILoggerFactory loggerFactory)
    {
        _spec = spec;
        _loggerFactory = loggerFactory;
    }

    public AgentDescriptor Descriptor => _spec.Descriptor;

    public bool IsAvailable() => AgentCommand.IsOnPath(_spec.Command);

    public bool IsRecoverableCredentialFault(string errorMessage) => _spec.CredentialFaultClassifier?.Invoke(errorMessage) ?? false;

    // No standard machine-readable model-list call across these CLIs; a spec that supplies a probe of its
    // own gets a live catalogue, everything else falls back to the static list.
    public IReadOnlyList<ModelInfo> StaticModels => _spec.Models;

    public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default)
        => _spec.LiveModelProbe?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<ModelInfo>?>(null);

    public Task<ProviderAuthStatus?> GetAuthStatusAsync(CancellationToken cancellationToken = default)
        => _spec.AuthStatusProbe?.Invoke(cancellationToken) ?? Task.FromResult<ProviderAuthStatus?>(null);

    public virtual Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<NativeStreamAdapter>();
        var process = StartProcess(options);
        var lifetime = new ProcessLifetime(process, logger);
        var session = new NativeAgentSession(process.StandardOutput, process.StandardInput, _spec.Mapper, logger, lifetime);
        return Task.FromResult<IAgentSession>(session);
    }

    private Process StartProcess(AgentSessionOptions options)
    {
        // Base args + the permission model (ask-per-tool by default, or skip when the user opts in).
        var baseArgs = new List<string>(_spec.Arguments);
        baseArgs.AddRange(_spec.Mapper.PermissionLaunchArguments(options.SkipPermissions));

        // Resume a prior conversation (e.g. after a host restart), in whichever spelling this CLI takes.
        if (options.ResumeSessionId is { Length: > 0 } resumeId)
        {
            baseArgs.AddRange(_spec.ResumeArguments(resumeId));
        }

        // Select the model when the CLI takes one (e.g. claude --model <id>). A null/blank id means the
        // CLI's own default; an adapter with no ModelArguments leaves it untouched.
        if (options.ModelId is { Length: > 0 } modelId && _spec.ModelArguments is { } buildModel)
        {
            baseArgs.AddRange(buildModel(modelId));
        }

        // Load Agnes-managed MCP servers via the CLI's config-file flag (e.g. claude --mcp-config).
        if (!string.IsNullOrEmpty(_spec.McpConfigFlag) && !string.IsNullOrEmpty(options.McpConfigPath))
        {
            baseArgs.Add(_spec.McpConfigFlag);
            baseArgs.Add(options.McpConfigPath);
        }

        // When a sandbox is set, run the agent inside it (streams flow through the exec pipe).
        // The guest working directory travels inside the wrapped argv (e.g. `incus exec --cwd`);
        // the host launcher process must use a real host directory, not the guest path.
        var (command, arguments) = (_spec.Command, (IReadOnlyList<string>)baseArgs);
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
