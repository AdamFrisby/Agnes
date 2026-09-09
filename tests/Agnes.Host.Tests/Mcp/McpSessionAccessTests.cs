using Agnes.Host.Mcp;
using Agnes.Host.Sharing;
using Agnes.Protocol;

namespace Agnes.Host.Tests.Mcp;

/// <summary>
/// <c>/mcp-agnes</c> is a second front door onto every session on the host, and it used to be an unguarded
/// one: holding <em>any</em> device token was enough to list every session, read every transcript, prompt
/// every agent and answer every permission — authority the SignalR hub refuses to the same token. These tests
/// pin that each tool now asks the SAME <see cref="SessionAccessDecider"/> the hub asks, for the same verb,
/// and that the catalogue tools filter rather than advertise what they would then deny.
/// </summary>
public sealed class McpSessionAccessTests
{
    private const string DeviceToken = "device-token";
    private const string Mine = "session-mine";
    private const string Theirs = "session-theirs";

    private static (AgnesMcpTools Tools, FakeAgnesMcpBackend Backend, StubSessionAccess Access) Build(
        StubSessionAccess? access = null)
    {
        var backend = new FakeAgnesMcpBackend();
        var stub = access ?? StubSessionAccess.OnlyFor(Mine);
        var tools = new AgnesMcpTools(
            backend,
            new FakeMcpAuthenticator(DeviceToken),
            new FixedTokenSource(DeviceToken),
            new SessionMcpTokens(),
            Display.DisplayFixture.NoDisplays(),
            stub);
        return (tools, backend, stub);
    }

    [Fact]
    public async Task Every_session_scoped_tool_refuses_a_session_the_hub_would_refuse()
    {
        var (tools, backend, _) = Build();

        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.GetSessionStatus(Theirs));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.SendPrompt(Theirs, "do it"));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.SetMode(Theirs, "code"));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.RespondPermission(Theirs, "req", "allow"));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.ReadSessionTranscript(Theirs));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.ArmGoal("keep going", 60, sessionId: Theirs));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.ListGoals(Theirs));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.SendUserFile("out.png", null, Theirs));
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.ReportStatus("busy", Theirs));

        // A refusal is a refusal all the way down: nothing reached the host.
        Assert.Null(backend.SentPrompt);
        Assert.Null(backend.SetModeCall);
        Assert.Null(backend.RespondedPermission);
        Assert.Null(backend.TranscriptRequest);
        Assert.Empty(backend.Armed);
        Assert.Empty(backend.Shared);
        Assert.Empty(backend.Statuses);
    }

    [Fact]
    public async Task The_same_tools_work_on_the_session_the_caller_may_reach()
    {
        var (tools, backend, _) = Build();

        await tools.SendPrompt(Mine, "do it");
        await tools.SetMode(Mine, "code");
        await tools.RespondPermission(Mine, "req", "allow");
        await tools.ReadSessionTranscript(Mine);
        await tools.ArmGoal("keep going", 60, sessionId: Mine);
        await tools.SendUserFile("out.png", null, Mine);
        await tools.ReportStatus("busy", Mine);

        Assert.Equal((Mine, "do it"), backend.SentPrompt);
        Assert.Equal((Mine, "code"), backend.SetModeCall);
        Assert.Equal((Mine, "req", "allow"), backend.RespondedPermission);
        Assert.Equal((Mine, false), backend.TranscriptRequest);
        Assert.Equal(Mine, Assert.Single(backend.Armed).SessionId);
        Assert.Equal(Mine, Assert.Single(backend.Shared).SessionId);
        Assert.Equal(Mine, Assert.Single(backend.Statuses).SessionId);
    }

    [Fact]
    public async Task Each_tool_asks_for_the_verb_its_hub_equivalent_requires()
    {
        // Reading asks to Subscribe, driving asks to Prompt, answering a permission asks to Approve. Getting
        // this wrong is how a view-only collaborator ends up able to type.
        var (tools, backend, access) = Build(StubSessionAccess.AllowAll());
        backend.Status = new McpSessionStatus(Mine, "scripted", "idle", null, [], 0, 0);

        await tools.ReadSessionTranscript(Mine);
        await tools.GetSessionStatus(Mine);
        await tools.SendPrompt(Mine, "x");
        await tools.SetMode(Mine, "code");
        await tools.RespondPermission(Mine, "r", "allow");

        Assert.Equal(
            [
                (Mine, SessionAccessKind.Subscribe),
                (Mine, SessionAccessKind.Subscribe),
                (Mine, SessionAccessKind.Prompt),
                (Mine, SessionAccessKind.Prompt),
                (Mine, SessionAccessKind.Approve),
            ],
            access.Asked);
    }

    [Fact]
    public async Task A_view_only_share_can_read_but_not_drive()
    {
        var (tools, backend, _) = Build(StubSessionAccess.OnlyFor(Mine, SessionAccessKind.Subscribe));

        await tools.ReadSessionTranscript(Mine);
        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.SendPrompt(Mine, "x"));

        Assert.Equal((Mine, false), backend.TranscriptRequest);
        Assert.Null(backend.SentPrompt);
    }

    [Fact]
    public async Task The_catalogue_lists_only_what_the_caller_could_subscribe_to()
    {
        var (tools, backend, _) = Build();
        backend.Sessions.Add(new McpSessionSummary(Mine, "scripted", "mine", "idle", 0));
        backend.Sessions.Add(new McpSessionSummary(Theirs, "scripted", "somebody else's project", "idle", 0));

        var listed = await tools.ListSessions();

        // Not even the id — a leaking catalogue names other people's folders to someone who cannot open them.
        Assert.Equal(Mine, Assert.Single(listed).SessionId);
    }

    [Fact]
    public async Task Open_approvals_and_goals_are_filtered_the_same_way()
    {
        var (tools, backend, _) = Build();
        backend.Approvals.Add(new OpenApproval(Mine, "req-1", "Write file", "tool-1", DateTimeOffset.UnixEpoch));
        backend.Approvals.Add(new OpenApproval(Theirs, "req-2", "Delete everything", "tool-2", DateTimeOffset.UnixEpoch));
        backend.Goals.Add(new SessionGoal("g1", Mine, "mine", 60, 5, 0, true, DateTimeOffset.UnixEpoch));
        backend.Goals.Add(new SessionGoal("g2", Theirs, "theirs", 60, 5, 0, true, DateTimeOffset.UnixEpoch));

        Assert.Equal("req-1", Assert.Single(await tools.ListOpenApprovals()).RequestId);
        Assert.Equal("g1", Assert.Single(await tools.ListGoals("all")).Id);
    }

    [Fact]
    public async Task Disarming_a_foreign_goal_is_refused_even_though_it_names_no_session()
    {
        // disarm_goal takes a goal id, not a session id — so the check has to resolve the goal's own session
        // first, or "which session is this?" becomes a way around the gate.
        var (tools, backend, _) = Build();
        backend.Goals.Add(new SessionGoal("g2", Theirs, "theirs", 60, 5, 0, true, DateTimeOffset.UnixEpoch));
        backend.Goals.Add(new SessionGoal("g1", Mine, "mine", 60, 5, 0, true, DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAsync<McpForbiddenException>(() => tools.DisarmGoal("g2", "not mine"));
        Assert.Empty(backend.Disarmed);

        await tools.DisarmGoal("g1", "done");
        Assert.Equal(("g1", "done"), Assert.Single(backend.Disarmed));
    }

    [Fact]
    public async Task An_agents_own_session_token_never_consults_the_sharing_layer()
    {
        // A session token's authority comes from the token, not from a share: the session it may act on is
        // fixed, so asking the sharing layer would be asking the wrong question.
        var sessionTokens = new SessionMcpTokens();
        var backend = new FakeAgnesMcpBackend();
        var access = StubSessionAccess.OnlyFor("nothing-at-all");
        var tools = new AgnesMcpTools(
            backend,
            new FakeMcpAuthenticator(DeviceToken),
            new FixedTokenSource(sessionTokens.Issue(Theirs)),
            sessionTokens,
            Display.DisplayFixture.NoDisplays(),
            access);

        await tools.ReportStatus("working");

        Assert.Equal(Theirs, Assert.Single(backend.Statuses).SessionId);
        Assert.Empty(access.Asked);
    }
}
