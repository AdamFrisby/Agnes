namespace Agnes.Host.Hosting;

/// <summary>Parses a git remote URL into the host + "owner/repo" used to scope a credential grant.</summary>
public static class GitRemote
{
    /// <summary>
    /// Handles the three forms git remotes take: https://host/owner/repo(.git),
    /// ssh://git@host/owner/repo(.git), and the scp-like git@host:owner/repo(.git). Returns false for
    /// anything without a clear host + owner/repo (so we don't grant an unscoped credential).
    /// </summary>
    public static bool TryParse(string? url, out string host, out string repo)
    {
        host = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        url = url.Trim();
        string path;
        if (!url.Contains("://") && url.Contains('@') && url.Contains(':'))
        {
            // scp-like: [user@]host:owner/repo(.git)
            var at = url.IndexOf('@');
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
