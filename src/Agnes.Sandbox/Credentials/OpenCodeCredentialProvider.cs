using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Credentials;

/// <summary>
/// Supplies OpenCode's provider credentials to a sandbox.
/// </summary>
/// <remarks>
/// Without this, a sandboxed OpenCode sees only the providers that need no credential — 7 free models at
/// the time of writing, versus 27 with the host's key — and it does <b>not</b> report the shortfall: asked
/// for a model it cannot reach, it silently streams from one it can. That turns a missing credential into
/// "the session quietly ran the wrong model", which is far worse than a visible failure.
///
/// The credential travels as <c>OPENCODE_AUTH_CONTENT</c> (OpenCode's inline-auth env var, the counterpart
/// of <c>OPENCODE_CONFIG_CONTENT</c>), so it rides the root-owned agent-env file and never lands in the
/// guest's filesystem. Note the honest tradeoff: unlike Claude's OAuth bundle — where the refresh token is
/// deliberately stripped because it is single-use and must stay host-side — a provider API key has no
/// reduced form that still works, so this forwards it whole. A sandboxed OpenCode session can therefore
/// read a key valid beyond that session; that is the price of the agent being able to reach the provider
/// at all, and it is why this is a per-adapter provider rather than something applied to every agent.
/// </remarks>
public sealed class OpenCodeCredentialProvider : IAgentCredentialProvider
{
    /// <summary>OpenCode's inline-auth env var — same mechanism as its inline config.</summary>
    internal const string AuthContentVariable = "OPENCODE_AUTH_CONTENT";

    private readonly ILogger<OpenCodeCredentialProvider> _logger;
    private readonly string _home;

    public OpenCodeCredentialProvider(ILogger<OpenCodeCredentialProvider> logger, string? homeDirectory = null)
    {
        _logger = logger;
        _home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public bool Handles(string adapterId) => string.Equals(adapterId, "opencode", StringComparison.Ordinal);

    public Task<SandboxCredential> GetAsync(string adapterId, CancellationToken cancellationToken = default)
        => Task.FromResult(Build(
            TryReadFile(Path.Combine(_home, ".local", "share", "opencode", "auth.json")),
            TryReadFile(Path.Combine(_home, ".local", "share", "opencode", "account.json")),
            Environment.GetEnvironmentVariable("OPENCODE_API_KEY")));

    /// <summary>Pure over its inputs so the shape of what crosses into the VM is testable without a
    /// filesystem: malformed or absent credentials contribute nothing rather than propagating junk.</summary>
    internal static SandboxCredential Build(string? authJson, string? accountJson, string? apiKey)
    {
        var env = new Dictionary<string, string>();
        var files = new List<SandboxCredentialFile>();

        if (IsJsonObject(authJson))
        {
            env[AuthContentVariable] = authJson!;
        }

        // A host-exported key wins: it is the explicit, per-run override.
        if (apiKey is { Length: > 0 })
        {
            env["OPENCODE_API_KEY"] = apiKey;
        }

        // account.json has no inline-env counterpart, so it must be a file. It carries which account is
        // active rather than the secret itself, and some providers' catalogues depend on it.
        if (IsJsonObject(accountJson))
        {
            files.Add(new SandboxCredentialFile(".local/share/opencode/account.json", accountJson!));
        }

        return new SandboxCredential { EnvironmentVariables = env, Files = files };
    }

    private static bool IsJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read host OpenCode credential file {Path}", path);
            return null;
        }
    }
}
