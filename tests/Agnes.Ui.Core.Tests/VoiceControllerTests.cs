using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.Voice;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// Proves the voice CORE end-to-end with a FAKE provider: the hidden controller maps transcripts onto the
/// existing <see cref="IAgnesHost"/> calls, the intent mapper is a pure function, and the privacy summarizer
/// structurally omits raw tool/file detail by default. No real STT/TTS engine is involved (device/cloud
/// engines are deferred — see <c>Agnes.Abstractions/Voice.cs</c>).
/// </summary>
public class VoiceControllerTests
{
    private const string Target = "target-session";
    private static VoiceOptions Options(bool forwardRaw = false) => new("fake", ForwardRawContext: forwardRaw);

    // ---- controller → host wiring (AC1, AC2) -------------------------------------------------

    [Fact]
    public async Task Spoken_instruction_is_relayed_to_the_target_session()
    {
        var host = new RecordingHost(Target);
        var provider = new FakeVoiceProvider();
        await using var controller = NewController(host, provider);

        await controller.AttachAsync();
        var action = await controller.ProcessTranscriptAsync("tell it to also add tests");

        Assert.Equal(VoiceActionKind.Prompt, action.Kind);
        var (session, content) = Assert.Single(host.Prompts);
        Assert.Equal(Target, session);
        var text = Assert.IsType<TextContent>(Assert.Single(content));
        Assert.Contains("add tests", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Listening_loop_relays_a_scripted_transcript()
    {
        var host = new RecordingHost(Target);
        var provider = new FakeVoiceProvider();
        await using var controller = NewController(host, provider);

        provider.Push("tell it to also add tests");
        provider.Complete();
        await controller.ListenAsync();

        var (_, content) = Assert.Single(host.Prompts);
        Assert.Contains("add tests", ((TextContent)content[0]).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Affirmative_while_a_permission_is_open_approves_it()
    {
        var host = new RecordingHost(Target, events:
        [
            new PermissionRequestedEvent("req-1", "tool-1", "Run rm -rf",
            [
                new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
                new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce),
            ]) { Sequence = 1 },
        ]);
        var provider = new FakeVoiceProvider();
        await using var controller = NewController(host, provider);

        await controller.AttachAsync();
        var action = await controller.ProcessTranscriptAsync("yes, allow it");

        Assert.Equal(VoiceActionKind.RespondPermission, action.Kind);
        var (session, requestId, optionId) = Assert.Single(host.PermissionResponses);
        Assert.Equal(Target, session);
        Assert.Equal("req-1", requestId);
        Assert.Equal("allow", optionId);
    }

    [Fact]
    public async Task Negative_while_a_permission_is_open_rejects_it()
    {
        var host = new RecordingHost(Target, events:
        [
            new PermissionRequestedEvent("req-1", "tool-1", "Run rm -rf",
            [
                new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
                new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce),
            ]) { Sequence = 1 },
        ]);
        var provider = new FakeVoiceProvider();
        await using var controller = NewController(host, provider);

        await controller.AttachAsync();
        var action = await controller.ProcessTranscriptAsync("no, deny that");

        Assert.Equal(VoiceActionKind.RespondPermission, action.Kind);
        var (_, _, optionId) = Assert.Single(host.PermissionResponses);
        Assert.Equal("deny", optionId);
    }

    [Fact]
    public async Task Switch_to_plan_mode_calls_set_mode()
    {
        var host = new RecordingHost(Target, modes: [new SessionMode("plan", "Plan"), new SessionMode("code", "Code")]);
        var provider = new FakeVoiceProvider();
        await using var controller = NewController(host, provider);

        await controller.AttachAsync();
        var action = await controller.ProcessTranscriptAsync("switch to plan mode");

        Assert.Equal(VoiceActionKind.SetMode, action.Kind);
        var (session, modeId) = Assert.Single(host.ModeChanges);
        Assert.Equal(Target, session);
        Assert.Equal("plan", modeId);
    }

    [Fact]
    public async Task Unrecognized_utterance_takes_no_action_and_signals_the_user()
    {
        var host = new RecordingHost(Target);
        var provider = new FakeVoiceProvider();
        await using var controller = NewController(host, provider);

        string? notUnderstood = null;
        controller.NotUnderstood += t => notUnderstood = t;

        await controller.AttachAsync();
        var action = await controller.ProcessTranscriptAsync("what's the weather like today");

        Assert.Equal(VoiceActionKind.None, action.Kind);
        Assert.Empty(host.Prompts);
        Assert.Empty(host.PermissionResponses);
        Assert.Empty(host.ModeChanges);
        Assert.Equal("what's the weather like today", notUnderstood);
    }

    // ---- turn-completion read-back (SpeakAsync) ----------------------------------------------

    [Fact]
    public async Task Turn_completion_speaks_a_summary()
    {
        var host = new RecordingHost(Target, events:
        [
            new MessageChunkEvent(MessageRole.Assistant, new TextContent("Added the tests you asked for")) { Sequence = 1 },
        ]);
        var provider = new FakeVoiceProvider();
        await using var controller = NewController(host, provider);

        await controller.AttachAsync();
        host.View.Apply(new TurnEndedEvent(StopReason.EndTurn) { Sequence = 2 });
        await controller.LastReadbackCompleted;

        var spoken = Assert.Single(provider.Spoken);
        Assert.Contains("Added the tests", spoken, StringComparison.Ordinal);
    }

    // ---- pure intent mapper (AC5/AC7) --------------------------------------------------------

    [Theory]
    [InlineData("tell it to also add tests", VoiceActionKind.Prompt)]
    [InlineData("ask it to run the build", VoiceActionKind.Prompt)]
    [InlineData("tell the agent to commit the changes", VoiceActionKind.Prompt)]
    [InlineData("hey there, how's it going", VoiceActionKind.None)]
    [InlineData("um", VoiceActionKind.None)]
    [InlineData("", VoiceActionKind.None)]
    public void Intent_mapper_classifies_utterances_with_no_open_permission(string transcript, VoiceActionKind expected)
    {
        var action = VoiceIntentMapper.Map(transcript, new VoiceSessionState());
        Assert.Equal(expected, action.Kind);
    }

    [Theory]
    [InlineData("yes", "allow")]
    [InlineData("allow it", "allow")]
    [InlineData("go ahead", "allow")]
    [InlineData("no", "deny")]
    [InlineData("deny that", "deny")]
    [InlineData("stop", "deny")]
    public void Intent_mapper_maps_permission_responses_when_a_request_is_open(string transcript, string expectedOption)
    {
        var state = new VoiceSessionState(new VoicePermissionState("r1",
        [
            new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
            new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce),
        ]));

        var action = VoiceIntentMapper.Map(transcript, state);

        Assert.Equal(VoiceActionKind.RespondPermission, action.Kind);
        Assert.Equal(expectedOption, action.OptionId);
    }

    [Fact]
    public void Affirmative_with_no_open_permission_is_not_an_action()
    {
        // "yes" only means "approve" when there is actually something to approve (AC5 conservatism).
        var action = VoiceIntentMapper.Map("yes", new VoiceSessionState());
        Assert.Equal(VoiceActionKind.None, action.Kind);
    }

    [Fact]
    public void Explicit_relay_beats_a_keyword_in_its_remainder()
    {
        var state = new VoiceSessionState(new VoicePermissionState("r1",
            [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce)]));

        // "tell it to stop the server" is a prompt, not a permission denial, despite containing "stop".
        var action = VoiceIntentMapper.Map("tell it to stop the server", state);
        Assert.Equal(VoiceActionKind.Prompt, action.Kind);
        Assert.Contains("stop the server", action.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_instruction_maps_identically_regardless_of_provider()
    {
        // AC7: controller logic is provider-independent — the pure mapper only sees text + state.
        var a = VoiceIntentMapper.Map("tell it to add tests", new VoiceSessionState());
        var b = VoiceIntentMapper.Map("tell it to add tests", new VoiceSessionState());
        Assert.Equal(a, b);
    }

    // ---- privacy default (AC3) ---------------------------------------------------------------

    [Fact]
    public void Summarizer_excludes_tool_args_and_file_paths_by_default()
    {
        var summarizer = new VoiceContextSummarizer();
        IReadOnlyList<SessionEvent> events =
        [
            new ToolCallEvent("tc-1", "Edit /home/dev/src/secret.cs", ToolKind.Edit, ToolCallStatus.Completed,
            [
                new DiffContent("/home/dev/src/secret.cs", "old", "SECRET_TOKEN=hunter2"),
                new TextContent("rm -rf /home/dev/private"),
            ]) { Sequence = 1 },
            new MessageChunkEvent(MessageRole.Assistant, new ResourceLinkContent("/home/dev/src/secret.cs", "secret.cs")) { Sequence = 2 },
            new MessageChunkEvent(MessageRole.Assistant, new TextContent("Done, I updated the configuration")) { Sequence = 3 },
            new PermissionRequestedEvent("r1", "tc-1", "Allow edit to /home/dev/src/secret.cs", []) { Sequence = 4 },
        ];

        var context = summarizer.Summarize(events, Options(forwardRaw: false)).ToPromptContext();

        Assert.False(context.Contains("/home/dev/src/secret.cs", StringComparison.Ordinal), "file path leaked");
        Assert.False(context.Contains("SECRET_TOKEN", StringComparison.Ordinal), "file contents leaked");
        Assert.False(context.Contains("rm -rf", StringComparison.Ordinal), "tool-call argument leaked");
        // The agent's own natural-language response is still available to speak back.
        Assert.Contains("updated the configuration", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarizer_may_include_raw_detail_when_the_flag_is_explicitly_set()
    {
        var summarizer = new VoiceContextSummarizer();
        IReadOnlyList<SessionEvent> events =
        [
            new ToolCallEvent("tc-1", "Edit /home/dev/src/secret.cs", ToolKind.Edit, ToolCallStatus.Completed,
            [
                new DiffContent("/home/dev/src/secret.cs", "old", "SECRET_TOKEN=hunter2"),
            ]) { Sequence = 1 },
        ];

        var context = summarizer.Summarize(events, Options(forwardRaw: true)).ToPromptContext();

        Assert.Contains("/home/dev/src/secret.cs", context, StringComparison.Ordinal);
        Assert.Contains("SECRET_TOKEN", context, StringComparison.Ordinal);
    }

    // ---- plugin point (AC6) ------------------------------------------------------------------

    [Fact]
    public void A_client_with_no_voice_provider_advertises_no_voice_capability()
    {
        var caps = ClientCapabilityBuilder.Build("c1", "wasm", supportsDynamicPlugins: false, ClientPluginSet.Empty);
        Assert.DoesNotContain(ClientCapabilityIds.Voice, caps.CapabilityIds);
    }

    [Fact]
    public void A_registered_voice_provider_populates_the_plugin_point_and_capability()
    {
        var plugins = ClientPluginHost.FromModules([new VoiceModule(new FakeVoiceProvider())]);
        Assert.NotNull(plugins.VoiceProviders.Find("fake"));

        var caps = ClientCapabilityBuilder.Build("c1", "desktop", supportsDynamicPlugins: true, plugins);
        Assert.Contains(ClientCapabilityIds.Voice, caps.CapabilityIds);
        Assert.Contains("client.voice", caps.PluginPointIds);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static VoiceControllerViewModel NewController(RecordingHost host, FakeVoiceProvider provider) =>
        new(host, provider, new VoiceContextSummarizer(), Target, Options());

    private sealed class VoiceModule(IVoiceProvider provider) : Agnes.Ui.Core.Plugins.IClientPluginModule
    {
        public void Register(Agnes.Ui.Core.Plugins.ClientPluginCollector collector) => collector.AddVoiceProvider(provider);
    }
}

/// <summary>A scripted, in-memory voice provider: transcripts are pushed by the test; SpeakAsync records.</summary>
internal sealed class FakeVoiceProvider : IVoiceProvider
{
    private readonly Channel<string> _transcripts = Channel.CreateUnbounded<string>();

    public string Id => "fake";

    public List<string> Spoken { get; } = [];

    public void Push(string transcript) => _transcripts.Writer.TryWrite(transcript);

    public void Complete() => _transcripts.Writer.TryComplete();

    public Task<ISpeechToTextSession> StartListeningAsync(VoiceOptions options, CancellationToken ct = default)
        => Task.FromResult<ISpeechToTextSession>(new Session(_transcripts.Reader));

    public Task SpeakAsync(string text, VoiceOptions options, CancellationToken ct = default)
    {
        Spoken.Add(text);
        return Task.CompletedTask;
    }

    private sealed class Session(ChannelReader<string> reader) : ISpeechToTextSession
    {
        public IAsyncEnumerable<string> Transcripts => reader.ReadAllAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="IAgnesHost"/> that records the three calls the voice controller drives and serves a
/// pre-seeded <see cref="SessionView"/>. Only the members the controller touches are meaningful; the rest
/// satisfy the interface.</summary>
internal sealed class RecordingHost : IAgnesHost
{
    public RecordingHost(string sessionId, IReadOnlyList<SessionEvent>? events = null, IReadOnlyList<SessionMode>? modes = null)
    {
        var info = new SessionInfo(sessionId, "fake-adapter", "/tmp/work", HeadSequence: events?.Count ?? 0, Modes: modes);
        View = new SessionView(sessionId);
        View.ApplySnapshot(new SessionSnapshot(info, events ?? [], info.HeadSequence));
    }

    public SessionView View { get; }

    public List<(string SessionId, IReadOnlyList<ContentBlock> Content)> Prompts { get; } = [];
    public List<(string SessionId, string RequestId, string OptionId)> PermissionResponses { get; } = [];
    public List<(string SessionId, string ModeId)> ModeChanges { get; } = [];

    public Task<SessionView> SubscribeAsync(string sessionId, long since = 0) => Task.FromResult(View);

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
    {
        Prompts.Add((sessionId, content));
        return Task.CompletedTask;
    }

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
    {
        PermissionResponses.Add((sessionId, requestId, optionId));
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string sessionId, string modeId)
    {
        ModeChanges.Add((sessionId, modeId));
        return Task.CompletedTask;
    }

    // ---- unused surface -------------------------------------------------------------------
    public string HostUrl => "fake://voice";
    public AgnesConnectionState State => AgnesConnectionState.Connected;
    public event Action<AgnesConnectionState>? StateChanged { add { _ = value; } remove { _ = value; } }
    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged { add { _ = value; } remove { _ = value; } }
    public event Action<InboxRun>? InboxRunReceived { add { _ = value; } remove { _ = value; } }
    public event Action<string, long, bool>? ReadStateChanged { add { _ = value; } remove { _ = value; } }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HostInfo> GetHostInfoAsync() => Task.FromResult(new HostInfo("h", "Host", "1.0"));
    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync() => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null)
        => Task.FromResult(new SessionInfo("s", adapterId, workingDirectory, 0));
    public Task CancelAsync(string sessionId) => Task.CompletedTask;
    public Task<GitStatus> GetGitStatusAsync(string sessionId) => Task.FromResult(new GitStatus(false, null, false, []));
    public Task<GitCommitResult> GitCommitAsync(string sessionId, string message) => Task.FromResult(new GitCommitResult(true, "ok"));
    public Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data) => Task.FromResult(fileName);
    public Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request) => Task.FromResult(new ScheduledTask("t", request.AdapterId, request.WorkingDirectory, request.Prompt, request.IntervalSeconds, true));
    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync() => Task.FromResult<IReadOnlyList<ScheduledTask>>([]);
    public Task RemoveScheduledTaskAsync(string taskId) => Task.CompletedTask;
    public Task<IReadOnlyList<InboxRun>> GetInboxAsync() => Task.FromResult<IReadOnlyList<InboxRun>>([]);
    public Task MarkSessionReadAsync(string sessionId, long sequence) => Task.CompletedTask;
    public Task MarkSessionUnreadAsync(string sessionId) => Task.CompletedTask;
    public Task PauseSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task ResumeSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task DeleteSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task StopSessionAsync(string sessionId) => Task.CompletedTask;
    public Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId) => Task.FromResult<SandboxStatus?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
