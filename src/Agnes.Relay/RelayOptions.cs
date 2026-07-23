namespace Agnes.Relay;

/// <summary>
/// Relay service configuration (deserialized from a small JSON file / env at startup, or built
/// directly in tests). Immutable record — the relay holds no other mutable global state.
/// </summary>
public sealed record RelayOptions
{
    /// <summary>Address the relay listens on for both host and client outbound connections.</summary>
    public string ListenAddress { get; init; } = "0.0.0.0";

    /// <summary>TCP port to listen on. <c>0</c> selects an ephemeral port (used by tests).</summary>
    public int Port { get; init; } = 5100;

    /// <summary>
    /// <c>authorized_keys</c>-style file of allowed host public keys — one base64 SPKI (P-256) per
    /// line, optional trailing label, <c>#</c> comments. Only keys listed here may claim a host-id.
    /// </summary>
    public string AuthorizedHostKeysFile { get; init; } = "";

    /// <summary>How long a client waits for its host to open the paired data connection.</summary>
    public TimeSpan DataConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a connection may take to complete the relay-protocol handshake before it is dropped.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Abuse controls (per-source-IP rate limit + concurrency cap + probing defense).</summary>
    public RelayRateLimitOptions RateLimit { get; init; } = new();

    /// <summary>
    /// The relay's own public DNS name. When set, the relay obtains a real Let's Encrypt certificate for it (via
    /// LettuceEncrypt, HTTP-01/TLS-ALPN-01 — the relay HAS a public address, so standard ACME works) and serves
    /// its broker port over TLS. Empty (default) = plain TCP, for local/testing. Setting this does NOT weaken the
    /// blind-pipe: the TLS'd connection carries only relay-protocol framing the relay already owns; the tunneled
    /// client↔host TLS inside it stays opaque.
    /// </summary>
    public string PublicDomain { get; init; } = "";

    /// <summary>Contact email registered with Let's Encrypt (required when <see cref="PublicDomain"/> is set).</summary>
    public string AcmeEmailAddress { get; init; } = "";

    /// <summary>Use the Let's Encrypt STAGING directory for the relay's own cert (testing a real domain).</summary>
    public bool AcmeUseStagingServer { get; init; }

    /// <summary>Directory LettuceEncrypt persists the relay's account + certificate to (survives restarts).</summary>
    public string AcmeCertificateDirectory { get; init; } = "";

    /// <summary>HTTP port LettuceEncrypt serves the HTTP-01 challenge on (default 80).</summary>
    public int AcmeHttpPort { get; init; } = 80;

    /// <summary>HTTPS port LettuceEncrypt answers the TLS-ALPN-01 challenge on (default 443).</summary>
    public int AcmeHttpsPort { get; init; } = 443;
}
