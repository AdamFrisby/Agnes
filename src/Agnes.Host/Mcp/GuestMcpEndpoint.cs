namespace Agnes.Host.Mcp;

/// <summary>
/// Pure helpers for the bridge-local MCP listener. Separated out because the two decisions they encode are
/// the ones that would be dangerous to get wrong and impossible to unit-test inside <c>Program</c>: adding a
/// listener without dropping the main one, and refusing every path but MCP on the plaintext port.
/// </summary>
public static class GuestMcpEndpoint
{
    /// <summary>
    /// Adds <paramref name="extra"/> to whatever the host is already configured to listen on.
    /// </summary>
    /// <remarks>
    /// Appending matters: Kestrel takes the URL list wholesale, so calling <c>UseUrls</c> with only the guest
    /// address would silently unbind the main TLS listener — the host would come up "fine" and be reachable
    /// by nothing but the sandboxes. A duplicate is dropped rather than bound twice.
    /// </remarks>
    public static string CombineUrls(string? existing, string extra)
    {
        var urls = (existing ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!urls.Contains(extra, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(extra);
        }

        return string.Join(';', urls);
    }

    /// <summary>The port of a bind URL, or null when it isn't a usable absolute URL with an explicit port.</summary>
    public static int? TryGetPort(string? bindUrl)
        => Uri.TryCreate(bindUrl, UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : null;

    /// <summary>Whether a path may be served on the guest port. Only the MCP endpoint and paths beneath it:
    /// the hub, the REST API and the web head all carry device tokens and must never be served in plaintext,
    /// even on a route that only sandboxes can reach.</summary>
    public static bool IsAllowedPath(string? path)
        => path is not null
           && (string.Equals(path, AgnesMcpEndpoints.Path, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(AgnesMcpEndpoints.Path + "/", StringComparison.OrdinalIgnoreCase));
}
