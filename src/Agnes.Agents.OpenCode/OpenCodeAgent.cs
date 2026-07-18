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
}

/// <summary>
/// Reference agent plugin for OpenCode. Unlike Claude Code, OpenCode ships native ACP,
/// so this is just a launch descriptor over the generic <see cref="AcpAgentAdapter"/>.
/// </summary>
public static class OpenCodeAgent
{
    public const string AdapterId = "opencode";

    public static AgentDescriptor Descriptor { get; } = new()
    {
        Id = AdapterId,
        DisplayName = "OpenCode",
    };

    public static AcpLaunchSpec CreateLaunchSpec(OpenCodeOptions? options = null)
    {
        options ??= new OpenCodeOptions();
        return new AcpLaunchSpec
        {
            Command = options.Command,
            Arguments = options.Arguments,
            Environment = options.Environment,
            Descriptor = Descriptor,
        };
    }

    public static AcpAgentAdapter Create(ILoggerFactory loggerFactory, OpenCodeOptions? options = null)
        => new(CreateLaunchSpec(options), loggerFactory);
}
