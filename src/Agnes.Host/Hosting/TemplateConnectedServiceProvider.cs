using Agnes.Abstractions;

namespace Agnes.Host.Hosting;

/// <summary>
/// REFERENCE STUB — copy this shape to add a real connected-service provider (GitHub, Linear, an OAuth
/// subscription, a plain API key, …). It resolves a placeholder short-lived credential from host config/env
/// with NO network call, so the plugin surface is exercised end-to-end before any real vendor integration
/// exists.
/// </summary>
/// <remarks>
/// <para>To wire a REAL provider, copy this class to e.g. <c>GitHubConnectedServiceProvider</c> and:</para>
/// <list type="number">
/// <item>Give it a distinct <see cref="Id"/> (e.g. "github") and <see cref="DisplayName"/>.</item>
/// <item>In <see cref="ResolveAsync"/>, replace the config lookup with the real token exchange: read the
/// long-lived secret this provider owns host-side (an OAuth refresh token, a stored API key) keyed by
/// <see cref="ConnectedServiceProfile.Id"/>, and perform the exchange (e.g. OAuth refresh) HERE, on the
/// host — the machine the user already trusts to run the agent, so no separate trusted relay is needed.</item>
/// <item>Return ONLY the short-lived resolved credential. NEVER put the refresh token (or any other
/// long-lived secret) into <see cref="ResolvedServiceCredential.Value"/> or its <c>Env</c> — forwarding a
/// single-use refresh token would race the host's own CLI and invalidate one side.</item>
/// <item>THROW a clear, actionable exception when the credential can't be resolved (not connected, expired
/// and unrefreshable, revoked) rather than returning an empty value — a silent empty would let an
/// unauthenticated CLI launch masquerade as success.</item>
/// <item>Register it in <c>Program.cs</c> as another <c>AddSingleton&lt;IConnectedServiceProvider&gt;</c>.
/// The <see cref="ConnectedServiceBroker"/> needs NO change — it routes purely by provider id.</item>
/// </list>
/// </remarks>
public sealed class TemplateConnectedServiceProvider : IConnectedServiceProvider
{
    /// <summary>The provider id this stub answers to. A real provider uses its own (e.g. "github").</summary>
    public const string ProviderId = "template";

    private readonly Func<string, string?> _secretLookup;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;

    /// <param name="secretLookup">
    /// How the stub fetches its placeholder secret for a given profile id (production wires this to host
    /// config/env; tests pass a fake). A REAL provider would instead read its own host-side secret store
    /// here and perform a token exchange. Returns null when nothing is configured for that profile.
    /// </param>
    /// <param name="time">Clock used to stamp <see cref="ResolvedServiceCredential.ExpiresAt"/> (injectable for tests).</param>
    /// <param name="lifetime">How long a materialised credential is considered valid (short by design).</param>
    public TemplateConnectedServiceProvider(
        Func<string, string?>? secretLookup = null,
        TimeProvider? time = null,
        TimeSpan? lifetime = null)
    {
        _secretLookup = secretLookup ?? (_ => "template-placeholder-token");
        _time = time ?? TimeProvider.System;
        _lifetime = lifetime ?? TimeSpan.FromHours(1);
    }

    public string Id => ProviderId;

    public string DisplayName => "Template (reference stub)";

    public Task<ResolvedServiceCredential> ResolveAsync(ConnectedServiceProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // A REAL provider performs its OAuth refresh / token exchange here (on the host) and reads its own
        // long-lived secret store. This stub just looks up a placeholder — no network, no vendor SDK.
        var secret = _secretLookup(profile.Id);
        if (string.IsNullOrEmpty(secret))
        {
            // Fail loudly, not silently: a real provider throws the same way when a profile is not connected
            // or its token has expired and cannot be refreshed, so a session start reports a clear error
            // instead of launching an unauthenticated CLI.
            throw new InvalidOperationException(
                $"Connected-service profile '{profile.Id}' has no credential configured for provider '{Id}'.");
        }

        // Only the short-lived resolved credential leaves the host. The refresh token / long-lived secret
        // is NEVER placed in Value or Env.
        var resolved = new ResolvedServiceCredential(
            Value: secret,
            ExpiresAt: _time.GetUtcNow() + _lifetime,
            Env: new Dictionary<string, string> { ["AGNES_CONNECTED_SERVICE"] = profile.Id });

        return Task.FromResult(resolved);
    }
}
