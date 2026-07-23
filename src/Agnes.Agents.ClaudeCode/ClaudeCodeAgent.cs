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

    /// <summary>Claude Code's currently selectable models. These are stable CLI aliases (the <c>claude</c> CLI
    /// resolves each to its latest concrete model), so they don't go stale as new dated model ids ship; a user
    /// who wants a specific dated id types it as a custom entry (<see cref="ModelInfo.IsCustomEntryAllowed"/>).</summary>
    public static IReadOnlyList<ModelInfo> StaticModels { get; } =
    [
        new ModelInfo("sonnet", "Claude Sonnet (latest)"),
        new ModelInfo("opus", "Claude Opus (latest)"),
        new ModelInfo("haiku", "Claude Haiku (latest)"),
    ];

    /// <summary>Claude Code selects a model with <c>--model &lt;id&gt;</c>.</summary>
    public static IReadOnlyList<string> BuildModelArguments(string modelId) => ["--model", modelId];

    public static AcpLaunchSpec CreateLaunchSpec(ClaudeCodeOptions? options = null)
    {
        options ??= new ClaudeCodeOptions();
        return new AcpLaunchSpec
        {
            Command = options.Command,
            Arguments = options.Arguments,
            Environment = options.Environment,
            Descriptor = Descriptor,
            // No standard ACP model-list call, so ship the static list and fall back to it (LiveModelProbe null).
            Models = StaticModels,
            ModelArguments = BuildModelArguments,
        };
    }

    public static AcpAgentAdapter Create(ILoggerFactory loggerFactory, ClaudeCodeOptions? options = null)
        => new(CreateLaunchSpec(options), loggerFactory);
}
