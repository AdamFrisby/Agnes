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
}

/// <summary>
/// Reference agent plugin for OpenCode. Unlike Claude Code, OpenCode ships native ACP, so this is mostly a
/// launch descriptor over the generic <see cref="AcpAgentAdapter"/> — plus the model axis, which OpenCode
/// expresses differently from every other agent Agnes drives.
/// </summary>
public static class OpenCodeAgent
{
    public const string AdapterId = "opencode";

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
        [property: JsonPropertyName("mcp")] IReadOnlyDictionary<string, RemoteMcp>? Mcp);

    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// OpenCode is configured through <c>OPENCODE_CONFIG_CONTENT</c>, not argv: <c>opencode acp</c> accepts
    /// no <c>--model</c> flag (only <c>opencode run</c> does), and Agnes writes it no config file. Model and
    /// MCP servers therefore share this single overlay — emitting them as two variables would mean the
    /// second silently replaced the first.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildConfigEnvironment(
        string? modelId, IReadOnlyList<InlineMcpServer> mcpServers)
    {
        var mcp = mcpServers.Count == 0
            ? null
            : mcpServers.ToDictionary(
                s => s.Name,
                s => new RemoteMcp("remote", s.Url,
                    s.AuthorizationHeader is { Length: > 0 } auth
                        ? new Dictionary<string, string> { ["Authorization"] = auth }
                        : null),
                StringComparer.Ordinal);

        var model = string.IsNullOrEmpty(modelId) ? null : modelId;
        if (model is null && mcp is null)
        {
            return new Dictionary<string, string>(); // nothing to say — leave the CLI's own config alone
        }

        return new Dictionary<string, string>
        {
            [ConfigContentVariable] = JsonSerializer.Serialize(new InlineConfig(model, mcp), OmitNulls),
        };
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
            Environment = options.Environment,
            Descriptor = Descriptor,
            // No static catalogue: the model list is per-account, so a stale hard-coded list would offer
            // models the user can't actually reach. Live probe only, degrading to "no picker".
            LiveModelProbe = async ct => ParseModels(await lister(ct).ConfigureAwait(false)),
            InlineConfig = BuildConfigEnvironment,
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
