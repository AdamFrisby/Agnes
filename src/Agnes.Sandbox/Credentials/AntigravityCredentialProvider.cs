using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Credentials;

/// <summary>
/// Supplies Google Antigravity credentials to a sandbox: the OAuth bundle the <c>agy</c> CLI reads from
/// <c>~/.gemini/antigravity-cli/antigravity-oauth-token</c>.
///
/// <para><b>Why this ships the refresh token when the Claude provider deliberately does not.</b>
/// <see cref="ClaudeCredentialProvider"/> strips <c>refresh_token</c> because Anthropic's are single-use:
/// a VM that refreshed would invalidate the host's copy. Antigravity's bundle cannot be treated the same
/// way, and the reason is observable rather than assumed — on this host the stored
/// <c>token.access_token</c> had been expired for <b>69 days</b> while <c>agy models</c> kept working.
/// The CLI therefore refreshes from <c>refresh_token</c> at run time and does not write the new access
/// token back to this file. Shipping the sanitised half would ship a credential that is already dead.</para>
///
/// <para>The residual risk is real and worth naming: the guest holds a long-lived Google refresh token
/// for the duration of the session. That is the same trade the orchestrator makes, and it is why
/// Antigravity sessions belong in a sandbox that is destroyed afterwards rather than a long-lived one.</para>
/// </summary>
public sealed class AntigravityCredentialProvider : IAgentCredentialProvider
{
    /// <summary>Where the CLI keeps its OAuth bundle, relative to the home directory.</summary>
    internal const string RelativeTokenPath = ".gemini/antigravity-cli/antigravity-oauth-token";

    /// <summary>
    /// Carries the bundle's raw contents into the guest for a runner that materialises it itself. Named
    /// to match the orchestrator's variable so one sandbox image serves both.
    /// </summary>
    internal const string OAuthCredsEnvVar = "CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON";

    private readonly ILogger<AntigravityCredentialProvider> _logger;

    public AntigravityCredentialProvider(ILogger<AntigravityCredentialProvider> logger) => _logger = logger;

    public bool Handles(string adapterId)
        => string.Equals(adapterId, "antigravity", StringComparison.Ordinal);

    public async Task<SandboxCredential> GetAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        var env = new Dictionary<string, string>();
        var files = new List<SandboxCredentialFile>();

        var tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), RelativeTokenPath);

        try
        {
            if (File.Exists(tokenPath))
            {
                var raw = await File.ReadAllTextAsync(tokenPath, cancellationToken).ConfigureAwait(false);
                if (LooksLikeCredential(raw))
                {
                    files.Add(new SandboxCredentialFile(RelativeTokenPath, raw));
                    env[OAuthCredsEnvVar] = raw;
                }
                else
                {
                    // A file that is present but unreadable is worth saying, because the failure it
                    // causes downstream ("agy is not authenticated") points nowhere near here.
                    _logger.LogWarning(
                        "Antigravity credentials at {Path} are not a recognisable OAuth bundle; sending nothing.",
                        tokenPath);
                }
            }
            else
            {
                _logger.LogInformation(
                    "No Antigravity credentials at {Path}; the sandbox will start unauthenticated.", tokenPath);
            }
        }
        catch (Exception ex)
        {
            // Never fail sandbox creation over credentials: an unauthenticated agent fails with a clear
            // message of its own, whereas a failed launch says nothing about why.
            _logger.LogWarning(ex, "Couldn't read Antigravity credentials from {Path}.", tokenPath);
        }

        return new SandboxCredential { Files = files, EnvironmentVariables = env };
    }

    /// <summary>
    /// Whether the bundle has the shape the CLI expects — a <c>token</c> object carrying a
    /// <c>refresh_token</c>. Checked structurally rather than by parsing into a record, because this is
    /// a proprietary file Agnes does not own and only needs to recognise.
    /// </summary>
    internal static bool LooksLikeCredential(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.Object
                && token.TryGetProperty("refresh_token", out var refresh)
                && refresh.ValueKind == JsonValueKind.String
                && refresh.GetString() is { Length: > 0 };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
