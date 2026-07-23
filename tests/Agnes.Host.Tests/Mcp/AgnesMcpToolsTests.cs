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
        => new(backend, new FakeMcpAuthenticator(ValidToken), new FixedTokenSource(presentedToken));

    [Fact]
    public void ToolsList_exposes_the_expected_tool_set_with_schemas()
    {
        var tools = BuildToolDescriptors();

        var names = tools.Select(t => t.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "get_session_status", "list_open_approvals", "list_sessions",
                "read_session_transcript", "respond_permission", "send_prompt", "set_mode",
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
        var instance = new AgnesMcpTools(new FakeAgnesMcpBackend(), new FakeMcpAuthenticator(ValidToken), new FixedTokenSource(ValidToken));
        var options = new McpServerToolCreateOptions();
        return typeof(AgnesMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, instance, options))
            .ToArray();
    }
}
