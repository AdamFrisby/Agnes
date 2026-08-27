using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Agnes.Acp;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.OpenCode;

/// <summary>How to launch OpenCode's native ACP server (<c>opencode acp</c>).</summary>
public sealed record OpenCodeOptions
{
    /// <summary>The OpenCode executable (resolved on PATH by default).</summary>
    public string Command { get; init; } = "opencode";

    /// <summary>Arguments that start OpenCode as an ACP server over stdio.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["acp"];

    /// <summary>Extra environment variables for the agent.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Runs <c>opencode models</c> and returns its stdout, or null when the CLI can't be asked.
    /// Injectable so the catalogue parsing is testable without spawning a process; null uses the real CLI.</summary>
    public Func<CancellationToken, Task<string?>>? ModelLister { get; init; }

    /// <summary>How deep subagents may nest. OpenCode defaults to 1, so a subagent cannot spawn its own —
    /// every branch of the work funnels back through the one root agent. Raising it lets a subagent split
    /// its own batch, which is where the fan-out actually multiplies.</summary>
    public int SubagentDepth { get; init; } = 3;

    /// <summary>Let the agent run subagents in the background. Without it OpenCode's task tool blocks until
    /// each subagent finishes, so subagents run strictly one at a time however many the agent asks for —
    /// which is what a live session showed: 33 task calls, zero overlap.</summary>
    public bool BackgroundSubagents { get; init; } = true;
}

/// <summary>
/// Reference agent plugin for OpenCode. Unlike Claude Code, OpenCode ships native ACP, so this is mostly a
/// launch descriptor over the generic <see cref="AcpAgentAdapter"/> — plus the model axis, which OpenCode
/// expresses differently from every other agent Agnes drives.
/// </summary>
public static class OpenCodeAgent
{
    public const string AdapterId = "opencode";

    /// <summary>
    /// Opts the agent into background subagents. OpenCode's task tool refuses <c>background: true</c>
    /// without it ("Background subagents require OPENCODE_EXPERIMENTAL_BACKGROUND_SUBAGENTS=true"), and with
    /// it the tool gains that parameter and the guidance telling the model it will be notified on
    /// completion. It is the difference between subagents that queue and subagents that accumulate.
    /// Experimental upstream, hence the name — but it is the only way to run many at once.
    /// </summary>
    internal const string BackgroundSubagentsVariable = "OPENCODE_EXPERIMENTAL_BACKGROUND_SUBAGENTS";

    /// <summary>The env var OpenCode reads an inline config from. It is <b>merged</b> over the config files
    /// (verified against v1.18: keys it omits keep their file values), so pinning the model this way leaves
    /// the user's own <c>opencode.json</c> otherwise intact.</summary>
    internal const string ConfigContentVariable = "OPENCODE_CONFIG_CONTENT";

    public static AgentDescriptor Descriptor { get; } = new()
    {
        Id = AdapterId,
        DisplayName = "OpenCode",
    };

    /// <summary>One MCP server as OpenCode's config expects it: <c>{"type":"remote","url":…,"headers":…}</c>.
    /// Property names are pinned explicitly — the default serializer emits C# casing, which OpenCode
    /// silently ignores.</summary>
    private sealed record RemoteMcp(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers);

    /// <summary>The inline config Agnes overlays. Typed rather than hand-built JSON so the shape is checked
    /// at compile time even though the schema belongs to OpenCode. Null members are omitted, so an overlay
    /// only ever states what Agnes actually wants to set.</summary>
    private sealed record InlineConfig(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("mcp")] IReadOnlyDictionary<string, RemoteMcp>? Mcp,
        [property: JsonPropertyName("subagent_depth")] int? SubagentDepth);

    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// OpenCode is configured through <c>OPENCODE_CONFIG_CONTENT</c>, not argv: <c>opencode acp</c> accepts
    /// no <c>--model</c> flag (only <c>opencode run</c> does), and Agnes writes it no config file. Model and
    /// MCP servers therefore share this single overlay — emitting them as two variables would mean the
    /// second silently replaced the first.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildConfigEnvironment(
        string? modelId,
        IReadOnlyList<InlineMcpServer> mcpServers,
        bool backgroundSubagents = true,
        int? subagentDepth = null)
    {
        var env = new Dictionary<string, string>();
        if (backgroundSubagents)
        {
            env[BackgroundSubagentsVariable] = "true";
        }

        var mcp = mcpServers.Count == 0
            ? null
            : mcpServers.ToDictionary(
                s => s.Name,
                s => new RemoteMcp("remote", s.Url,
                    s.AuthorizationHeader is { Length: > 0 } auth
                        ? new Dictionary<string, string> { ["Authorization"] = auth }
                        : null),
                StringComparer.Ordinal);

        var depth = subagentDepth is > 1 ? subagentDepth : null; // 1 is OpenCode's own default: say nothing
        var model = string.IsNullOrEmpty(modelId) ? null : modelId;
        if (model is not null || mcp is not null || depth is not null)
        {
            // Only state an overlay when there is something to say — an empty one would still override
            // the user's own config with "nothing".
            env[ConfigContentVariable] = JsonSerializer.Serialize(new InlineConfig(model, mcp, depth), OmitNulls);
        }

        return env;
    }

    /// <summary>
    /// Parses <c>opencode models</c> output — one <c>provider/model</c> id per line. The catalogue depends on
    /// which providers the user has authenticated, so there is no meaningful static fallback: an unauthenticated
    /// or absent CLI yields an empty list and the client simply shows no picker.
    /// </summary>
    public static IReadOnlyList<ModelInfo> ParseModels(string? stdout)
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
            // Ids are always provider/model; anything else is a banner, warning or blank line.
            if (line.Length == 0 || line.IndexOf('/') <= 0 || line.Contains(' ', StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(line))
            {
                models.Add(new ModelInfo(line, line));
            }
        }

        return models;
    }

    public static AcpLaunchSpec CreateLaunchSpec(OpenCodeOptions? options = null)
    {
        options ??= new OpenCodeOptions();
        var lister = options.ModelLister ?? (ct => RunModelsAsync(options.Command, ct));
        return new AcpLaunchSpec
        {
            Command = options.Command,
            Arguments = options.Arguments,
            // Bare `opencode` is its interactive TUI; `acp` is the flagged mode. Explicitly empty, not null
            // — null would mean this CLI offers no console at all.
            ConsoleArguments = [],
            Environment = options.Environment,
            Descriptor = Descriptor,
            // No static catalogue: the model list is per-account, so a stale hard-coded list would offer
            // models the user can't actually reach. Live probe only, degrading to "no picker".
            LiveModelProbe = async ct => ParseModels(await lister(ct).ConfigureAwait(false)),
            InlineConfig = (model, mcp) => BuildConfigEnvironment(
                model, mcp, options.BackgroundSubagents, options.SubagentDepth),
            // The same command, run wherever the agent actually lives. A sandboxed OpenCode without the
            // host's provider key sees only the credential-free models and will quietly substitute one, so
            // the selected model has to be checked against the catalogue the agent itself can see.
            ModelProbeArguments = ["models"],
            ModelProbeParser = ParseModels,
        };
    }

    public static AcpAgentAdapter Create(ILoggerFactory loggerFactory, OpenCodeOptions? options = null)
        => new(CreateLaunchSpec(options), loggerFactory);

    /// <summary>Shells out to <c>opencode models</c>. Returns null on any failure so the catalogue falls back
    /// rather than surfacing an error — a missing or unauthenticated CLI is a normal state, not a fault.</summary>
    private static async Task<string?> RunModelsAsync(string command, CancellationToken cancellationToken)
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
