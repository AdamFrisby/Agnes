using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Credentials;

/// <summary>
/// Supplies Claude Code credentials for the sandbox, ported from CodeyBox. Reads the host's
/// <c>~/.claude/.credentials.json</c> and builds a <b>sanitised</b> bundle — accessToken + expiresAt
/// only, <b>never the refresh_token</b>. Anthropic's OAuth refresh tokens are single-use, so shipping
/// one into a VM would race the host <c>claude</c> CLI and invalidate one party; keeping it host-side
/// makes the host CLI the sole refresher. Also forwards ANTHROPIC_API_KEY / CLAUDE_CODE_OAUTH_TOKEN.
/// </summary>
public sealed class ClaudeCredentialProvider : IAgentCredentialProvider
{
    private static readonly string[] AdapterIds = ["claude-code", "claude-code-native"];
    private readonly ILogger<ClaudeCredentialProvider> _logger;

    public ClaudeCredentialProvider(ILogger<ClaudeCredentialProvider> logger) => _logger = logger;

    public bool Handles(string adapterId) => Array.IndexOf(AdapterIds, adapterId) >= 0;

    public async Task<SandboxCredential> GetAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        var env = new Dictionary<string, string>();
        foreach (var name in new[] { "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                env[name] = value;
            }
        }

        var files = new List<SandboxCredentialFile>();
        var credsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        try
        {
            if (File.Exists(credsPath))
            {
                var raw = await File.ReadAllTextAsync(credsPath, cancellationToken).ConfigureAwait(false);
                if (TrySanitise(raw, out var bundle, out var accessToken))
                {
                    // The access token in CLAUDE_CODE_OAUTH_TOKEN is what actually authenticates the
                    // in-VM claude CLI (it does not honour a materialised .credentials.json). Set it
                    // unless the host already exported one above. Still materialise the sanitised file
                    // (refresh_token stripped) as a secondary, forward-compatible artifact.
                    if (!env.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"))
                    {
                        env["CLAUDE_CODE_OAUTH_TOKEN"] = accessToken;
                    }

                    files.Add(new SandboxCredentialFile(".claude/.credentials.json", bundle));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read host Claude credentials");
        }

        return new SandboxCredential { EnvironmentVariables = env, Files = files };
    }

    /// <summary>Keeps accessToken + expiresAt; drops refresh_token and everything else.</summary>
    internal static bool TrySanitise(string? rawContents, out string sanitisedBundle)
        => TrySanitise(rawContents, out sanitisedBundle, out _);

    /// <summary>Keeps accessToken + expiresAt; drops refresh_token. Also returns the raw access token.</summary>
    internal static bool TrySanitise(string? rawContents, out string sanitisedBundle, out string accessToken)
    {
        sanitisedBundle = string.Empty;
        accessToken = string.Empty;
        if (string.IsNullOrEmpty(rawContents))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var tokenEl)
                || tokenEl.ValueKind != JsonValueKind.String
                || tokenEl.GetString() is not { Length: > 0 } token)
            {
                return false;
            }

            accessToken = token;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("claudeAiOauth");
                writer.WriteStartObject();
                writer.WriteString("accessToken", token);
                if (oauth.TryGetProperty("expiresAt", out var expiresAt))
                {
                    writer.WritePropertyName("expiresAt");
                    expiresAt.WriteTo(writer); // verbatim (number or string)
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            sanitisedBundle = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
