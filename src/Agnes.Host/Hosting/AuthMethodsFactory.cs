using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// Builds the <see cref="AuthMethods"/> wire shape from the registered <see cref="IAuthMethodProvider"/>
/// plugins — the single source of truth for what <c>GET /auth/methods</c> reports. Kept as a pure function
/// over the registry (no side effects, DI'd inputs) so the endpoint and its tests share one mapping.
/// </summary>
public static class AuthMethodsFactory
{
    public static AuthMethods Build(IPluginRegistry<IAuthMethodProvider> methods)
    {
        var github = methods.Find("github");
        var oidc = methods.Find("oidc");
        var mtls = methods.Find("mtls");

        // One descriptor per enabled method, carrying its AuthFlowKind so the client can bucket them.
        var flows = methods.All
            .Where(m => m.IsEnabled)
            .Select(m => new AuthMethodDescriptor(m.MethodId, m.DisplayName, m.Kind))
            .ToArray();

        return new AuthMethods(
            Pairing: methods.Find("pairing")?.IsEnabled ?? false,
            GitHub: github?.IsEnabled ?? false,
            GitHubClientId: (github?.IsEnabled ?? false) ? github!.ClientMetadata.GetValueOrDefault("clientId") : null,
            Keypair: methods.Find("keypair")?.IsEnabled ?? false,
            Oidc: oidc?.IsEnabled ?? false,
            OidcIssuer: (oidc?.IsEnabled ?? false) ? oidc!.ClientMetadata.GetValueOrDefault("issuer") : null,
            Mtls: mtls?.IsEnabled ?? false,
            Flows: flows);
    }
}
