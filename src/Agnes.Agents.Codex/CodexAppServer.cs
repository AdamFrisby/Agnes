using System.Diagnostics;
using System.Threading.Channels;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Codex;

/// <summary>Launch descriptor for the Codex app-server adapter.</summary>
public sealed record CodexLaunchSpec
{
    public string Command { get; init; } = "codex";
    public IReadOnlyList<string> Arguments { get; init; } = ["app-server"];
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public AgentDescriptor Descriptor { get; init; } = CodexAppServer.Descriptor;
}

/// <summary>
/// A native Codex adapter that drives <c>codex app-server</c> — Codex's persistent JSON-RPC stdio
/// server — over one long-lived process per session. Unlike <c>codex exec</c> (single-turn), the
/// app-server keeps a thread alive across turns, matching Agnes's persistent-session model. Streams
/// map through <see cref="CodexMap"/>; approvals surface as permission requests.
/// </summary>
public sealed class CodexAppServerAdapter : IAgentAdapter
{
    private readonly CodexLaunchSpec _spec;
    private readonly ILoggerFactory _loggerFactory;

    public CodexAppServerAdapter(CodexLaunchSpec spec, ILoggerFactory loggerFactory)
    {
        _spec = spec;
        _loggerFactory = loggerFactory;
    }

    public AgentDescriptor Descriptor => _spec.Descriptor;

    public bool IsAvailable() => AgentCommand.IsOnPath(_spec.Command);

    public async Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        var process = StartProcess(options);
        var lifetime = new ProcessLifetime(process, _loggerFactory.CreateLogger<CodexAppServerAdapter>());
        var connection = new CodexConnection(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            _loggerFactory.CreateLogger<CodexConnection>(),
            lifetime);
        try
        {
            await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);

            // Ask-per-tool by default (Codex sends approval requests); autonomous opts out of prompts.
            var approvalPolicy = options.SkipPermissions ? "never" : "on-request";
            var sandbox = options.SkipPermissions && options.Sandbox is not null ? "danger-full-access" : "workspace-write";

            var session = await connection.StartThreadAsync(options.WorkingDirectory, approvalPolicy, sandbox, cancellationToken).ConfigureAwait(false);

            // Disposing the session tears down the connection (and kills the app-server process).
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
        // When a sandbox is set, run the agent inside it (streams flow through the exec pipe). The
        // guest working directory travels in thread/start's cwd, so the host launcher uses a real
        // host directory, not the guest path.
        var (command, arguments) = (_spec.Command, (IReadOnlyList<string>)_spec.Arguments);
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
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ApplyEnvironment(startInfo, _spec.Environment);
        ApplyEnvironment(startInfo, options.Environment);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Codex app-server '{_spec.Command}'.");
    }

    private static void ApplyEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }
    }

    /// <summary>Wraps the session so disposing it also disposes the owning connection (and process).</summary>
    private sealed class ConnectionOwningSession(IAgentSession inner, IAsyncDisposable owner) : IAgentSession
    {
        public string AgentSessionId => inner.AgentSessionId;
        public ChannelReader<SessionEvent> Events => inner.Events;

        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
            => inner.PromptAsync(content, cancellationToken);

        public Task CancelAsync(CancellationToken cancellationToken = default) => inner.CancelAsync(cancellationToken);

        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
            => inner.RespondToPermissionAsync(requestId, optionId, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
        }
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
                    logger.LogDebug("[codex stderr] {Line}", line);
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

/// <summary>Registers the native Codex (app-server) adapter — mirrors <c>ClaudeCodeNative</c>.</summary>
public static class CodexAppServer
{
    public const string AdapterId = "codex";

    public static readonly AgentDescriptor Descriptor = new()
    {
        Id = AdapterId,
        DisplayName = "Codex",
    };

    /// <summary>The config override that turns on Codex's structured "ask the user" questions
    /// (<c>item/tool/requestUserInput</c>). The flag is "under development" upstream — opt-in only.</summary>
    internal const string UserInputConfigArg = "features.default_mode_request_user_input=true";

    /// <param name="enableUserInput">Prepend <c>-c features.default_mode_request_user_input=true</c> so the
    /// app-server surfaces structured questions. Experimental — off by default.</param>
    public static CodexAppServerAdapter Create(
        ILoggerFactory loggerFactory,
        string? command = null,
        IReadOnlyList<string>? arguments = null,
        bool enableUserInput = false)
    {
        var args = arguments ?? ["app-server"];
        if (enableUserInput && !args.Any(a => a.Contains("default_mode_request_user_input", StringComparison.Ordinal)))
        {
            args = ["-c", UserInputConfigArg, .. args];
        }

        return new(new CodexLaunchSpec
        {
            Command = command ?? "codex",
            Arguments = args,
            Descriptor = Descriptor,
        }, loggerFactory);
    }
}
