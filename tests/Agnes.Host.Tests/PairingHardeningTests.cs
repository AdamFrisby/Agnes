using Agnes.Host.Hosting;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

/// <summary>
/// The typed bootstrap code is ~40 bits because a human reads and types it. That is defensible exactly
/// once, on a host nobody has connected to yet. These pin the properties that replace it afterwards.
/// </summary>
public sealed class PairingHardeningTests
{
    private static DeviceRegistry NewRegistry(bool allowCodeAfterFirstDevice = false)
        => new(bootstrapToken: null,
            dataFilePath: Path.Combine(Path.GetTempPath(), "agnes-pairing-" + Guid.NewGuid().ToString("n") + ".json"),
            logger: null,
            pairingEnabled: true,
            allowCodeAfterFirstDevice: allowCodeAfterFirstDevice);

    // ---- the typed code closes after the first device ----

    [Fact]
    public void The_typed_code_pairs_the_first_device()
    {
        var registry = NewRegistry();

        Assert.NotNull(registry.TryPair(registry.PairingCode, "laptop"));
    }

    [Fact]
    public void The_typed_code_is_refused_once_a_device_is_paired()
    {
        var registry = NewRegistry();
        Assert.NotNull(registry.TryPair(registry.PairingCode, "laptop"));

        // Even with the freshly-rotated, entirely correct code: there is now a device that could vouch,
        // so the weak path is closed.
        Assert.Null(registry.TryPair(registry.PairingCode, "attacker"));
    }

    [Fact]
    public void An_operator_can_deliberately_keep_the_code_open()
    {
        var registry = NewRegistry(allowCodeAfterFirstDevice: true);
        Assert.NotNull(registry.TryPair(registry.PairingCode, "laptop"));

        Assert.NotNull(registry.TryPair(registry.PairingCode, "second"));
    }

    // ---- QR grants ----

    [Fact]
    public void A_grant_is_high_entropy_and_url_safe()
    {
        var grant = new PairingGrants().Mint("https://host:5099");

        // 256 bits of CSPRNG output, base64url with no padding — six times the bootstrap code's entropy,
        // and safe to carry in the deep link's query string without escaping.
        Assert.Equal(43, grant.Secret.Length);
        Assert.DoesNotContain('=', grant.Secret);
        Assert.DoesNotContain('+', grant.Secret);
        Assert.DoesNotContain('/', grant.Secret);
    }

    [Fact]
    public void Two_grants_never_collide()
    {
        var grants = new PairingGrants();
        var secrets = Enumerable.Range(0, 200).Select(_ => grants.Mint("https://host:5099").Secret).ToList();

        Assert.Equal(secrets.Count, secrets.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_grant_redeems_exactly_once()
    {
        var grants = new PairingGrants();
        var grant = grants.Mint("https://host:5099");

        Assert.True(grants.TryRedeem(grant.Secret, out _));
        Assert.False(grants.TryRedeem(grant.Secret, out _)); // a photographed QR can't be replayed
    }

    [Fact]
    public void A_grant_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var grants = new PairingGrants(() => now);
        var grant = grants.Mint("https://host:5099");

        now += PairingGrants.Lifetime + TimeSpan.FromSeconds(1);

        Assert.False(grants.TryRedeem(grant.Secret, out _));
    }

    [Fact]
    public void Hiding_a_QR_revokes_its_grant_immediately()
    {
        var grants = new PairingGrants();
        var grant = grants.Mint("https://host:5099");

        grants.Revoke(grant.Secret);

        Assert.False(grants.TryRedeem(grant.Secret, out _));
    }

    [Fact]
    public void A_grant_can_carry_the_session_it_was_generated_from()
    {
        var grants = new PairingGrants();
        var grant = grants.Mint("https://host:5099", sessionId: "s-42");

        Assert.Contains("session=s-42", grant.DeepLink, StringComparison.Ordinal);
        Assert.True(grants.TryRedeem(grant.Secret, out var session));
        Assert.Equal("s-42", session);
    }

    [Fact]
    public void A_wrong_grant_is_refused()
        => Assert.False(new PairingGrants().TryRedeem("not-a-real-grant", out _));

    // ---- approval ----

    [Fact]
    public void The_requesting_device_can_compute_the_same_verification_code()
    {
        var approvals = new PairingApprovals();
        var pending = approvals.Open("SPKI-PUBLIC-KEY", "phone")!;

        // Derived, not dictated: the new device computes this from its own key and the request id, so a
        // substituted key produces a different number and the mismatch is visible to the human.
        Assert.Equal(
            PairingApprovals.DeriveVerificationCode("SPKI-PUBLIC-KEY", pending.RequestId),
            pending.VerificationCode);
        Assert.Equal(6, pending.VerificationCode.Length);
    }

    [Fact]
    public void A_different_key_yields_a_different_code()
    {
        var approvals = new PairingApprovals();
        var mine = approvals.Open("MY-KEY", "phone")!;

        var attacker = PairingApprovals.DeriveVerificationCode("ATTACKER-KEY", mine.RequestId);

        Assert.NotEqual(mine.VerificationCode, attacker);
    }

    [Fact]
    public void Nothing_is_issued_until_a_human_approves()
    {
        var approvals = new PairingApprovals();
        var pending = approvals.Open("KEY", "phone")!;

        var status = approvals.Poll(pending.RequestId);

        Assert.Equal(PairApprovalState.Pending, status.State);
        Assert.Null(status.Token);
    }

    [Fact]
    public void Approval_hands_the_token_to_the_requester_exactly_once()
    {
        var approvals = new PairingApprovals();
        var pending = approvals.Open("KEY", "phone")!;

        Assert.True(approvals.Approve(pending.RequestId, (name, _) => new PairingResult("d1", name, "the-token")));

        Assert.Equal("the-token", approvals.Poll(pending.RequestId).Token);
        // A replayed poll — or an observer who learns the id later — gets nothing.
        Assert.Equal(PairApprovalState.Unknown, approvals.Poll(pending.RequestId).State);
    }

    [Fact]
    public void A_denied_request_never_yields_a_token()
    {
        var approvals = new PairingApprovals();
        var pending = approvals.Open("KEY", "phone")!;

        Assert.True(approvals.Deny(pending.RequestId));

        var status = approvals.Poll(pending.RequestId);
        Assert.Equal(PairApprovalState.Denied, status.State);
        Assert.Null(status.Token);
        Assert.False(approvals.Approve(pending.RequestId, (n, _) => new PairingResult("d", n, "t")));
    }

    [Fact]
    public void An_unknown_request_is_indistinguishable_from_an_expired_one()
    {
        var now = DateTimeOffset.UtcNow;
        var approvals = new PairingApprovals(() => now);
        var pending = approvals.Open("KEY", "phone")!;

        now += PairingApprovals.Lifetime + TimeSpan.FromSeconds(1);

        // Both report Unknown, so polling can't be used to enumerate live request ids.
        Assert.Equal(PairApprovalState.Unknown, approvals.Poll(pending.RequestId).State);
        Assert.Equal(PairApprovalState.Unknown, approvals.Poll("never-existed").State);
    }

    [Fact]
    public void Outstanding_requests_are_bounded()
    {
        var approvals = new PairingApprovals();

        var accepted = Enumerable.Range(0, 50).Count(i => approvals.Open("KEY" + i, "d" + i) is not null);

        // An unauthenticated endpoint must not let anyone flood the approver's screen.
        Assert.InRange(accepted, 1, 20);
    }

    [Fact]
    public void Only_pending_requests_are_offered_for_approval()
    {
        var approvals = new PairingApprovals();
        var first = approvals.Open("A", "one")!;
        approvals.Open("B", "two");
        approvals.Deny(first.RequestId);

        Assert.DoesNotContain(approvals.Pending(), p => p.RequestId == first.RequestId);
        Assert.Single(approvals.Pending());
    }
}
