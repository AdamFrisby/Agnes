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
            CredentialFaultClassifier = IsRecoverableCredentialFault,
        }, loggerFactory);

    /// <summary>
    /// A revoked/expired Claude OAuth token surfaces as an agent error; a sandboxed claude can't refresh in
    /// place (its token is baked into the launch env), so the host relaunches it with fresh credentials.
    /// This recognizes those messages. It lives here (with the Claude adapter) rather than in the host, so
    /// adding another agent whose token can expire doesn't mean editing the orchestrator.
    /// </summary>
    public static bool IsRecoverableCredentialFault(string message)
    {
        var m = message.ToLowerInvariant();
        return m.Contains("oauth", StringComparison.Ordinal)
            || m.Contains("token has been revoked", StringComparison.Ordinal)
            || m.Contains("authentication_error", StringComparison.Ordinal)
            || m.Contains("invalid bearer token", StringComparison.Ordinal)
            || (m.Contains("401", StringComparison.Ordinal) && (m.Contains("auth", StringComparison.Ordinal) || m.Contains("token", StringComparison.Ordinal)));
    }
}
