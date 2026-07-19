namespace Agnes.Sandbox.Credentials;

/// <summary>
/// The credentials an agent needs inside a sandbox. <see cref="EnvironmentVariables"/> are set for
/// the agent; <see cref="Files"/> are materialised at their (home-relative) paths with mode 0600.
/// </summary>
public sealed record SandboxCredential
{
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<SandboxCredentialFile> Files { get; init; } = [];

    public static SandboxCredential Empty { get; } = new();
}

/// <summary>A credential file to write inside the sandbox (path is relative to the agent's $HOME).</summary>
public sealed record SandboxCredentialFile(string HomeRelativePath, string Contents);

/// <summary>Supplies the credentials for a given agent adapter (reads host creds, sanitises them).</summary>
public interface IAgentCredentialProvider
{
    /// <summary>The adapter ids this provider supplies credentials for (e.g. "claude-code").</summary>
    bool Handles(string adapterId);

    Task<SandboxCredential> GetAsync(string adapterId, CancellationToken cancellationToken = default);
}
