using Agnes.Abstractions;
using Agnes.Host.Sharing;

namespace Agnes.Host.Tests;

/// <summary>
/// Covers collaboration/02 session sharing: two deliberately-separate mechanisms — direct sharing with an
/// identified recipient (three access levels + an orthogonal permission-approval toggle) and always-view-only
/// public links. The security invariants are the point and are asserted structurally, not by inspecting a
/// default: permission-approvals cannot attach to a view-only / public / inactive share, and a public link has
/// no code path to send / approve / manage. Everything is offline; temp paths come from
/// <see cref="Path.GetTempPath"/> and expiry is driven by a hand-advanced clock.
/// </summary>
public class SessionSharingTests
{
    // A clock we advance by hand so public-link expiry is deterministic (no real waiting).
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // A session-activity probe the test flips: only ids in the set are "active" (live on the host).
    private sealed class FakeActivity : ISessionActivityProbe
    {
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);
        public bool IsActive(string sessionId) => Active.Contains(sessionId);
    }

    private const string Session = "sess-1";

    private static (SessionShareStore Shares, PublicLinkStore Links, SessionSharingService Service, SessionAccessAuthorizer Authorizer, FakeActivity Activity)
        Build(TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        var shares = new SessionShareStore(directory: null, time: clock);
        var links = new PublicLinkStore(directory: null, time: clock);
        var activity = new FakeActivity { Active = { Session } };
        var service = new SessionSharingService(shares, links, activity);
        var authorizer = new SessionAccessAuthorizer(shares);
        return (shares, links, service, authorizer, activity);
    }

    // Extracts the one-time raw token from a public-link URL's "?t=..." query — the only place it is ever exposed.
    private static string? RawToken(Uri url)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "t")
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
    }

    private static SharingCaller Owner => new(DeviceId: "owner-device", GitHubLogin: null, IsOwner: true);
    private static SharingCaller Device(string id) => new(DeviceId: id, GitHubLogin: null, IsOwner: false);
    private static SharingCaller Github(string login) => new(DeviceId: null, GitHubLogin: login, IsOwner: false);

    // ---- direct sharing: subscribe / prompt level enforcement (AC1) ----

    [Fact]
    public void Owner_can_subscribe_and_prompt_without_any_share()
    {
        var (_, _, _, auth, _) = Build();

        Assert.True(auth.CanSubscribe(Session, Owner));
        Assert.True(auth.CanPrompt(Session, Owner));
        Assert.True(auth.CanManage(Session, Owner));
    }

    [Fact]
    public void An_unshared_device_is_rejected_from_subscribe()
    {
        var (_, _, _, auth, _) = Build();

        // AC7 / additive: a session that has never been shared is owner-only — a paired-but-unshared device
        // cannot even watch it.
        Assert.False(auth.CanSubscribe(Session, Device("intruder")));
        Assert.False(auth.CanPrompt(Session, Device("intruder")));
    }

    [Fact]
    public async Task A_view_only_recipient_can_subscribe_but_prompt_is_rejected()
    {
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "bob", SessionAccessLevel.ViewOnly, allowPermissionApprovals: false);

        Assert.True(auth.CanSubscribe(Session, Github("bob")));
        // AC1: enforced server-side, not merely a hidden compose box.
        Assert.False(auth.CanPrompt(Session, Github("bob")));
        Assert.False(auth.CanManage(Session, Github("bob")));
    }

    [Fact]
    public async Task A_can_edit_recipient_can_subscribe_and_prompt()
    {
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "bob", SessionAccessLevel.CanEdit, allowPermissionApprovals: false);

        Assert.True(auth.CanSubscribe(Session, Github("bob")));
        Assert.True(auth.CanPrompt(Session, Github("bob")));
        // CanEdit is not CanManage — cannot re-share (see the manage test below).
        Assert.False(auth.CanManage(Session, Github("bob")));
    }

    [Fact]
    public async Task A_share_can_name_a_paired_device_id_as_the_recipient()
    {
        // The v1 fallback: recipient identity may be a paired device id, not only a GitHub login.
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "device-42", SessionAccessLevel.CanEdit, allowPermissionApprovals: false);

        Assert.True(auth.CanPrompt(Session, Device("device-42")));
        Assert.False(auth.CanPrompt(Session, Device("device-99")));
    }

    [Fact]
    public async Task Revoking_a_share_denies_further_access_immediately()
    {
        // AC4: revocation is immediate; the next authorization read (even on an already-open connection) fails.
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "bob", SessionAccessLevel.CanEdit, allowPermissionApprovals: false);
        Assert.True(auth.CanPrompt(Session, Github("bob")));

        await svc.RevokeAsync(Session, "bob");
        Assert.False(auth.CanSubscribe(Session, Github("bob")));
        Assert.False(auth.CanPrompt(Session, Github("bob")));
    }

    // ---- the orthogonal permission-approval capability (AC2) ----

    [Fact]
    public async Task A_can_edit_share_without_the_flag_cannot_approve_permissions()
    {
        // AC2: approving tool calls is a separate grant, never implied by CanEdit.
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "bob", SessionAccessLevel.CanEdit, allowPermissionApprovals: false);

        Assert.True(auth.CanPrompt(Session, Github("bob")));
        Assert.False(auth.CanApprovePermissions(Session, Github("bob")));
    }

    [Fact]
    public async Task A_can_edit_share_with_the_flag_can_approve_permissions()
    {
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "bob", SessionAccessLevel.CanEdit, allowPermissionApprovals: true);

        Assert.True(auth.CanApprovePermissions(Session, Github("bob")));
    }

    [Fact]
    public async Task Enabling_approvals_on_a_view_only_share_is_rejected_with_a_typed_error()
    {
        // Structural, not defaulted-off: the flag on a view-only share is refused outright.
        var (shares, _, svc, _, _) = Build();

        var ex = await Assert.ThrowsAsync<SharingException>(
            () => svc.ShareWithAsync(Session, "bob", SessionAccessLevel.ViewOnly, allowPermissionApprovals: true));
        Assert.Contains("view-only", ex.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing was written — the rejected share does not exist in any downgraded form.
        Assert.Null(shares.FindActive(Session, "bob"));
    }

    [Fact]
    public async Task Enabling_approvals_on_an_inactive_session_is_rejected_with_a_typed_error()
    {
        var (shares, _, svc, _, activity) = Build();
        activity.Active.Clear(); // the session is no longer live

        var ex = await Assert.ThrowsAsync<SharingException>(
            () => svc.ShareWithAsync(Session, "bob", SessionAccessLevel.CanEdit, allowPermissionApprovals: true));
        Assert.Contains("inactive", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(shares.FindActive(Session, "bob"));
    }

    [Fact]
    public void A_public_link_has_no_api_by_which_to_enable_approvals()
    {
        // AC3, structural: CreatePublicLinkAsync takes only PublicLinkOptions, which has no approval or level
        // field — there is no overload, parameter, or setter to abuse. This test documents that impossibility;
        // it compiles precisely because the capability cannot be expressed.
        var optionType = typeof(PublicLinkOptions);
        Assert.DoesNotContain(optionType.GetProperties(),
            p => p.Name.Contains("Approv", StringComparison.OrdinalIgnoreCase)
              || p.Name.Contains("Level", StringComparison.OrdinalIgnoreCase)
              || p.Name.Contains("Edit", StringComparison.OrdinalIgnoreCase));

        var backend = typeof(ISharingBackend).GetMethod(nameof(ISharingBackend.CreatePublicLinkAsync))!;
        // Only (sessionId, options, ct) — no bool/level parameter through which write access could be requested.
        Assert.DoesNotContain(backend.GetParameters(),
            p => p.ParameterType == typeof(bool) || p.ParameterType == typeof(SessionAccessLevel));
    }

    // ---- manage level (AC: CanManage required to re-share) ----

    [Fact]
    public async Task Can_manage_is_required_to_change_another_collaborators_access()
    {
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "manager", SessionAccessLevel.CanManage, allowPermissionApprovals: false);
        await svc.ShareWithAsync(Session, "editor", SessionAccessLevel.CanEdit, allowPermissionApprovals: false);

        Assert.True(auth.CanManage(Session, Github("manager")));
        // A CanEdit collaborator cannot re-share / manage.
        Assert.False(auth.CanManage(Session, Github("editor")));
    }

    [Fact]
    public async Task Re_sharing_supersedes_the_prior_level()
    {
        var (_, _, svc, auth, _) = Build();
        await svc.ShareWithAsync(Session, "bob", SessionAccessLevel.CanEdit, allowPermissionApprovals: false);
        Assert.True(auth.CanPrompt(Session, Github("bob")));

        await svc.ShareWithAsync(Session, "bob", SessionAccessLevel.ViewOnly, allowPermissionApprovals: false);
        Assert.False(auth.CanPrompt(Session, Github("bob")));
        Assert.True(auth.CanSubscribe(Session, Github("bob")));
    }

    // ---- public links: view-only by construction, hashed token, limits, revoke ----

    [Fact]
    public async Task A_public_viewer_can_read_but_no_code_path_lets_it_write()
    {
        var (_, links, svc, _, _) = Build();
        var link = await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(Expiry: null, MaxUses: null, RequireConsent: false));

        var rawToken = RawToken(link.Url);
        Assert.False(string.IsNullOrEmpty(rawToken));

        // A read-only viewer (no device token) validates and may view …
        Assert.Equal(PublicLinkValidation.Valid, links.Validate(Session, rawToken));

        // … and there is simply no method — on the store, the service, or the authorizer — that turns a public
        // link into send/approve/manage. The authorizer's write checks take a device-identified caller only;
        // there is no "CanPromptViaPublicLink" surface at all (AC3, structural).
        var authorizerWriteMethods = typeof(SessionAccessAuthorizer).GetMethods()
            .Where(m => m.Name is "CanPrompt" or "CanApprovePermissions" or "CanManage");
        Assert.DoesNotContain(authorizerWriteMethods,
            m => m.GetParameters().Any(p => p.ParameterType == typeof(string) && p.Name!.Contains("token", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task The_stored_public_link_token_is_a_hash_not_the_raw_token()
    {
        // AC6: the raw token appears only once (in the URL); the store holds a SHA-256 hash it can't reverse.
        var (_, links, svc, _, _) = Build();
        var link = await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(null, null, false));
        var rawToken = RawToken(link.Url)!;

        var stored = links.FindActive(Session)!;
        Assert.NotEqual(rawToken, stored.TokenHash);
        Assert.Equal(link.TokenHash, stored.TokenHash);
        // The stored value is a hex SHA-256 digest (64 hex chars), never the base64url raw token.
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.Matches("^[0-9a-f]{64}$", stored.TokenHash);
    }

    [Fact]
    public async Task Public_link_max_uses_is_enforced()
    {
        // AC5: exactly MaxUses opens succeed, then further attempts are rejected.
        var (_, links, svc, _, _) = Build();
        var link = await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(Expiry: null, MaxUses: 2, RequireConsent: false));
        var raw = RawToken(link.Url);

        Assert.Equal(PublicLinkValidation.Valid, links.Validate(Session, raw));
        Assert.Equal(PublicLinkValidation.Valid, links.Validate(Session, raw));
        Assert.Equal(PublicLinkValidation.UsesExhausted, links.Validate(Session, raw));
    }

    [Fact]
    public async Task Public_link_expiry_is_enforced_regardless_of_remaining_uses()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var (_, links, svc, _, _) = Build(clock);
        var link = await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(Expiry: TimeSpan.FromHours(1), MaxUses: 100, RequireConsent: false));
        var raw = RawToken(link.Url);

        Assert.Equal(PublicLinkValidation.Valid, links.Validate(Session, raw));

        clock.Advance(TimeSpan.FromHours(2)); // past expiry, plenty of uses left
        Assert.Equal(PublicLinkValidation.Expired, links.Validate(Session, raw));
    }

    [Fact]
    public async Task Revoking_a_public_link_invalidates_it_immediately()
    {
        // AC4/AC6: revoke kills the current link at once; a reissue is the only way back (a new token).
        var (_, links, svc, _, _) = Build();
        var link = await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(null, null, false));
        var raw = RawToken(link.Url);
        Assert.Equal(PublicLinkValidation.Valid, links.Validate(Session, raw));

        await svc.RevokePublicLinkAsync(Session);
        Assert.Equal(PublicLinkValidation.NotFound, links.Validate(Session, raw));
        Assert.Null(links.FindActive(Session));
    }

    [Fact]
    public async Task Reissuing_a_public_link_supersedes_the_old_token()
    {
        var (_, links, svc, _, _) = Build();
        var first = await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(null, null, false));
        var firstRaw = RawToken(first.Url);

        var second = await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(null, null, false));
        var secondRaw = RawToken(second.Url);

        Assert.NotEqual(firstRaw, secondRaw);
        Assert.Equal(PublicLinkValidation.NotFound, links.Validate(Session, firstRaw)); // old token dead
        Assert.Equal(PublicLinkValidation.Valid, links.Validate(Session, secondRaw));
    }

    [Fact]
    public async Task A_wrong_public_token_is_rejected_indistinguishably_from_no_link()
    {
        var (_, links, svc, _, _) = Build();
        await svc.CreatePublicLinkAsync(Session, new PublicLinkOptions(null, null, false));

        Assert.Equal(PublicLinkValidation.NotFound, links.Validate(Session, "not-the-token"));
        Assert.Equal(PublicLinkValidation.NotFound, links.Validate("other-session", "anything"));
    }

    // ---- persistence: shares/links survive a reload; a revoked share stays inactive ----

    [Fact]
    public void Shares_survive_a_reload_from_disk_and_a_revoked_share_stays_revoked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agnes-shares-" + Guid.NewGuid().ToString("n"));
        try
        {
            var first = new SessionShareStore(dir, TimeProvider.System);
            first.Share(Session, "bob", SessionAccessLevel.CanEdit, allowPermissionApprovals: true, sharedByDevice: "owner");
            first.Share(Session, "carol", SessionAccessLevel.ViewOnly, allowPermissionApprovals: false, sharedByDevice: "owner");
            first.Revoke(Session, "carol");

            var reloaded = new SessionShareStore(dir, TimeProvider.System);
            var bob = reloaded.FindActive(Session, "bob");
            Assert.NotNull(bob);
            Assert.Equal(SessionAccessLevel.CanEdit, bob!.Level);
            Assert.True(bob.AllowPermissionApprovals);
            Assert.Null(reloaded.FindActive(Session, "carol")); // revoked, retained but inactive
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
