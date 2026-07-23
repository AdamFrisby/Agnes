using System.Text.Json;
using System.Text.Json.Serialization;
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
            AuthStatusProbe = _ => Task.FromResult<ProviderAuthStatus?>(ProbeAuthStatus(DefaultCredentialsPath)),
        }, loggerFactory);

    /// <summary>The host's Claude OAuth credentials file (<c>~/.claude/.credentials.json</c>).</summary>
    public static string DefaultCredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    /// <summary>Shape of Claude's on-disk <c>~/.claude/.credentials.json</c> (only the fields we read).</summary>
    private sealed record ClaudeCredentialsFile(
        [property: JsonPropertyName("claudeAiOauth")] ClaudeOAuth? ClaudeAiOauth);

    private sealed record ClaudeOAuth(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresAt")] long? ExpiresAt);

    /// <summary>
    /// Reads Claude's on-disk OAuth credentials file and reports login state. Logged-in = the file exists
    /// with a non-empty access token that hasn't expired (<c>expiresAt</c> is epoch milliseconds). A missing
    /// file, an absent/empty token, or an expired token is a confident "not logged in" for this CLI (its
    /// login state is authoritatively this file), each with a human-readable <see cref="ProviderAuthStatus.Issue"/>.
    /// The path is a parameter so tests can point it at a temp file.
    /// </summary>
    public static ProviderAuthStatus ProbeAuthStatus(string credentialsPath)
    {
        var now = DateTimeOffset.UtcNow;
        if (!File.Exists(credentialsPath))
        {
            return new ProviderAuthStatus(false, null, null, "Not signed in — run `claude login` on the host.", now);
        }

        try
        {
            var raw = File.ReadAllText(credentialsPath);
            var oauth = JsonSerializer.Deserialize<ClaudeCredentialsFile>(raw)?.ClaudeAiOauth;
            if (oauth?.AccessToken is not { Length: > 0 })
            {
                return new ProviderAuthStatus(false, null, null, "No Claude access token found — run `claude login` on the host.", now);
            }

            if (oauth.ExpiresAt is { } expiresAt && DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) <= now)
            {
                return new ProviderAuthStatus(false, "OAuth", "OAuth", "The Claude login token has expired — run `claude login` on the host.", now);
            }

            return new ProviderAuthStatus(true, "OAuth", "OAuth", null, now);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ProviderAuthStatus(false, null, null, "The Claude credentials file couldn't be read.", now);
        }
    }

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
