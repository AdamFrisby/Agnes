using Agnes.Abstractions;

namespace Agnes.Host.Hosting;

/// <summary>
/// One authentication bootstrap method the host advertises to clients (pairing code, GitHub device-flow,
/// keypair, …). Exposed as a plugin-point (AC13) so the set of advertised methods flows through the same
/// <see cref="Agnes.Abstractions.IPluginRegistry{TProvider}"/> as agents and sandboxes, and a new method
/// can be added as a built-in (or NuGet) plugin rather than by editing the <c>/auth/methods</c> endpoint.
/// The built-ins here are thin advertisers over the existing auth services (<see cref="DeviceRegistry"/>,
/// <see cref="GitHubIdentity"/>, <see cref="KeypairAuth"/>) — the token-issuing endpoints still delegate
/// to those services, so this migration changes how methods are *discovered*, not how tokens are minted.
/// </summary>
public interface IAuthMethodProvider
{
    /// <summary>Stable id, e.g. <c>pairing</c>, <c>github</c>, <c>keypair</c>.</summary>
    string MethodId { get; }

    /// <summary>Human-friendly label.</summary>
    string DisplayName { get; }

    /// <summary>Whether this method is currently usable on this host (configured + ready).</summary>
    bool IsEnabled { get; }

    /// <summary>Public, client-facing metadata for this method (e.g. a GitHub OAuth <c>clientId</c>).
    /// Never secrets — only values safe to hand an unauthenticated client.</summary>
    IReadOnlyDictionary<string, string> ClientMetadata { get; }

    /// <summary>Which real-world situation this method serves, so the client buckets it into the right UX
    /// group ("add this device" / "restore access" / "authorize a headless process") rather than one flat
    /// list. Defaults to <see cref="AuthFlowKind.NewDevice"/> — the common case — so existing providers
    /// compile unchanged.</summary>
    AuthFlowKind Kind => AuthFlowKind.NewDevice;
}

/// <summary>Built-in: short pairing-code sign-in, backed by <see cref="DeviceRegistry"/>.</summary>
public sealed class PairingAuthMethodProvider(DeviceRegistry devices) : IAuthMethodProvider
{
    public string MethodId => "pairing";
    public string DisplayName => "Pairing code";
    public bool IsEnabled => devices.PairingEnabled;
    public IReadOnlyDictionary<string, string> ClientMetadata => new Dictionary<string, string>();

    // Scan/enter a short code the already-trusted host shows — the canonical "add this device" flow.
    public AuthFlowKind Kind => AuthFlowKind.NewDevice;
}

/// <summary>Built-in: GitHub device-flow SSO, backed by <see cref="GitHubIdentity"/>.</summary>
public sealed class GitHubAuthMethodProvider(GitHubIdentity github) : IAuthMethodProvider
{
    public string MethodId => "github";
    public string DisplayName => "GitHub sign-in";
    public bool IsEnabled => github.Options.IsUsable;
    public IReadOnlyDictionary<string, string> ClientMetadata =>
        github.Options.IsUsable && github.Options.ClientId is { Length: > 0 } clientId
            ? new Dictionary<string, string> { ["clientId"] = clientId }
            : new Dictionary<string, string>();

    // Primary use is adding a device via GitHub identity; it's also valid for restoring access after losing
    // every device (it needs no already-trusted device to vouch), but we advertise the primary bucket.
    public AuthFlowKind Kind => AuthFlowKind.NewDevice;
}

/// <summary>Built-in: offline keypair (SSH authorized_keys style), backed by <see cref="KeypairAuth"/>.</summary>
public sealed class KeypairAuthMethodProvider(KeypairAuth keypair) : IAuthMethodProvider
{
    public string MethodId => "keypair";
    public string DisplayName => "Keypair";
    public bool IsEnabled => keypair.IsUsable;
    public IReadOnlyDictionary<string, string> ClientMetadata => new Dictionary<string, string>();

    // SSH authorized_keys style: a headless daemon/terminal proving a device-held key, not a person signing in.
    public AuthFlowKind Kind => AuthFlowKind.ConnectTerminal;
}
