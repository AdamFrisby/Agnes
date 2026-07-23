using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The connected-services credential surface (.ideas/providers/02): a named, multi-profile provider
/// credential broker that mirrors the sandbox git-credential broker's "host holds the secret, only a
/// short-lived resolved credential is handed out" shape. Everything here is offline — no network.
/// </summary>
public class ConnectedServiceBrokerTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), $"agnes-connected-svc-{Guid.NewGuid():n}");

    private static ConnectedServiceBroker BrokerOver(
        ConnectedServiceProfileStore store,
        params IConnectedServiceProvider[] providers)
    {
        var registry = new PluginRegistry<IConnectedServiceProvider>(providers, p => p.Id);
        return new ConnectedServiceBroker(store, registry);
    }

    [Fact]
    public void Profile_store_round_trips_named_profiles_across_a_reload()
    {
        var dir = NewTempDir();
        try
        {
            var store = new ConnectedServiceProfileStore(dir);
            var personal = store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "personal"));
            var work = store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "work"));

            Assert.False(string.IsNullOrWhiteSpace(personal.Id));
            Assert.NotEqual(personal.Id, work.Id);

            // A brand-new instance over the same directory sees both named profiles.
            var reloaded = new ConnectedServiceProfileStore(dir);
            var all = reloaded.List();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, p => p.AccountLabel == "personal");
            Assert.Contains(all, p => p.AccountLabel == "work");

            // Delete removes exactly one and persists the removal.
            Assert.True(reloaded.Delete(personal.Id));
            Assert.False(reloaded.Delete(personal.Id)); // already gone
            Assert.Single(new ConnectedServiceProfileStore(dir).List());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Broker_resolves_a_profile_to_the_template_providers_stub_credential()
    {
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "personal"));
        var broker = BrokerOver(store, new TemplateConnectedServiceProvider(secretLookup: _ => "stub-token-123"));

        var resolved = await broker.ResolveAsync(profile.Id);

        Assert.Equal("stub-token-123", resolved.Value);
        Assert.NotNull(resolved.ExpiresAt); // short-lived by design
        Assert.NotNull(resolved.Env);
        Assert.Equal(profile.Id, resolved.Env!["AGNES_CONNECTED_SERVICE"]);
    }

    [Fact]
    public async Task Resolving_an_unknown_profile_id_fails_clearly()
    {
        var store = new ConnectedServiceProfileStore();
        var broker = BrokerOver(store, new TemplateConnectedServiceProvider());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => broker.ResolveAsync("does-not-exist"));
    }

    [Fact]
    public async Task A_profile_pointing_at_an_unregistered_provider_fails_clearly()
    {
        var store = new ConnectedServiceProfileStore();
        // The profile references "ghost", but only the template provider is registered.
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "ghost", "Ghost", "personal"));
        var broker = BrokerOver(store, new TemplateConnectedServiceProvider());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ResolveAsync(profile.Id));
        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_provider_is_selectable_by_its_id_with_no_broker_change()
    {
        // Proves the plugin-point extensibility: register a completely different IConnectedServiceProvider
        // and the SAME broker routes to it purely by the profile's ProviderId — no code change here.
        var store = new ConnectedServiceProfileStore();
        var templateProfile = store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "personal"));
        var fakeProfile = store.Save(new ConnectedServiceProfile(string.Empty, "linear-fake", "Linear (fake)", "work"));

        var broker = BrokerOver(
            store,
            new TemplateConnectedServiceProvider(secretLookup: _ => "template-secret"),
            new FakeConnectedServiceProvider("linear-fake", "linear-secret"));

        Assert.Equal("template-secret", (await broker.ResolveAsync(templateProfile.Id)).Value);
        Assert.Equal("linear-secret", (await broker.ResolveAsync(fakeProfile.Id)).Value);
    }

    [Fact]
    public async Task Template_provider_fails_loudly_when_no_credential_is_configured()
    {
        // A silent empty would let an unauthenticated CLI launch masquerade as success — so a missing
        // secret throws instead.
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "personal"));
        var broker = BrokerOver(store, new TemplateConnectedServiceProvider(secretLookup: _ => null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ResolveAsync(profile.Id));
    }

    [Fact]
    public void The_profile_listing_surface_exposes_names_and_labels_but_never_a_secret()
    {
        // ListProfiles is what a hub method would return to a client: identity/routing only. Assert no
        // credential value ("super-secret-token") appears anywhere in the serialized listing.
        const string secret = "super-secret-token";
        var store = new ConnectedServiceProfileStore();
        store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "personal"));
        var broker = BrokerOver(store, new TemplateConnectedServiceProvider(secretLookup: _ => secret));

        var listing = broker.ListProfiles();
        Assert.Single(listing);
        Assert.Equal("personal", listing[0].AccountLabel);
        Assert.Equal("Template", listing[0].DisplayName);

        var json = System.Text.Json.JsonSerializer.Serialize(listing);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
    }

    /// <summary>A second, unrelated provider used only to prove the broker routes by id with no change.</summary>
    private sealed class FakeConnectedServiceProvider(string id, string secret) : IConnectedServiceProvider
    {
        public string Id => id;
        public string DisplayName => id;

        public Task<ResolvedServiceCredential> ResolveAsync(ConnectedServiceProfile profile, CancellationToken ct = default)
            => Task.FromResult(new ResolvedServiceCredential(secret, ExpiresAt: null));
    }
}
