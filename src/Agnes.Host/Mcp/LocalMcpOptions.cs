namespace Agnes.Host.Mcp;

/// <summary>
/// Where an agent running <b>on the host</b> (an unsandboxed session) reaches Agnes's own MCP endpoint.
/// </summary>
/// <remarks>
/// The main listener cannot serve this. It is TLS with a self-signed or pinned certificate — the deployment
/// Agnes is designed for — and an agent CLI has no way to be told to trust it; and it is authenticated by
/// *device* tokens, which carry the authority of a paired human across every session on the host and must
/// never be handed to an agent. So the same mechanism the sandbox bridge already uses is bound a second
/// time, on loopback: a plaintext listener whose port serves nothing but
/// <see cref="AgnesMcpEndpoints.Path"/>, reachable only from this machine, and accepting only the
/// per-session bearer tokens minted by <see cref="SessionMcpTokens"/>.
///
/// Plaintext is acceptable here for exactly the reasons it is on the bridge: the traffic never leaves the
/// loopback interface, the port serves no route that carries device authority, and the only credential that
/// crosses it is a token scoped to one session and revoked when that session closes.
/// </remarks>
public sealed record LocalMcpOptions
{
    /// <summary>The default loopback bind address. A fixed port rather than an ephemeral one so an agent
    /// config written for a session stays valid across a host restart.</summary>
    public const string DefaultBindUrl = "http://127.0.0.1:5117";

    /// <summary>The URL an agent on this host dials, e.g. <c>http://127.0.0.1:5117/mcp-agnes</c>.
    /// Null when the listener is disabled — no endpoint, and no <c>agnes</c> server offered locally.</summary>
    public string? Url { get; init; }

    /// <summary>The address:port the host binds for it, e.g. <c>http://127.0.0.1:5117</c>.</summary>
    public string? BindUrl { get; init; }

    /// <summary>The MCP URL implied by a bind address, or null when the address is unusable.</summary>
    public static string? UrlFor(string? bindUrl)
        => Uri.TryCreate(bindUrl, UriKind.Absolute, out var uri)
            ? new Uri(uri, AgnesMcpEndpoints.Path).ToString()
            : null;
}
