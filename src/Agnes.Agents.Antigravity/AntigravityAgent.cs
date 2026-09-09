using System.Diagnostics;
using Agnes.Abstractions;
using Agnes.Agents.Native;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Antigravity;

/// <summary>How to launch Google Antigravity (<c>agy</c>) as a stream-json peer.</summary>
public sealed record AntigravityOptions
{
    /// <summary>The Antigravity executable. The installer puts it at <c>~/.local/bin/agy</c>.</summary>
    public string Command { get; init; } = "agy";

    /// <summary>
    /// Arguments that run <c>agy</c> as a persistent NDJSON peer.
    ///
    /// <para>Note what is <b>not</b> here: <c>--print</c>. In agy 1.1.24 that flag takes a value
    /// (<c>--print='prompt'</c>), so passing it bare swallows the next argument as the prompt — the CLI
    /// says so outright: <i>"--print took \"--dangerously-skip-permissions\" as its prompt"</i>.
    /// <c>--input-format stream-json</c> selects print mode on its own.</para>
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } =
        ["--input-format", "stream-json", "--output-format", "stream-json"];

    /// <summary>
    /// Per-response wait. agy defaults to 5 minutes and aborts the whole session with "timed out waiting
    /// for response" when a single turn exceeds it — which a real coding turn does. Go duration syntax.
    /// </summary>
    public TimeSpan PrintTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Runs <c>agy models</c> and returns stdout, or null when the CLI can't be asked. Injectable
    /// so catalogue parsing is testable without spawning a process.</summary>
    public Func<CancellationToken, Task<string?>>? ModelLister { get; init; }
}

/// <summary>
/// Agent plugin for Google Antigravity, the successor to gemini-cli.
///
/// <para>Antigravity ships no ACP, so this rides <see cref="NativeStreamAdapter"/> with a mapper for its
/// own stream protocol — the same route Pi takes. What it does offer, and what makes it worth first-class
/// support rather than one-shot invocation, is <c>--input-format stream-json</c>: a persistent process
/// that takes one NDJSON message per turn and keeps the conversation in memory between them. Verified
/// against agy 1.1.24 — turn 2 answered from turn 1's context with the same conversation id.</para>
///
/// <para><b>Autonomous only, and not by preference.</b> Antigravity has no permission protocol, and
/// omitting <c>--dangerously-skip-permissions</c> does not make it ask: it silently redirects writes to a
/// scratch directory and reports success. An attended session is therefore refused outright — see
/// <see cref="AntigravityAgentAdapter"/>.</para>
/// </summary>
public static class AntigravityAgent
{
    public const string AdapterId = "antigravity";

    public static AgentDescriptor Descriptor { get; } = new()
    {
        Id = AdapterId,
        DisplayName = "Antigravity",
    };

    /// <summary>Antigravity selects a model with <c>--model &lt;id&gt;</c>.</summary>
    public static IReadOnlyList<string> BuildModelArguments(string modelId) => ["--model", modelId];

    /// <summary>
    /// Resumes with <c>--conversation &lt;id&gt;</c>, using the id from the <c>init</c> frame. Not
    /// <c>--continue</c>, which resumes whatever ran last in this working directory — fine for a batch
    /// runner with one conversation per checkout, wrong for a client that may hold several sessions
    /// against the same repository at once.
    /// </summary>
    public static IReadOnlyList<string> BuildResumeArguments(string sessionId) => ["--conversation", sessionId];

    /// <summary>
    /// <c>--add-dir &lt;cwd&gt;</c> — the flag without which Antigravity does not edit your code.
    ///
    /// <para>Given no workspace, <c>agy</c> writes files to <c>~/.gemini/antigravity-cli/scratch/</c> and
    /// reports success. This is <b>not</b> the permission behaviour: it happens with
    /// <c>--dangerously-skip-permissions</c> set. Verified in a clean Incus guest — the identical prompt
    /// wrote to the scratch directory without this flag and to the working directory with it.</para>
    ///
    /// <para>It only looked correct on the developer host because that machine had accumulated
    /// Antigravity workspace state for the directory being used. A fresh sandbox has none, which is
    /// precisely the case that matters. CodeyBox passes neither this nor <c>--new-project</c>.</para>
    /// </summary>
    public static IReadOnlyList<string> BuildWorkingDirectoryArguments(string workingDirectory)
        => ["--add-dir", workingDirectory];

    /// <summary>
    /// Parses <c>agy models</c>, whose output is <c>id\tDisplay Name</c> per line after a status header.
    /// </summary>
    internal static IReadOnlyList<ModelInfo> ParseModels(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var columns = line.Split('\t', StringSplitOptions.TrimEntries);
            if (columns.Length < 2 || !IsIdentifier(columns[0]))
            {
                // Skips the "Fetching available models..." header without matching on its text, which
                // would break the moment the wording changes.
                continue;
            }

            if (seen.Add(columns[0]))
            {
                models.Add(new ModelInfo(columns[0], columns[1]));
            }
        }

        return models;
    }

    private static bool IsIdentifier(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/');

    public static NativeLaunchSpec CreateLaunchSpec(AntigravityOptions? options = null)
    {
        options ??= new AntigravityOptions();
        var lister = options.ModelLister ?? (ct => RunListModelsAsync(options.Command, ct));

        var arguments = new List<string>(options.Arguments);
        if (options.PrintTimeout > TimeSpan.Zero)
        {
            arguments.Add("--print-timeout");
            arguments.Add($"{(long)options.PrintTimeout.TotalSeconds}s");
        }

        return new NativeLaunchSpec
        {
            Command = options.Command,
            Arguments = arguments,
            // Bare `agy` is its interactive TUI, which is exactly what the console button should attach to.
            ConsoleArguments = [],
            Environment = options.Environment,
            Descriptor = Descriptor,
            Mapper = new AntigravityStreamMapper(),
            // No static catalogue: the gateway's model list changes without a CLI release (this host was
            // offering Gemini 3.8 while CodeyBox's baked-in list still named 3.5), so a hardcoded list
            // would offer models that no longer resolve.
            LiveModelProbe = async ct => ParseModels(await lister(ct).ConfigureAwait(false)),
            ModelArguments = BuildModelArguments,
            ResumeArguments = BuildResumeArguments,
            WorkingDirectoryArguments = BuildWorkingDirectoryArguments,
        };
    }

    public static AntigravityAgentAdapter Create(ILoggerFactory loggerFactory, AntigravityOptions? options = null)
        => new(CreateLaunchSpec(options), loggerFactory);

    /// <summary>Shells out to <c>agy models</c>. Returns null on any failure, because a missing or
    /// unauthenticated CLI is a normal state rather than an error to surface.</summary>
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
                ArgumentList = { "models" },
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
/// Antigravity's adapter: the generic native adapter, plus the one decision Agnes has to make for
/// itself — what to do with a session that asked to approve each tool call on a CLI that cannot ask.
/// </summary>
public sealed class AntigravityAgentAdapter : NativeStreamAdapter
{
    /// <summary>
    /// What an attended session is told. It names the specific failure rather than "unsupported", because
    /// the failure mode here is not a missing feature but a misleading one.
    /// </summary>
    internal const string AttendedNotSupported =
        "Antigravity has no per-tool permission protocol, and running it without " +
        "--dangerously-skip-permissions does not make it ask. Verified against agy 1.1.24: it silently " +
        "redirects file writes to ~/.gemini/antigravity-cli/scratch/ and reports success, so the session " +
        "would show edits that never reached your working directory. Agnes will not present an attended " +
        "session it cannot gate, still less one that would quietly do nothing. Turn on autonomous mode for " +
        "this session — inside a sandbox, which is the only real boundary Antigravity has — or use an agent " +
        "that implements the ACP permission protocol (Claude Code, Copilot, OpenCode).";

    public AntigravityAgentAdapter(NativeLaunchSpec spec, ILoggerFactory loggerFactory) : base(spec, loggerFactory)
    {
    }

    public override Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        => options.SkipPermissions
            ? base.StartSessionAsync(options, cancellationToken)
            : throw new NotSupportedException(AttendedNotSupported);
}
