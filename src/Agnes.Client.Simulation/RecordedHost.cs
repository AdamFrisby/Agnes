using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Recording;

namespace Agnes.Client.Simulation;

/// <summary>
/// An <see cref="IAgnesHost"/> that replays recorded sessions as real test data. Each recording is
/// offered as an "agent"; opening and subscribing to it plays the captured event stream back with
/// its original timing (scale via <c>speed</c>: use a large value for instant replay in tests).
/// </summary>
public sealed class RecordedHost : IAgnesHost
{
    private readonly IReadOnlyDictionary<string, SessionRecording> _byId;
    private readonly double _speed;
    private readonly ConcurrentDictionary<string, RecordedSession> _sessions = new();
    private int _counter;

    public RecordedHost(string hostUrl, IReadOnlyList<SessionRecording> recordings, double speed = 1.0)
    {
        HostUrl = hostUrl;
        _speed = speed;
        _byId = recordings
            .Select((r, i) => (Id: Slug(r.Name, i), Recording: r))
            .ToDictionary(x => x.Id, x => x.Recording);
    }

    public string HostUrl { get; }
    public AgnesConnectionState State { get; private set; } = AgnesConnectionState.Disconnected;
    public event Action<AgnesConnectionState>? StateChanged;

#pragma warning disable CS0067
    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;
#pragma warning restore CS0067

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        State = AgnesConnectionState.Connecting;
        StateChanged?.Invoke(State);
        await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        State = AgnesConnectionState.Connected;
        StateChanged?.Invoke(State);
    }

    public Task<HostInfo> GetHostInfoAsync()
        => Task.FromResult(new HostInfo("recorded", "Recorded sessions", "0.1.0"));

    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync()
        => Task.FromResult<IReadOnlyList<AgentInfo>>(
            _byId.Select(kv => new AgentInfo(kv.Key, kv.Value.Name, "recording", Available: true)).ToArray());

    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask")
    {
        var recording = _byId.TryGetValue(adapterId, out var r) ? r : _byId.Values.FirstOrDefault();
        var id = $"rec-{Interlocked.Increment(ref _counter):x4}";
        if (recording is not null)
        {
            _sessions[id] = new RecordedSession(id, recording);
        }

        return Task.FromResult(new SessionInfo(id, adapterId, workingDirectory, 0));
    }

    public Task<SessionView> SubscribeAsync(string sessionId, long since = 0)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.View.ApplySnapshot(session.Snapshot());
            session.StartReplay(_speed); // auto-play the recording on subscribe
            return Task.FromResult(session.View);
        }

        var view = new SessionView(sessionId);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(sessionId, "recorded", string.Empty, 0), [], 0));
        return Task.FromResult(view);
    }

    // Recorded sessions are read-only playback.
    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content) => Task.CompletedTask;
    public Task CancelAsync(string sessionId) => Task.CompletedTask;
    public Task SetModeAsync(string sessionId, string modeId) => Task.CompletedTask;
    public Task<GitStatus> GetGitStatusAsync(string sessionId) => Task.FromResult(new GitStatus(false, null, false, []));
    public Task<GitCommitResult> GitCommitAsync(string sessionId, string message) => Task.FromResult(new GitCommitResult(false, "read-only"));
    public Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request) => Task.FromResult(new ScheduledTask("", request.AdapterId, request.WorkingDirectory, request.Prompt, request.IntervalSeconds, false));
    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync() => Task.FromResult<IReadOnlyList<ScheduledTask>>([]);
    public Task RemoveScheduledTaskAsync(string taskId) => Task.CompletedTask;
    public Task<IReadOnlyList<InboxRun>> GetInboxAsync() => Task.FromResult<IReadOnlyList<InboxRun>>([]);
#pragma warning disable CS0067
    public event Action<InboxRun>? InboxRunReceived;
#pragma warning restore CS0067
    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId) => Task.CompletedTask;
    public Task PauseSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task ResumeSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task DeleteSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId) => Task.FromResult<SandboxStatus?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Slug(string name, int index)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return $"{index}-{new string(chars).Trim('-')}";
    }

    private sealed class RecordedSession
    {
        private readonly SessionRecording _recording;
        private readonly object _gate = new();
        private readonly List<SessionEvent> _log = [];
        private long _seq;
        private int _started;

        public RecordedSession(string id, SessionRecording recording)
        {
            _recording = recording;
            View = new SessionView(id);
        }

        public SessionView View { get; }

        public void StartReplay(double speed)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            _ = Task.Run(() => ReplayAsync(speed));
        }

        private async Task ReplayAsync(double speed)
        {
            var start = DateTimeOffset.UtcNow;
            foreach (var recorded in _recording.Events)
            {
                var targetMs = speed <= 0 ? 0 : recorded.OffsetMs / speed;
                var wait = targetMs - (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                if (wait > 1)
                {
                    await Task.Delay((int)wait).ConfigureAwait(false);
                }

                Emit(recorded.Event);
            }
        }

        private void Emit(SessionEvent @event)
        {
            lock (_gate)
            {
                var stamped = @event with { Sequence = ++_seq, Timestamp = DateTimeOffset.UtcNow };
                _log.Add(stamped);
                View.Apply(stamped);
            }
        }

        public SessionSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new SessionSnapshot(new SessionInfo(View.SessionId, _recording.AdapterId, string.Empty, _seq), [.. _log], _seq);
            }
        }
    }
}
