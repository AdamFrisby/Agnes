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

    // --print is required for headless stream-json mode (without it the CLI starts its interactive
    // TUI and emits nothing on a pipe). --input-format stream-json keeps ONE process alive across
    // turns (a persistent session), so we never respawn or --resume. Sandboxed launches also add
    // --dangerously-skip-permissions (the VM is the boundary); that stays out of the host default.
    public static readonly string[] DefaultArguments =
        ["--print", "--output-format", "stream-json", "--input-format", "stream-json", "--verbose"];

    public static NativeStreamAdapter Create(ILoggerFactory loggerFactory, string? command = null, IReadOnlyList<string>? arguments = null)
        => new(new NativeLaunchSpec
        {
            Command = command ?? "claude",
            Arguments = arguments ?? DefaultArguments,
            Descriptor = Descriptor,
            Mapper = new ClaudeCodeStreamMapper(),
            McpConfigFlag = "--mcp-config",
        }, loggerFactory);
}
