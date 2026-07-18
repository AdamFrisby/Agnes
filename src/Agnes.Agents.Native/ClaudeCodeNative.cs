using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Native;

/// <summary>
/// Builds a native-SDK adapter for Claude Code (stream-json), offered alongside the ACP adapter.
/// Flags are a sensible default and configurable at registration — they and the permission/cancel
/// control protocol should be tuned against the installed <c>claude</c> CLI.
/// </summary>
public static class ClaudeCodeNative
{
    public const string AdapterId = "claude-code-native";

    public static readonly AgentDescriptor Descriptor = new()
    {
        Id = AdapterId,
        DisplayName = "Claude Code (native)",
    };

    public static readonly string[] DefaultArguments =
        ["--output-format", "stream-json", "--input-format", "stream-json", "--verbose"];

    public static NativeStreamAdapter Create(ILoggerFactory loggerFactory, string? command = null, IReadOnlyList<string>? arguments = null)
        => new(new NativeLaunchSpec
        {
            Command = command ?? "claude",
            Arguments = arguments ?? DefaultArguments,
            Descriptor = Descriptor,
            Mapper = new ClaudeCodeStreamMapper(),
        }, loggerFactory);
}
