using System.Text.Json;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Tests;

/// <summary>
/// Who a paired device is on this host, and how it stays that way.
///
/// <para>These pin the replacement for a rule that failed in the field: ownership used to be "whoever paired
/// earliest", so anything that wrote a record before the operator's own device did — a stray test run, a demo,
/// a device later revoked — silently took the host over, and under the default shared isolation that left
/// every real device unable to open anything. Ownership is now a property of HOW a device was admitted, kept
/// on the record, and changeable only by an Owner.</para>
/// </summary>
public sealed class DeviceRoleTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"agnes-roles-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }

    private DeviceRegistry New(string? bootstrap = null) => new(bootstrap, _file);

    // ---- admission decides the role ----

    [Fact]
    public void The_typed_pairing_code_admits_an_owner()
    {
        // The code is read off the host's own console, so presenting it IS the operator acting.
        var reg = New();
        var paired = reg.TryPair(reg.PairingCode, "laptop")!;

        Assert.Equal(DeviceRole.Owner, paired.Role);
        Assert.True(reg.IsOwner(paired.DeviceId));
        Assert.Equal(DeviceRole.Owner, Assert.Single(reg.ListDevices()).Role);
    }

    [Fact]
    public void An_issued_device_is_a_member_unless_the_caller_says_otherwise()
    {
        var reg = New();
        var member = reg.IssueDeviceToken("phone", "github:bob", "github");
        var owner = reg.IssueDeviceToken("laptop", "key:mine", "keypair", DeviceRole.Owner);

        Assert.Equal(DeviceRole.Member, member.Role);
        Assert.False(reg.IsOwner(member.DeviceId));
        Assert.Equal(DeviceRole.Owner, owner.Role);
        Assert.True(reg.IsOwner(owner.DeviceId));
    }

    [Fact]
    public void The_bootstrap_token_is_an_operator_without_being_a_device()
    {
        var reg = New(bootstrap: "boot");
        Assert.True(reg.IsOwner(reg.ResolveCallerId("boot")));
        Assert.Empty(reg.ListDevices());          // it has no record...
        Assert.Null(reg.DescribeSelf("boot"));    // ...so /devices/me has nothing to describe
    }

    [Fact]
    public void A_device_can_describe_itself_without_listing_the_household()
    {
        var reg = New();
        var mine = reg.IssueDeviceToken("phone", "approved:abc", "approval");

        var me = reg.DescribeSelf(mine.Token)!;
        Assert.Equal(mine.DeviceId, me.Id);
        Assert.Equal(DeviceRole.Member, me.Role);
        Assert.Equal("approval", me.Kind);
        Assert.True(me.IsCurrentDevice);
        Assert.Null(reg.DescribeSelf("not-a-token"));
    }

    // ---- migrating a store written before roles existed ----

    [Fact]
    public void A_pre_roles_store_keeps_the_owner_it_effectively_had()
    {
        // Exactly what the old IsOwner computed on every call — the earliest-paired device — frozen once, so
        // upgrading a live host changes nothing about who owns it.
        WriteLegacyStore(
            ("aaa", "2026-01-05T00:00:00+00:00"),
            ("bbb", "2026-01-01T00:00:00+00:00"),
            ("ccc", "2026-01-09T00:00:00+00:00"));

        var reg = New();

        Assert.Equal(DeviceRole.Owner, reg.ListDevices().Single(d => d.Id == "bbb").Role);
        Assert.True(reg.IsOwner("bbb"));
        Assert.False(reg.IsOwner("aaa"));
        Assert.Equal(1, reg.OwnerCount);
    }

    [Fact]
    public void The_migration_runs_once_and_never_second_guesses_a_recorded_role()
    {
        WriteLegacyStore(
            ("aaa", "2026-01-05T00:00:00+00:00"),
            ("bbb", "2026-01-01T00:00:00+00:00"));

        var first = New();
        first.SetRole("aaa", DeviceRole.Owner, callerId: "bbb");
        first.SetRole("bbb", DeviceRole.Member, callerId: "aaa");

        // Reloading must not "re-migrate" and hand ownership back to the earliest device: the roles on disk
        // are now explicit decisions, and the store no longer looks like a pre-roles one.
        var reloaded = New();
        Assert.True(reloaded.IsOwner("aaa"));
        Assert.False(reloaded.IsOwner("bbb"));
    }

    [Fact]
    public void An_empty_store_gets_no_owner_conjured_for_it()
    {
        var reg = New();
        Assert.Equal(0, reg.OwnerCount);
        Assert.False(reg.IsOwner("anything"));
    }

    // ---- one device, one record ----

    [Fact]
    public void Signing_in_again_with_the_same_key_rotates_the_token_instead_of_adding_a_row()
    {
        var reg = New();
        var first = reg.IssueDeviceToken("laptop", "key:mine", "keypair", DeviceRole.Owner, identity: "keypair:fp1");
        var second = reg.IssueDeviceToken("laptop (renamed)", "key:mine", "keypair", DeviceRole.Member, identity: "keypair:fp1");

        Assert.Equal(first.DeviceId, second.DeviceId);      // same device
        Assert.NotEqual(first.Token, second.Token);
        Assert.False(reg.IsValid(first.Token));             // the old token stops working
        Assert.True(reg.IsValid(second.Token));

        var only = Assert.Single(reg.ListDevices());
        Assert.Equal("laptop (renamed)", only.Name);
        // The role on the record wins over whatever the sign-in asked for: re-authenticating is not a way to
        // demote (or promote) yourself behind an Owner's back.
        Assert.Equal(DeviceRole.Owner, only.Role);
        Assert.Equal(DeviceRole.Owner, second.Role);
    }

    [Fact]
    public void Two_devices_behind_one_account_stay_two_devices()
    {
        var reg = New();
        var laptop = reg.IssueDeviceToken("laptop", "github:alice", "github", identity: DeviceIdentity.For("github:alice", "laptop"));
        var phone = reg.IssueDeviceToken("phone", "github:alice", "github", identity: DeviceIdentity.For("github:alice", "phone"));

        Assert.NotEqual(laptop.DeviceId, phone.DeviceId);
        Assert.Equal(2, reg.ListDevices().Count);
        Assert.True(reg.IsValid(laptop.Token));  // signing in on the phone did not log the laptop out
    }

    [Fact]
    public void A_pairing_code_device_has_no_identity_to_rotate()
    {
        // A code is single-use and stands for nothing durable, so two pairings are two devices.
        var reg = new DeviceRegistry(null, _file, allowCodeAfterFirstDevice: true);
        var first = reg.TryPair(reg.PairingCode, "same-name")!;
        var second = reg.TryPair(reg.PairingCode, "same-name")!;

        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.True(reg.IsValid(first.Token));
    }

    // ---- promoting and demoting ----

    [Fact]
    public void An_owner_can_promote_and_demote_but_never_leave_the_host_ownerless()
    {
        var reg = New();
        var owner = reg.TryPair(reg.PairingCode, "laptop")!;
        var member = reg.IssueDeviceToken("phone", "approved:x", "approval");

        // The last Owner cannot step down — a host with no Owner can never promote anybody back.
        var refused = reg.SetRole(owner.DeviceId, DeviceRole.Member, callerId: owner.DeviceId);
        Assert.False(refused.Ok);
        Assert.Contains("last Owner", refused.Error, StringComparison.Ordinal);
        Assert.True(reg.IsOwner(owner.DeviceId));

        // Promote the phone first, and then stepping down is fine.
        Assert.True(reg.SetRole(member.DeviceId, DeviceRole.Owner, owner.DeviceId).Ok);
        Assert.True(reg.SetRole(owner.DeviceId, DeviceRole.Member, owner.DeviceId).Ok);
        Assert.False(reg.IsOwner(owner.DeviceId));
        Assert.True(reg.IsOwner(member.DeviceId));

        Assert.True(reg.SetRole("no-such-device", DeviceRole.Owner, owner.DeviceId).NotFound);
    }

    [Fact]
    public void A_role_change_survives_a_restart()
    {
        var reg = New();
        var owner = reg.TryPair(reg.PairingCode, "laptop")!;
        var phone = reg.IssueDeviceToken("phone", "approved:x", "approval");
        reg.SetRole(phone.DeviceId, DeviceRole.Owner, owner.DeviceId);

        Assert.True(New().IsOwner(phone.DeviceId));
    }

    // ---- last-seen, and pruning by it ----

    [Fact]
    public void Last_seen_reaches_the_disk_so_a_restart_can_still_tell_a_stale_device()
    {
        var reg = New();
        var device = reg.IssueDeviceToken("phone", "approved:x", "approval");
        Assert.Null(Assert.Single(reg.ListDevices()).LastSeenAt);

        Assert.True(reg.IsValid(device.Token));
        reg.Flush();

        Assert.NotNull(Assert.Single(New().ListDevices()).LastSeenAt);
    }

    [Fact]
    public void Prune_removes_the_stale_and_spares_the_caller_and_the_last_owner()
    {
        WriteLegacyStore(
            ("owner", "2020-01-01T00:00:00+00:00"),
            ("stale", "2020-01-02T00:00:00+00:00"),
            ("caller", "2020-01-03T00:00:00+00:00"));

        var reg = New();                         // migration makes "owner" (earliest) the Owner
        var removed = reg.Prune(unusedForDays: 30, callerId: "caller");

        Assert.Equal("stale", Assert.Single(removed).Id);
        Assert.Contains(reg.ListDevices(), d => d.Id == "caller");  // never the device asking
        Assert.Contains(reg.ListDevices(), d => d.Id == "owner");   // never the last Owner
    }

    [Fact]
    public void Prune_leaves_a_recently_seen_device_alone()
    {
        var reg = New();
        var device = reg.IssueDeviceToken("phone", "approved:x", "approval");
        Assert.True(reg.IsValid(device.Token));

        Assert.Empty(reg.Prune(unusedForDays: 30, callerId: "somebody-else"));
        Assert.Single(reg.ListDevices());
    }

    // ---- what each admission method is worth, per configuration ----

    [Fact]
    public void An_authorized_key_is_an_owner_by_default_and_configurably_not()
    {
        Assert.Equal(DeviceRole.Owner, Options().KeypairRole);
        Assert.Equal(DeviceRole.Member, Options(("Agnes:Auth:Keypair:Role", "Member")).KeypairRole);
    }

    [Fact]
    public void A_federated_identity_is_a_member_unless_the_operator_named_it()
    {
        var roles = Options(
            ("Agnes:Auth:GitHub:Owners:0", "alice"),
            ("Agnes:Auth:Oidc:Owners:0", "sub-123"),
            ("Agnes:Auth:CloudflareAccess:Owners:0", "alice@example.com"),
            ("Agnes:Auth:Mtls:Owners:0", "CN=ops"));

        Assert.Equal(DeviceRole.Owner, roles.ForGitHub("alice"));
        Assert.Equal(DeviceRole.Owner, roles.ForGitHub("ALICE"));   // logins are not case-sensitive
        Assert.Equal(DeviceRole.Member, roles.ForGitHub("mallory"));
        Assert.Equal(DeviceRole.Member, roles.ForGitHub(null));

        Assert.Equal(DeviceRole.Owner, roles.ForOidc("sub-123"));
        Assert.Equal(DeviceRole.Member, roles.ForOidc("sub-999"));

        // Cloudflare names a person twice over; an operator will write the email.
        Assert.Equal(DeviceRole.Owner, roles.ForCloudflare("opaque-subject", "alice@example.com"));
        Assert.Equal(DeviceRole.Member, roles.ForCloudflare("opaque-subject", "bob@example.com"));

        Assert.Equal(DeviceRole.Owner, roles.ForMtls("CN=ops"));
        Assert.Equal(DeviceRole.Member, roles.ForMtls("CN=intern"));
    }

    [Fact]
    public void With_no_owners_configured_every_federated_sign_in_is_a_member()
    {
        var roles = Options();
        Assert.Equal(DeviceRole.Member, roles.ForGitHub("alice"));
        Assert.Equal(DeviceRole.Member, roles.ForOidc("sub-123"));
        Assert.Equal(DeviceRole.Member, roles.ForCloudflare("s", "alice@example.com"));
        Assert.Equal(DeviceRole.Member, roles.ForMtls("CN=ops"));
    }

    private static DeviceRoleOptions Options(params (string Key, string Value)[] settings)
        => DeviceRoleOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
                .Build());

    /// <summary>Writes a devices.json as it looked before roles existed — no Role field at all.</summary>
    private void WriteLegacyStore(params (string Id, string PairedAt)[] devices)
        => File.WriteAllText(_file, JsonSerializer.Serialize(devices.Select(d => new
        {
            Id = d.Id,
            Name = d.Id,
            TokenHash = "hash-" + d.Id,
            Subject = "pairing",
            Kind = "pairing",
            PairedAt = d.PairedAt,
        })));
}
