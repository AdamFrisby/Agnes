namespace Agnes.Abstractions;

/// <summary>
/// A logical project, potentially checked out on more than one host (a laptop, a cloud box, a CI-adjacent
/// machine). Spans hosts and has no filesystem presence of its own — it is the "what project is this"
/// identity that a set of <see cref="Checkout"/>s across machines belong to. See
/// <c>.ideas/connectivity/05-multi-machine-workspace-model.md</c>.
/// </summary>
public sealed record Workspace(string Id, string DisplayName, string RepositoryUrl);

/// <summary>
/// One host's actual on-disk copy of a <see cref="Workspace"/> — a specific clone or git worktree, with its
/// own lifecycle (create / switch-branch / clean-up) independent of any other host's checkout of the same
/// workspace. <see cref="Branch"/> is the branch currently checked out (read live), null when unknown.
/// </summary>
public sealed record Checkout(string Id, string WorkspaceId, string HostId, string Path, string? Branch);

/// <summary>
/// Derives a stable <see cref="Workspace"/> identity from a repository URL, so the same repo checked out on
/// two hosts (through slightly different remote forms — https vs. scp-like, with or without a <c>.git</c>
/// suffix or trailing slash) normalizes to one workspace. This is the single normalizer shared by the host
/// (which tags each persisted checkout with the resulting id) and the client (which groups checkouts across
/// hosts by it) so the two never drift. Pure string logic — no git, no I/O.
/// </summary>
public static class WorkspaceIdentity
{
    /// <summary>
    /// The canonical workspace id for a repository URL: <c>host/owner/repo</c> (lowercased) when the URL
    /// parses as one of git's three remote forms (https/http/ssh URL, or scp-like <c>git@host:owner/repo</c>),
    /// otherwise the trimmed, lowercased URL with any trailing <c>/</c> or <c>.git</c> stripped. Never throws.
    /// </summary>
    public static string Normalize(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return string.Empty;
        }

        var url = repositoryUrl.Trim();
        if (TryParse(url, out var host, out var repo))
        {
            return $"{host}/{repo}".ToLowerInvariant();
        }

        // Unparseable (a local path, an unusual scheme): fall back to the URL itself, stripped and lowercased,
        // so two spellings of the same thing still collapse to one id.
        var fallback = url.TrimEnd('/');
        if (fallback.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            fallback = fallback[..^4];
        }

        return fallback.ToLowerInvariant();
    }

    /// <summary>A short, human-facing name for the workspace (the repo's leaf name, e.g. <c>Agnes</c>).</summary>
    public static string DisplayName(string? repositoryUrl)
    {
        var id = Normalize(repositoryUrl);
        if (id.Length == 0)
        {
            return "(unknown)";
        }

        var slash = id.LastIndexOf('/');
        return slash >= 0 && slash < id.Length - 1 ? id[(slash + 1)..] : id;
    }

    // The three forms git remotes take: https://host/owner/repo(.git), ssh://git@host/owner/repo(.git), and
    // the scp-like git@host:owner/repo(.git). Mirrors the host's GitRemote parser, kept here (in a dependency-
    // free assembly) so the client can normalize identically without referencing the host.
    private static bool TryParse(string url, out string host, out string repo)
    {
        host = string.Empty;
        repo = string.Empty;

        string path;
        if (!url.Contains("://", StringComparison.Ordinal) && url.Contains('@') && url.Contains(':'))
        {
            var at = url.IndexOf('@', StringComparison.Ordinal);
            var colon = url.IndexOf(':', at + 1);
            if (colon < 0)
            {
                return false;
            }

            host = url[(at + 1)..colon];
            path = url[(colon + 1)..];
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh")
        {
            host = uri.Host;
            path = uri.AbsolutePath.TrimStart('/');
        }
        else
        {
            return false;
        }

        repo = path.Trim('/');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return host.Length > 0 && repo.Contains('/');
    }
}
