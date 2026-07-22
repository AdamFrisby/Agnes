namespace Agnes.Host.Hosting;

/// <summary>
/// A git forge (GitHub, GitLab, Bitbucket, …) the host knows how to work with. Exposed as a plugin-point
/// (AC13) so forge integration flows through the same <see cref="Agnes.Abstractions.IPluginRegistry{TProvider}"/>
/// as agents and sandboxes: GitHub is the built-in today, and another forge can be added as a built-in or
/// NuGet plugin. <see cref="Matches"/> identifies which forge owns a given remote host (parsed generically
/// by <see cref="GitRemote"/>), which is the seam a forge-specific feature (PR listing/checkout, richer
/// credential brokering) resolves against instead of hardcoding "github".
/// </summary>
public interface IGitHostProvider
{
    /// <summary>Stable id, e.g. <c>github</c>.</summary>
    string Id { get; }

    /// <summary>Human-friendly name.</summary>
    string DisplayName { get; }

    /// <summary>Whether this forge owns the given remote host (e.g. <c>github.com</c>).</summary>
    bool Matches(string remoteHost);
}

/// <summary>Built-in provider for GitHub (github.com and GitHub Enterprise Cloud subdomains). This is the
/// existing GitHub integration expressed as a registered forge plugin.</summary>
public sealed class GitHubGitHostProvider : IGitHostProvider
{
    public string Id => "github";
    public string DisplayName => "GitHub";

    public bool Matches(string remoteHost)
        => remoteHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           || remoteHost.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
}
