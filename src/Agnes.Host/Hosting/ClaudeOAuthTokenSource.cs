using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Hosting;

/// <summary>
/// Reads the Claude OAuth <b>access</b> token from the host's <c>~/.claude/.credentials.json</c> for the quota
/// probe. Deliberately reuses <see cref="ClaudeCredentialProvider.TrySanitise(string?, out string, out string)"/>
/// — the exact same parsing the sandbox credential provider already uses — so the credential-file format lives
/// in one place. It only ever returns the short-lived access token; the single-use refresh token is never read
/// or forwarded (the host CLI stays the sole refresher).
/// </summary>
/// <remarks>
/// The home directory is injected (defaulting to the current user's profile) so a test can point the reader at
/// a temp directory containing a canned credentials file — no real credentials and no network are touched.
/// </remarks>
public sealed class ClaudeOAuthTokenSource
{
    private readonly string _credentialsPath;

    /// <param name="homeDirectory">
    /// The home directory under which <c>.claude/.credentials.json</c> is read. When null/blank the current
    /// user's profile is used (production); tests pass a temp directory.
    /// </param>
    public ClaudeOAuthTokenSource(string? homeDirectory = null)
    {
        var home = string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;
        _credentialsPath = Path.Combine(home, ".claude", ".credentials.json");
    }

    /// <summary>
    /// The current Claude OAuth access token, or null when there is none to read (file missing, unreadable, or
    /// no access token in it). Never throws for these ordinary cases — the probe treats null as "no credential".
    /// </summary>
    public async Task<string?> ReadAccessTokenAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return null;
            }

            var raw = await File.ReadAllTextAsync(_credentialsPath, ct).ConfigureAwait(false);
            return ClaudeCredentialProvider.TrySanitise(raw, out _, out var accessToken) ? accessToken : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing/locked/garbled credentials file is an ordinary "no token" case, not a probe crash.
            return null;
        }
    }
}
