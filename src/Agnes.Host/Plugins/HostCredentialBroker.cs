using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Plugins;

/// <summary>
/// The real <see cref="ICredentialBroker"/> a plugin gets when it declares (and is granted) the
/// <see cref="PluginCapabilityIds.Credentials"/> capability — thin wrapper over the same
/// <see cref="CredentialSourceRegistry"/> the git-credential broker already resolves against, so a
/// plugin and Agnes's own sandboxed-session git auth share one source of truth for linked accounts.
/// </summary>
public sealed class HostCredentialBroker(CredentialSourceRegistry sources) : ICredentialBroker
{
    public async Task<string?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        var source = sources.For(host);
        if (source is null)
        {
            return null;
        }

        var credential = await source.ResolveAsync(new CredentialRequest("https", host, Repo: null, "get"), cancellationToken).ConfigureAwait(false);
        return credential?.Password;
    }
}
