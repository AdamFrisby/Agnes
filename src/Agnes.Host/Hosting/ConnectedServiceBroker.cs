using Agnes.Abstractions;

namespace Agnes.Host.Hosting;

/// <summary>
/// The single reuse point for materialising a connected-service credential. Given a stored profile id, it
/// looks the profile up in the <see cref="ConnectedServiceProfileStore"/>, finds the matching registered
/// <see cref="IConnectedServiceProvider"/> by <see cref="ConnectedServiceProfile.ProviderId"/>, and calls
/// its <see cref="IConnectedServiceProvider.ResolveAsync"/>. Any feature that needs a provider credential
/// (an agent-CLI launch, a future integration) calls this instead of a bespoke path — and a new provider
/// added through the plugin registry is selectable with NO change here, because routing is purely by id.
/// </summary>
/// <remarks>
/// This is the connected-services analogue of the sandbox git-credential broker
/// (<c>Agnes.Host.Hosting.CredentialBroker</c>): host holds the real secret, the broker hands back only a
/// short-lived resolved credential. It is deliberately parallel to — not a replacement of — that existing
/// broker.
/// </remarks>
public sealed class ConnectedServiceBroker
{
    private readonly ConnectedServiceProfileStore _profiles;
    private readonly IPluginRegistry<IConnectedServiceProvider> _providers;

    public ConnectedServiceBroker(
        ConnectedServiceProfileStore profiles,
        IPluginRegistry<IConnectedServiceProvider> providers)
    {
        _profiles = profiles;
        _providers = providers;
    }

    /// <summary>The stored profiles, as safe-to-share identity records (names/labels only, never secrets).
    /// This is what a hub method exposing "which profiles are configured" returns.</summary>
    public IReadOnlyList<ConnectedServiceProfile> ListProfiles() => _profiles.List();

    /// <summary>
    /// Resolves the short-lived credential for the stored profile with id <paramref name="profileId"/>.
    /// Throws <see cref="KeyNotFoundException"/> when no such profile exists, and
    /// <see cref="InvalidOperationException"/> when the profile references a provider id that no registered
    /// <see cref="IConnectedServiceProvider"/> handles. A provider's own resolve failure (not connected,
    /// expired/unrefreshable) surfaces as whatever exception that provider throws — never a silent empty.
    /// </summary>
    public Task<ResolvedServiceCredential> ResolveAsync(string profileId, CancellationToken ct = default)
    {
        var profile = _profiles.Find(profileId)
            ?? throw new KeyNotFoundException($"No connected-service profile with id '{profileId}'.");

        var provider = _providers.Find(profile.ProviderId)
            ?? throw new InvalidOperationException(
                $"No connected-service provider registered for id '{profile.ProviderId}' (profile '{profileId}').");

        return provider.ResolveAsync(profile, ct);
    }
}
