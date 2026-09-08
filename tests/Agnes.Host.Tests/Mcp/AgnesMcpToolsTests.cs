using System.Reflection;
using System.Text.Json;
using Agnes.Host.Mcp;
using Agnes.Protocol;
using ModelContextProtocol.Server;

namespace Agnes.Host.Tests.Mcp;

public sealed class AgnesMcpToolsTests
{
    private const string ValidToken = "good-token";

    private static AgnesMcpTools Build(FakeAgnesMcpBackend backend, string? presentedToken = ValidToken)
        => new(backend, new FakeMcpAuthenticator(ValidToken), new FixedTokenSource(presentedToken), new SessionMcpTokens(), Display.DisplayFixture.NoDisplays());

    [Fact]
    public void ToolsList_exposes_the_expected_tool_set_with_schemas()
    {
        var tools = BuildToolDescriptors();

        var names = tools.Select(t => t.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "arm_goal",
                // The computer_* set: one tool per action, always advertised, refusing on a session that has
                // no display. See docs/display-channel.md.
                "computer_click", "computer_cursor_position", "computer_drag", "computer_frames",
                "computer_hold_key", "computer_key", "computer_move", "computer_screenshot",
                "computer_scroll", "computer_type", "computer_wait",
                "disarm_goal", "get_session_status", "list_goals", "list_open_approvals",
                "list_sessions", "read_session_transcript", "report_status", "respond_permission",
                "send_prompt", "send_user_file", "set_mode",
            },
            names);

        // Every tool carries a JSON-schema object for its inputs.
        foreach (var tool in tools)
        {
            Assert.Equal(JsonValueKind.Object, tool.ProtocolTool.InputSchema.ValueKind);
        }

        // A representative arg-bearing tool advertises its parameters in the schema.
        var sendPrompt = tools.Single(t => t.ProtocolTool.Name == "send_prompt");
        var properties = sendPrompt.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("sessionId", out _));
        Assert.True(properties.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Unauthenticated_call_is_rejected()
    {
        var backend = new FakeAgnesMcpBackend();
        var tools = Build(backend, presentedToken: null);

        await Assert.ThrowsAsync<McpUnauthenticatedException>(() => tools.ListSessions());
        Assert.Null(backend.SentPrompt);
    }

    [Fact]
    public async Task Call_with_an_unknown_token_is_rejected()
    {
        var backend = new FakeAgnesMcpBackend();
        var tools = Build(backend, presentedToken: "wrong");

        await Assert.ThrowsAsync<McpUnauthenticatedException>(() => tools.SendPrompt("s1", "hello"));
        Assert.Null(backend.SentPrompt);
    }

    [Fact]
    public async Task Authenticated_send_prompt_reaches_the_backend_with_the_right_args()
    {
        var backend = new FakeAgnesMcpBackend();
        var tools = Build(backend);

        var result = await tools.SendPrompt("session-42", "add tests");

        Assert.True(result.Ok);
        Assert.Equal(("session-42", "add tests"), backend.SentPrompt);
    }

    [Fact]
    public async Task Respond_permission_routes_through_the_approval_path()
    {
        var backend = new FakeAgnesMcpBackend();
        var tools = Build(backend);

        await tools.RespondPermission("session-9", "req-7", "allow");

        Assert.Equal(("session-9", "req-7", "allow"), backend.RespondedPermission);
    }

    [Fact]
    public async Task Set_mode_reaches_the_backend()
    {
        var backend = new FakeAgnesMcpBackend();
        var tools = Build(backend);

        await tools.SetMode("session-1", "code");

        Assert.Equal(("session-1", "code"), backend.SetModeCall);
    }

    [Fact]
    public async Task List_open_approvals_projects_the_host_approvals()
    {
        var backend = new FakeAgnesMcpBackend();
        backend.Approvals.Add(new OpenApproval("session-1", "req-1", "Write file", "tool-1", DateTimeOffset.UnixEpoch));
        var tools = Build(backend);

        var approvals = await tools.ListOpenApprovals();

        var only = Assert.Single(approvals);
        Assert.Equal("session-1", only.SessionId);
        Assert.Equal("req-1", only.RequestId);
    }

    [Fact]
    public async Task Read_transcript_defaults_to_no_raw_context_and_forwards_the_opt_in()
    {
        var backend = new FakeAgnesMcpBackend();
        var tools = Build(backend);

        await tools.ReadSessionTranscript("session-1");
        Assert.Equal(("session-1", false), backend.TranscriptRequest);

        await tools.ReadSessionTranscript("session-1", forwardRawContext: true);
        Assert.Equal(("session-1", true), backend.TranscriptRequest);
    }

    private static IReadOnlyList<McpServerTool> BuildToolDescriptors()
    {
        var instance = new AgnesMcpTools(new FakeAgnesMcpBackend(), new FakeMcpAuthenticator(ValidToken), new FixedTokenSource(ValidToken), new SessionMcpTokens(), Display.DisplayFixture.NoDisplays());
        var options = new McpServerToolCreateOptions();
        return typeof(AgnesMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, instance, options))
            .ToArray();
    }

    // ---- a sandboxed agent's session token is NOT a device token ----

    private static (AgnesMcpTools Tools, SessionMcpTokens Tokens, string Token) BuildForSession(
        FakeAgnesMcpBackend backend, string sessionId)
    {
        var tokens = new SessionMcpTokens();
        var sessionToken = tokens.Issue(sessionId);
        // Authenticator deliberately rejects it: the ONLY thing vouching for this caller is the session token.
        var tools = new AgnesMcpTools(backend, new FakeMcpAuthenticator(ValidToken), new FixedTokenSource(sessionToken), tokens, Display.DisplayFixture.NoDisplays());
        return (tools, tokens, sessionToken);
    }

    [Fact]
    public async Task A_session_token_cannot_reach_the_cross_session_tools()
    {
        // These act across every session on the host. An agent inside one VM must not gain that reach.
        var (tools, _, _) = BuildForSession(new FakeAgnesMcpBackend(), "s1");

        await Assert.ThrowsAsync<McpUnauthenticatedException>(() => tools.ListSessions());
        await Assert.ThrowsAsync<McpUnauthenticatedException>(() => tools.SendPrompt("s2", "do something"));
        await Assert.ThrowsAsync<McpUnauthenticatedException>(() => tools.ReadSessionTranscript("s2", false));
    }

    [Fact]
    public async Task A_session_token_arms_a_goal_on_its_own_session_only()
    {
        // Even naming another session must not redirect it — the token is the identity, not the argument.
        var backend = new FakeAgnesMcpBackend();
        var (tools, _, _) = BuildForSession(backend, "s1");

        await tools.ArmGoal("finish it", idleSeconds: 60, sessionId: "s2-somebody-else");

        Assert.Equal("s1", Assert.Single(backend.Armed).SessionId);
    }

    [Fact]
    public async Task A_session_token_cannot_disarm_another_sessions_goal()
    {
        var backend = new FakeAgnesMcpBackend();
        var (otherTools, _, _) = BuildForSession(backend, "s2");
        var theirGoal = await otherTools.ArmGoal("their goal", idleSeconds: 60);

        var (tools, _, _) = BuildForSession(backend, "s1");

        await Assert.ThrowsAsync<ArgumentException>(() => tools.DisarmGoal(theirGoal.Id, "not mine"));
    }

    [Fact]
    public async Task A_session_token_lists_only_its_own_goals()
    {
        var backend = new FakeAgnesMcpBackend();
        var (mine, _, _) = BuildForSession(backend, "s1");
        var (theirs, _, _) = BuildForSession(backend, "s2");
        await mine.ArmGoal("mine", idleSeconds: 60);
        await theirs.ArmGoal("theirs", idleSeconds: 60);

        // "all" is ignored for a session caller — it can't widen its own scope.
        var listed = await mine.ListGoals(sessionId: "all");

        Assert.All(listed, g => Assert.Equal("s1", g.SessionId));
    }

    [Fact]
    public async Task A_device_token_still_reaches_everything_including_other_sessions_goals()
    {
        var backend = new FakeAgnesMcpBackend();
        var tools = Build(backend);

        await tools.ArmGoal("device-armed", idleSeconds: 60, sessionId: "s9");

        Assert.Equal("s9", Assert.Single(backend.Armed).SessionId);
        await tools.ListSessions(); // no throw: unchanged authority
    }

    [Fact]
    public async Task A_device_token_must_name_a_session_since_it_has_none_of_its_own()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => Build(new FakeAgnesMcpBackend()).ArmGoal("no target", idleSeconds: 60));

    // ---- send_user_file ----

    [Fact]
    public async Task Send_user_file_acts_on_the_calling_session_and_ignores_a_named_one()
    {
        // Same rule as the goal tools: the token IS the identity, so an agent naming somebody else's session
        // sends into its own — it can't use this to drop a file into a stranger's transcript.
        var backend = new FakeAgnesMcpBackend();
        var (tools, _, _) = BuildForSession(backend, "s1");

        var confirmation = await tools.SendUserFile("out/report.md", "the numbers", sessionId: "s2-somebody-else");

        var only = Assert.Single(backend.Shared);
        Assert.Equal(("s1", "out/report.md", "the numbers"), only);
        Assert.Equal("Sent report.md (12 KB) to the user.", confirmation);
    }

    [Fact]
    public async Task Send_user_file_with_a_device_token_requires_a_session()
    {
        var backend = new FakeAgnesMcpBackend();

        await Assert.ThrowsAsync<ArgumentException>(() => Build(backend).SendUserFile("report.md"));
        Assert.Empty(backend.Shared);
    }

    [Fact]
    public async Task Send_user_file_with_a_device_token_sends_to_the_named_session()
    {
        var backend = new FakeAgnesMcpBackend();

        await Build(backend).SendUserFile("report.md", sessionId: "s9");

        Assert.Equal("s9", Assert.Single(backend.Shared).SessionId);
    }

    [Fact]
    public async Task A_veto_reason_reaches_the_agent_as_the_tool_error()
    {
        // The whole point of the veto being reported rather than swallowed: the agent must be able to act on
        // "that file has a credential in it" instead of assuming the person received it.
        var backend = new FakeAgnesMcpBackend
        {
            ShareFailure = new InvalidOperationException("Sending 'secrets.env' was blocked: contains a credential."),
        };
        var (tools, _, _) = BuildForSession(backend, "s1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.SendUserFile("secrets.env"));

        Assert.Contains("contains a credential", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Send_user_file_advertises_its_parameters_and_is_not_read_only()
    {
        var tool = BuildToolDescriptors().Single(t => t.ProtocolTool.Name == "send_user_file");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("path", out _));
        Assert.True(properties.TryGetProperty("caption", out _));
        Assert.True(properties.TryGetProperty("sessionId", out _));

        // Only `path` is required — an agent calling it needs to know caption and sessionId are optional.
        var required = tool.ProtocolTool.InputSchema.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        Assert.Equal(["path"], required);
    }

    // ---- report_status ----

    [Fact]
    public async Task Report_status_acts_on_the_calling_session_and_ignores_a_named_one()
    {
        // Same rule as the goal tools and send_user_file: the token IS the identity, so an agent naming
        // somebody else's session reports into its own.
        var backend = new FakeAgnesMcpBackend();
        var (tools, _, _) = BuildForSession(backend, "s1");

        var ack = await tools.ReportStatus("Found the leak; patching it.", sessionId: "s2-somebody-else");

        Assert.Equal(("s1", "Found the leak; patching it."), Assert.Single(backend.Statuses));
        Assert.Equal("Noted.", ack);
    }

    [Fact]
    public async Task Report_status_with_a_device_token_requires_a_session()
    {
        var backend = new FakeAgnesMcpBackend();

        await Assert.ThrowsAsync<ArgumentException>(() => Build(backend).ReportStatus("no target"));
        Assert.Empty(backend.Statuses);
    }

    [Fact]
    public async Task Report_status_with_a_device_token_reports_for_the_named_session()
    {
        var backend = new FakeAgnesMcpBackend();

        await Build(backend).ReportStatus("On it.", sessionId: "s9");

        Assert.Equal("s9", Assert.Single(backend.Statuses).SessionId);
    }

    [Fact]
    public async Task An_unauthenticated_report_is_rejected()
    {
        var backend = new FakeAgnesMcpBackend();

        await Assert.ThrowsAsync<McpUnauthenticatedException>(
            () => Build(backend, presentedToken: null).ReportStatus("hello", sessionId: "s1"));
        Assert.Empty(backend.Statuses);
    }

    [Fact]
    public async Task A_clipped_report_is_told_so_rather_than_truncated_in_silence()
    {
        // The whole point of the acknowledgement carrying the limit: a model that reads "Noted." after losing
        // two thirds of its sentence has no reason to write a shorter one next time.
        var backend = new FakeAgnesMcpBackend
        {
            StatusResult = _ => new Agnes.Host.Sessions.StatusReportResult(
                "Rewrote the index scan and…", Clipped: true, TrimmedToFirstLine: false, MaxChars: 240),
        };
        var (tools, _, _) = BuildForSession(backend, "s1");

        var ack = await tools.ReportStatus(new string('x', 900));

        Assert.Equal(
            "Noted the first 240 characters: \"Rewrote the index scan and…\". "
            + "Keep future reports to one or two sentences under 240 characters.",
            ack);
    }

    [Fact]
    public async Task A_multi_line_report_is_told_that_only_the_first_line_was_kept()
    {
        var backend = new FakeAgnesMcpBackend
        {
            StatusResult = _ => new Agnes.Host.Sessions.StatusReportResult(
                "Starting the refactor.", Clipped: false, TrimmedToFirstLine: true, MaxChars: 240),
        };
        var (tools, _, _) = BuildForSession(backend, "s1");

        Assert.Equal(
            "Kept the first line; reports are a single line.",
            await tools.ReportStatus("Starting the refactor.\n- and then this"));
    }

    [Fact]
    public async Task A_veto_reason_reaches_the_agent_as_the_status_tool_error()
    {
        var backend = new FakeAgnesMcpBackend
        {
            StatusFailure = new InvalidOperationException("That status wasn't recorded: it names a customer."),
        };
        var (tools, _, _) = BuildForSession(backend, "s1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ReportStatus("…"));

        Assert.Contains("it names a customer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_status_states_the_limit_in_its_description_and_takes_an_optional_session()
    {
        var tool = BuildToolDescriptors().Single(t => t.ProtocolTool.Name == "report_status");

        // The model has to be able to read the budget off the tool, not discover it by being clipped.
        Assert.Contains(
            Agnes.Host.Sessions.StatusOptions.DefaultMaxCharsText,
            tool.ProtocolTool.Description ?? string.Empty,
            StringComparison.Ordinal);

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("status", out _));
        Assert.True(properties.TryGetProperty("sessionId", out _));

        var required = tool.ProtocolTool.InputSchema.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        Assert.Equal(["status"], required);
    }
}
