using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class DeviceRegistryTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"agnes-devices-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    private DeviceRegistry New(string? bootstrap = null) => new(bootstrap, _file);

    [Fact]
    public void Pairing_with_the_code_issues_a_working_token()
    {
        var reg = New();
        var result = reg.TryPair(reg.PairingCode, "my-laptop");
        Assert.NotNull(result);
        Assert.Equal("my-laptop", result!.DeviceName);
        Assert.True(reg.IsValid(result.Token));
        Assert.False(reg.IsValid("some-other-token"));
    }

    [Fact]
    public void Wrong_code_is_rejected_and_the_code_is_single_use()
    {
        var reg = New();
        Assert.Null(reg.TryPair("WRONG-CODE", "x"));

        var code = reg.PairingCode;
        Assert.NotNull(reg.TryPair(code, "first"));
        // The code rotated on success, so the same code can't pair a second device.
        Assert.NotEqual(code, reg.PairingCode);
        Assert.Null(reg.TryPair(code, "second"));
    }

    [Fact]
    public void Repeated_bad_attempts_rotate_the_code()
    {
        var reg = New();
        var original = reg.PairingCode;
        for (var i = 0; i < 5; i++)
        {
            reg.TryPair("NOPE-NOPE", "x");
        }

        Assert.NotEqual(original, reg.PairingCode);
    }

    [Fact]
    public void Bootstrap_token_is_always_valid()
    {
        var reg = New(bootstrap: "boot-strap-token");
        Assert.True(reg.IsValid("boot-strap-token"));
        Assert.False(reg.IsValid("nope"));
    }

    [Fact]
    public void Devices_persist_and_can_be_revoked()
    {
        var reg = New();
        var result = reg.TryPair(reg.PairingCode, "phone")!;
        Assert.Single(reg.ListDevices());

        // A new registry over the same file loads the paired device (token still valid).
        var reloaded = New();
        Assert.True(reloaded.IsValid(result.Token));
        Assert.Single(reloaded.ListDevices());

        Assert.True(reloaded.Revoke(result.DeviceId));
        Assert.False(reloaded.IsValid(result.Token));
        Assert.Empty(reloaded.ListDevices());
    }

    [Fact]
    public void The_file_never_stores_a_usable_token()
    {
        var reg = New();
        var result = reg.TryPair(reg.PairingCode, "d")!;
        var contents = File.ReadAllText(_file);
        Assert.DoesNotContain(result.Token, contents); // only the SHA-256 hash is persisted
    }

    [Fact]
    public void IssueDeviceToken_mints_a_working_token_with_a_subject()
    {
        var reg = New();
        var result = reg.IssueDeviceToken("phone", subject: "github:alice", kind: "github");
        Assert.True(reg.IsValid(result.Token));
        Assert.Equal("github:alice", Assert.Single(reg.ListDevices()).Subject);
    }

    [Fact]
    public void ListDevices_marks_the_calling_device_so_a_client_can_warn_before_self_revoking()
    {
        var reg = New();
        var mine = reg.IssueDeviceToken("laptop", subject: "pairing", kind: "pairing");
        reg.IssueDeviceToken("phone", subject: "pairing", kind: "pairing");

        var listed = reg.ListDevices(mine.Token);

        Assert.Equal(2, listed.Count);
        Assert.Equal(mine.DeviceId, Assert.Single(listed, d => d.IsCurrentDevice).Id);
    }

    [Fact]
    public void ListDevices_marks_nothing_without_a_caller_token_and_does_not_touch_last_seen()
    {
        var reg = New();
        var mine = reg.IssueDeviceToken("laptop", subject: "pairing", kind: "pairing");

        Assert.All(reg.ListDevices(), d => Assert.False(d.IsCurrentDevice));
        Assert.All(reg.ListDevices("not-a-token"), d => Assert.False(d.IsCurrentDevice));

        // Listing is a pure read: it must not make an unused device look freshly active.
        Assert.Null(Assert.Single(reg.ListDevices(mine.Token)).LastSeenAt);
    }

    [Fact]
    public void Pairing_can_be_disabled_while_other_bootstraps_still_work()
    {
        var reg = new DeviceRegistry(null, _file, pairingEnabled: false);
        Assert.False(reg.PairingEnabled);
        Assert.Equal(string.Empty, reg.PairingCode);       // no code is minted or exposed
        Assert.Null(reg.TryPair("ANY-CODE", "x"));         // pairing is refused entirely

        // ...but a stronger method (GitHub SSO / keypair) can still issue a usable token.
        var token = reg.IssueDeviceToken("d", subject: "github:bob", kind: "github");
        Assert.True(reg.IsValid(token.Token));
    }
}
