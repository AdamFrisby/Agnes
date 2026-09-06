namespace Agnes.Host.Mcp;

/// <summary>
/// Pure helpers for Agnes's plaintext MCP listeners — the sandbox-bridge one and the loopback one
/// (<see cref="LocalMcpOptions"/>). Separated out because the two decisions they encode are the ones that
/// would be dangerous to get wrong and impossible to unit-test inside <c>Program</c>: adding a listener
/// without dropping the main one, and refusing every path but MCP on a plaintext port.
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

    /// <summary>
    /// Every port that must be restricted to the MCP path: the sandbox-bridge listener and the loopback one
    /// alike. There is more than one plaintext listener now, and a gate written against a single port would
    /// silently serve the hub in the clear on whichever one it forgot.
    /// </summary>
    public static IReadOnlySet<int> RestrictedPorts(params string?[] bindUrls)
    {
        var ports = new HashSet<int>();
        foreach (var url in bindUrls)
        {
            if (TryGetPort(url) is { } port)
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    /// <summary>Which of Kestrel's two mutually-exclusive endpoint channels a plaintext MCP listener must be
    /// added through, given how the host's main listener is configured.</summary>
    public enum ListenerChannel
    {
        /// <summary>Append to the hosting addresses (<c>ASPNETCORE_URLS</c> / the <c>urls</c> setting).</summary>
        HostingUrls,

        /// <summary>Add an explicit <c>KestrelServerOptions.Listen</c> endpoint.</summary>
        KestrelEndpoint,

        /// <summary>Neither — adding an endpoint at all would displace the main listener. Don't bind.</summary>
        None,
    }

    /// <summary>
    /// Picks the channel that <b>adds</b> a listener instead of replacing the host's main one.
    /// </summary>
    /// <remarks>
    /// Kestrel takes endpoints from two places and they do not merge symmetrically — verified empirically,
    /// because getting this wrong silently unbinds the listener every client uses:
    /// <list type="bullet">
    /// <item>With <c>Kestrel:Endpoints</c> configured (Agnes's own dev config), configured endpoints win and
    /// the hosting addresses are ignored outright — so <c>UseUrls</c> here would bind nothing at all, while
    /// an explicit <c>Listen</c> is added alongside them.</item>
    /// <item>With only hosting addresses (the Docker image's <c>ASPNETCORE_URLS=…:5081</c>), the reverse: an
    /// explicit <c>Listen</c> makes Kestrel ignore those addresses, and the host would come up serving
    /// nothing but MCP. Appending to the address list is what composes.</item>
    /// <item>With neither, Kestrel's own default endpoint applies only while both channels are empty, so
    /// using either one suppresses it. Agnes declines to bind rather than displace the main listener —
    /// set <c>ASPNETCORE_URLS</c> or <c>Kestrel:Endpoints</c> and the MCP listeners come up with it.</item>
    /// </list>
    /// </remarks>
    public static ListenerChannel ChooseChannel(string? hostingUrls, bool kestrelEndpointsConfigured)
        => kestrelEndpointsConfigured ? ListenerChannel.KestrelEndpoint
            : !string.IsNullOrWhiteSpace(hostingUrls) ? ListenerChannel.HostingUrls
            : ListenerChannel.None;

    /// <summary>The address and port to <c>Listen</c> on for a bind URL, or null when it names neither an IP
    /// nor a wildcard we can resolve without guessing.</summary>
    public static (System.Net.IPAddress Address, int Port)? TryGetEndpoint(string? bindUrl)
    {
        if (!Uri.TryCreate(bindUrl, UriKind.Absolute, out var uri) || uri.IsDefaultPort)
        {
            return null;
        }

        var host = uri.Host;
        var address =
            System.Net.IPAddress.TryParse(host, out var parsed) ? parsed
            : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ? System.Net.IPAddress.Loopback
            : host is "*" or "+" ? System.Net.IPAddress.Any
            : null;

        return address is null ? null : (address, uri.Port);
    }

    /// <summary>
    /// Whether a TCP port on loopback can be bound right now. Advisory only (nothing stops a race), but it
    /// turns "another Agnes already owns this default port" from a fatal Kestrel start-up failure into a
    /// feature that declines to start — running a second host on one machine must not be a hard error.
    /// </summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>Whether a path may be served on the guest port. Only the MCP endpoint and paths beneath it:
    /// the hub, the REST API and the web head all carry device tokens and must never be served in plaintext,
    /// even on a route that only sandboxes can reach.</summary>
    public static bool IsAllowedPath(string? path)
        => path is not null
           && (string.Equals(path, AgnesMcpEndpoints.Path, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(AgnesMcpEndpoints.Path + "/", StringComparison.OrdinalIgnoreCase));
}
