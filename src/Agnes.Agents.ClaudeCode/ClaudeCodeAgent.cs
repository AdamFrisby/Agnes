using Agnes.Abstractions;
using Agnes.Acp;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.ClaudeCode;

/// <summary>How to launch Claude Code's ACP endpoint. Defaults to the published ACP bridge.</summary>
public sealed record ClaudeCodeOptions
{
    /// <summary>Executable that hosts the Claude Code ACP bridge.</summary>
    public string Command { get; init; } = "npx";

    /// <summary>Arguments that start the bridge in ACP mode.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["-y", "@zed-industries/claude-code-acp"];

    /// <summary>Extra environment variables (e.g. credentials) for the agent.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// Reference agent plugin for Claude Code. It is deliberately thin: all ACP behavior
/// lives in <see cref="AcpAgentAdapter"/>; this only supplies the launch descriptor.
/// </summary>
public static class ClaudeCodeAgent
{
    public const string AdapterId = "claude-code";

    public static AgentDescriptor Descriptor { get; } = new()
    {
        Id = AdapterId,
        DisplayName = "Claude Code",
    };

    public static AcpLaunchSpec CreateLaunchSpec(ClaudeCodeOptions? options = null)
    {
        options ??= new ClaudeCodeOptions();
        return new AcpLaunchSpec
        {
            Command = options.Command,
            Arguments = options.Arguments,
            Environment = options.Environment,
            Descriptor = Descriptor,
        };
    }

    public static AcpAgentAdapter Create(ILoggerFactory loggerFactory, ClaudeCodeOptions? options = null)
        => new(CreateLaunchSpec(options), loggerFactory);
}
