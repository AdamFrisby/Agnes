using System.Diagnostics;
using Agnes.Abstractions;
using Agnes.Agents.Native;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Pi;

/// <summary>How to launch Pi in its RPC mode (<c>pi --mode rpc</c>).</summary>
public sealed record PiOptions
{
    /// <summary>The Pi executable (resolved on PATH by default; the npm package installs it as <c>pi</c>).</summary>
    public string Command { get; init; } = "pi";

    /// <summary>Arguments that start Pi as an RPC server over stdio.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["--mode", "rpc"];

    /// <summary>Extra environment variables for the agent — where a provider API key goes, since Pi reads
    /// every provider credential from the environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Runs <c>pi --list-models</c> and returns its stdout, or null when the CLI can't be asked.
    /// Injectable so catalogue parsing is testable without spawning a process; null uses the real CLI.</summary>
    public Func<CancellationToken, Task<string?>>? ModelLister { get; init; }
}

/// <summary>
/// Agent plugin for Pi (<c>@earendil-works/pi-coding-agent</c>). Pi does <b>not</b> speak ACP — verified
/// against v0.84.3, whose only output modes are <c>text</c>, <c>json</c> and <c>rpc</c> — so this rides the
/// native stream-json adapter instead, with a mapper for Pi's RPC protocol. That protocol is bidirectional
/// newline-delimited JSON over stdio, which is exactly what <see cref="NativeStreamAdapter"/> already drives.
///
/// <para>The reason to reach for Pi at all is durability: it retries a failed provider call at the
/// agent-turn level, keeping the conversation and tool history intact, and only then gives up. See
/// <see cref="PiStreamMapper"/> for how that is threaded through so a retried turn doesn't read as a
/// finished one.</para>
/// </summary>
public static class PiAgent
{
    public const string AdapterId = "pi";

    public static AgentDescriptor Descriptor { get; } = new()
    {
        Id = AdapterId,
        DisplayName = "Pi",
    };

    /// <summary>Pi selects a model with <c>--model &lt;provider/id&gt;</c>.</summary>
    public static IReadOnlyList<string> BuildModelArguments(string modelId) => ["--model", modelId];

    /// <summary>
    /// Pi resumes with <c>--session-id &lt;id&gt;</c> — "use this exact project session, creating it if it
    /// doesn't exist" — not the <c>--resume</c> every other CLI Agnes drives takes. <c>--resume</c> on Pi
    /// opens an interactive picker, which would wedge a headless launch.
    /// </summary>
    public static IReadOnlyList<string> BuildResumeArguments(string sessionId) => ["--session-id", sessionId];

    /// <summary>Pi appends to its system prompt with <c>--append-system-prompt &lt;text&gt;</c>. (Threaded by
    /// the launch spec only if the native adapter grows a system-prompt hook; stated here because it is the
    /// flag, and a caller composing argv by hand needs the right one.)</summary>
    public static IReadOnlyList<string> BuildSystemPromptArguments(string systemPrompt)
        => ["--append-system-prompt", systemPrompt];

    /// <summary>The column headers <c>pi --list-models</c> prints above its table. Rows are only read after
    /// this line, because an unauthenticated CLI answers with prose instead — and prose splits into columns
    /// just as happily as a table does ("No models available." would otherwise read as a model
    /// <c>No/models</c>).</summary>
    private static readonly string[] TableHeader = ["provider", "model"];

    /// <summary>
    /// Parses <c>pi --list-models</c> output: a whitespace-aligned table headed
    /// <c>provider model context max-out thinking images</c>. The id Pi's <c>--model</c> flag wants is
    /// <c>provider/model</c>, so that is what a <see cref="ModelInfo"/> carries. Output with no table at all
    /// yields an empty list — read as "couldn't determine", so the picker falls back rather than offering
    /// nonsense.
    /// </summary>
    public static IReadOnlyList<ModelInfo> ParseModels(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inTable = false;
        foreach (var raw in stdout.Split('\n'))
        {
            var columns = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 2)
            {
                continue;
            }

            if (!inTable)
            {
                inTable = columns.Take(TableHeader.Length).SequenceEqual(TableHeader, StringComparer.Ordinal);
                continue; // the header itself is not a model
            }

            var (provider, model) = (columns[0], columns[1]);
            if (!IsIdentifier(provider) || !IsIdentifier(model))
            {
                continue;
            }

            var id = $"{provider}/{model}";
            if (seen.Add(id))
            {
                models.Add(new ModelInfo(id, $"{model} ({provider})"));
            }
        }

        return models;
    }

    private static bool IsIdentifier(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    public static NativeLaunchSpec CreateLaunchSpec(PiOptions? options = null)
    {
        options ??= new PiOptions();
        var lister = options.ModelLister ?? (ct => RunListModelsAsync(options.Command, ct));
        return new NativeLaunchSpec
        {
            Command = options.Command,
            Arguments = options.Arguments,
            Environment = options.Environment,
            Descriptor = Descriptor,
            Mapper = new PiStreamMapper(),
            // No static catalogue: which models Pi offers is exactly which providers the user has
            // credentials for, so a baked-in list would offer models it cannot reach.
            LiveModelProbe = async ct => ParseModels(await lister(ct).ConfigureAwait(false)),
            ModelArguments = BuildModelArguments,
            ResumeArguments = BuildResumeArguments,
            // No McpConfigFlag: Pi ships no MCP client at all, by explicit design ("No MCP. Build CLI tools
            // with READMEs, or build an extension that adds MCP support."). Agnes's MCP catalogue therefore
            // has nothing to bind to on this adapter — stating a flag that doesn't exist would be worse
            // than admitting the gap.
        };
    }

    public static PiAgentAdapter Create(ILoggerFactory loggerFactory, PiOptions? options = null)
        => new(CreateLaunchSpec(options), loggerFactory);

    /// <summary>Shells out to <c>pi --list-models</c>. Returns null on any failure so the catalogue falls
    /// back rather than surfacing an error — a missing or unauthenticated CLI is a normal state.</summary>
    private static async Task<string?> RunListModelsAsync(string command, CancellationToken cancellationToken)
    {
        if (!AgentCommand.IsOnPath(command))
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                ArgumentList = { "--list-models" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Pi's adapter: the generic <see cref="NativeStreamAdapter"/> plus the one thing Agnes must decide for
/// itself — what to do about a session that asked for per-tool approval on a CLI that cannot ask.
/// </summary>
public sealed class PiAgentAdapter : NativeStreamAdapter
{
    /// <summary>What an attended session is told. Names the actual constraint and the two ways out, because
    /// "unsupported" without either is a dead end.</summary>
    internal const string AttendedNotSupported =
        "Pi has no per-tool permission system: it runs every tool with the permissions of the process that " +
        "launched it, and exposes no way to ask before a tool call. Agnes will not present an attended " +
        "session it cannot actually gate. Turn on autonomous mode for this session — ideally with a sandbox, " +
        "which is the boundary Pi's own documentation recommends — or use an agent that implements the ACP " +
        "permission protocol (Claude Code, Copilot, OpenCode).";

    public PiAgentAdapter(NativeLaunchSpec spec, ILoggerFactory loggerFactory) : base(spec, loggerFactory)
    {
    }

    /// <summary>
    /// Fails closed on an attended session. Every other adapter treats <c>SkipPermissions == false</c> as
    /// "ask the user before each tool call"; Pi cannot, so honouring the request literally would mean
    /// running a session unguarded while the UI showed the guarded state. Refusing is the honest outcome —
    /// the same stance <c>OpenCodeNativeAgent</c> takes towards a sandbox it can't provide.
    /// </summary>
    public override Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        => options.SkipPermissions
            ? base.StartSessionAsync(options, cancellationToken)
            : throw new NotSupportedException(AttendedNotSupported);
}
