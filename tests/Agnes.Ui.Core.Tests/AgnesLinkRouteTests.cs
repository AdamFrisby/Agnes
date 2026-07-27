using Agnes.Protocol;
using Agnes.Ui.Core;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// What an <c>agnes://pair</c> link means. Shared by the desktop and the phone deliberately: the Android
/// activity and the macOS protocol activation that receive these links can't be exercised in a test, so the
/// decision they both defer to is tested here instead of twice, badly, in places neither CI job builds.
/// </summary>
public class AgnesLinkRouteTests
{
    private const string Pin = "aa11bb22cc33dd44ee55ff6600112233445566778899aabbccddeeff00112233";

    [Fact]
    public void A_scanned_grant_is_acted_on_without_asking()
    {
        var link = PairingLink.Build("https://box:5099", grant: "GRANT-1", fingerprint: Pin);

        var route = AgnesLinkRoute.Parse(link);

        Assert.NotNull(route);
        Assert.Equal("https://box:5099", route.HostUrl);
        Assert.Equal("GRANT-1", route.Secret);
        Assert.Equal(Pin, route.Fingerprint);
        // The grant came off the host's own screen — holding it is the proof.
        Assert.True(route.AutoSubmit);
    }

    [Fact]
    public void A_typed_code_is_carried_but_never_submitted_for_you()
    {
        var route = AgnesLinkRoute.Parse("agnes://pair?host=https%3A%2F%2Fbox%3A5099&code=ABCD-EFGH");

        Assert.NotNull(route);
        Assert.Equal("ABCD-EFGH", route.Secret);
        Assert.False(route.AutoSubmit);
    }

    [Fact]
    public void A_grant_wins_over_a_code_when_a_link_somehow_carries_both()
    {
        var route = AgnesLinkRoute.Parse("agnes://pair?host=https%3A%2F%2Fbox%3A5099&code=TYPED&grant=SCANNED");

        Assert.Equal("SCANNED", route!.Secret);
        Assert.True(route.AutoSubmit);
    }

    [Fact]
    public void A_session_link_carries_the_session_so_scanning_lands_in_it()
    {
        var link = PairingLink.Build("https://box:5099", grant: "G", sessionId: "sess-7");

        Assert.Equal("sess-7", AgnesLinkRoute.Parse(link)!.SessionId);
    }

    [Fact]
    public void A_host_with_a_real_certificate_simply_has_no_pin()
    {
        var route = AgnesLinkRoute.Parse(PairingLink.Build("https://agnes.example.com", grant: "G"));

        Assert.Null(route!.Fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("agnes://pair?grant=G")]              // a secret with nowhere to send it
    [InlineData("https://box:5099")]                  // not a pairing link at all
    [InlineData("nonsense")]
    public void A_link_that_names_no_host_is_no_route(string? link)
        => Assert.Null(AgnesLinkRoute.Parse(link));

    [Fact]
    public void Surrounding_whitespace_from_a_copy_paste_is_tolerated()
    {
        var route = AgnesLinkRoute.Parse("  agnes://pair?host=https%3A%2F%2Fbox%3A5099&grant=G\n");

        Assert.Equal("https://box:5099", route!.HostUrl);
    }

    // ---- view links: a pointer, never a credential ----

    [Fact]
    public void A_session_link_is_a_view_link_and_carries_no_secret()
    {
        var link = SessionLink.Build("https://box:5099", "sess-7", sequence: 42, fingerprint: Pin);

        var route = AgnesLinkRoute.Parse(link);

        Assert.Equal(AgnesLinkKind.ViewSession, route!.Kind);
        Assert.Equal("https://box:5099", route.HostUrl);
        Assert.Equal("sess-7", route.SessionId);
        Assert.Equal(42, route.Sequence);
        Assert.Equal(Pin, route.Fingerprint);
        Assert.Null(route.Secret);
        Assert.False(route.AutoSubmit);
    }

    [Fact]
    public void A_grant_bolted_onto_a_view_link_is_refused()
    {
        // The reason this matters: if a view link could carry a grant, a message anyone can send would be
        // able to talk a stranger's client into enrolling with a host they've never heard of.
        var route = AgnesLinkRoute.Parse(
            "agnes://session?host=https%3A%2F%2Fbox%3A5099&session=sess-7&grant=STOLEN&code=ALSO-STOLEN");

        Assert.Equal(AgnesLinkKind.ViewSession, route!.Kind);
        Assert.Null(route.Secret);
        Assert.False(route.AutoSubmit);
    }

    [Fact]
    public void A_view_link_with_no_session_is_no_route()
        => Assert.Null(AgnesLinkRoute.Parse("agnes://session?host=https%3A%2F%2Fbox%3A5099"));

    [Fact]
    public void A_link_with_no_moment_opens_at_the_live_tail()
        => Assert.Null(AgnesLinkRoute.Parse(SessionLink.Build("https://box:5099", "sess-7"))!.Sequence);

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("not-a-number")]
    public void A_nonsense_sequence_is_treated_as_no_sequence(string sequence)
        => Assert.Null(AgnesLinkRoute.Parse(
            $"agnes://session?host=https%3A%2F%2Fbox&session=s&seq={sequence}")!.Sequence);

    [Fact]
    public void Pair_and_view_links_stay_distinguishable()
    {
        Assert.True(PairingLink.IsPairLink(PairingLink.Build("https://box", grant: "G")));
        Assert.False(PairingLink.IsPairLink(SessionLink.Build("https://box", "s")));
        Assert.True(SessionLink.IsSessionLink(SessionLink.Build("https://box", "s")));
        Assert.False(SessionLink.IsSessionLink(PairingLink.Build("https://box", grant: "G")));
    }

    [Fact]
    public void A_pair_link_may_still_name_a_session_because_scanning_one_in_person_is_deliberate()
    {
        var route = AgnesLinkRoute.Parse(PairingLink.Build("https://box:5099", grant: "G", sessionId: "sess-7"));

        Assert.Equal(AgnesLinkKind.Pair, route!.Kind);
        Assert.Equal("sess-7", route.SessionId);
        Assert.True(route.AutoSubmit);
    }
}
