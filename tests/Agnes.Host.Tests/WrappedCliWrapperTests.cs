using System.Text;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Wrap;
using Agnes.Wrap.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The local CLI wrapper (sessions/07): spawn a CLI in a real PTY, tee/capture its I/O into an Agnes session,
/// register it with a host, and hand it off via the connectivity/03 Replay path. These run against a real
/// pseudo-terminal (<c>/dev/ptmx</c>); each test skips with a clear reason if PTY allocation genuinely fails.
/// </summary>
public sealed class WrappedCliWrapperTests : IDisposable
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private readonly List<string> _dirs = [];

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agnes-wrap-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SessionManager NewHost(WrappedCliAdapter adapter) => new(
        new PluginRegistry<IAgentAdapter>([adapter], a => a.Descriptor.Id),
        new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

    // Fail fast + skip (never hang the suite) if this environment can't hand out a PTY at all.
    private static async Task EnsurePtyAsync()
    {
        try
        {
            await using var probe = await new PortaPtySpawner().SpawnAsync(
                new PtyLaunch("sh", ["-c", "exit 0"], Path.GetTempPath()));
        }
        catch (Exception ex)
        {
            // Dynamic skip (xunit v2): don't fail the suite where a PTY genuinely can't be allocated.
            throw Xunit.Sdk.SkipException.ForSkip($"PTY allocation is unavailable in this environment: {ex.Message}");
        }
    }

    private static async Task<IReadOnlyList<SessionEvent>> WaitForAsync(
        SessionManager host, string sessionId, Func<IReadOnlyList<SessionEvent>, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var events = (await host.GetSnapshotAsync(sessionId, 0)).Events;
            if (predicate(events))
            {
                return events;
            }

            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private static string Transcript(IReadOnlyList<SessionEvent> events)
        => string.Concat(events.OfType<TerminalOutputEvent>().Select(e => e.Data));

    [Fact]
    public async Task Wrapped_session_captures_the_CLIs_terminal_output()
    {
        await EnsurePtyAsync();
        var adapter = new WrappedCliAdapter("sh", ["-c", "echo wrapped-hello"], new PortaPtySpawner());
        await using var host = NewHost(adapter);

        var session = await host.OpenSessionAsync(WrappedCliAdapter.AdapterId, NewDir(), useSandbox: false);
        var events = await WaitForAsync(host, session.SessionId, e => Transcript(e).Contains("wrapped-hello", StringComparison.Ordinal));

        Assert.Contains("wrapped-hello", Transcript(events), StringComparison.Ordinal);
        Assert.Contains(events, e => e is TerminalOutputEvent);
    }

    [Fact]
    public async Task Wrapped_session_captures_input_fed_to_the_CLI()
    {
        await EnsurePtyAsync();
        var adapter = new WrappedCliAdapter("cat", spawner: new PortaPtySpawner());
        await using var host = NewHost(adapter);

        var opened = await host.OpenSessionAsync(WrappedCliAdapter.AdapterId, NewDir(), useSandbox: false);
        var terminal = adapter.LastSession!;

        // cat echoes stdin back on stdout; the PTY (cooked mode) also echoes it — either way the input we feed
        // shows up in the captured terminal stream, no separate input-event kind needed.
        await terminal.WriteInputAsync(Encoding.UTF8.GetBytes("hello-stdin\n"));
        var events = await WaitForAsync(host, opened.SessionId, e => Transcript(e).Contains("hello-stdin", StringComparison.Ordinal));

        Assert.Contains("hello-stdin", Transcript(events), StringComparison.Ordinal);

        await terminal.SendEndOfInputAsync(); // let cat see EOF and exit cleanly
    }

    [Fact]
    public async Task Wrapped_session_is_registered_and_handoff_capable_via_replay()
    {
        await EnsurePtyAsync();

        // Source host: a wrapped CLI that prints a line, so there's a transcript to carry.
        var sourceAdapter = new WrappedCliAdapter("sh", ["-c", "echo wrapped-hello"], new PortaPtySpawner());
        await using var source = NewHost(sourceAdapter);
        var opened = await source.OpenSessionAsync(WrappedCliAdapter.AdapterId, NewDir(), useSandbox: false);
        await WaitForAsync(source, opened.SessionId, e => Transcript(e).Contains("wrapped-hello", StringComparison.Ordinal));

        // It is a first-class, catalogued Agnes session.
        var summary = Assert.Single(await source.ListSessionSummariesAsync());
        Assert.Equal(opened.SessionId, summary.SessionId);
        Assert.Equal(WrappedCliAdapter.AdapterId, summary.AdapterId);

        // ...and it reports Replay handoff support (Agnes owns the transcript).
        Assert.Equal(HandoffSupport.Replay, source.HandoffSupportFor(WrappedCliAdapter.AdapterId));

        var state = await source.PrepareHandoffAsync(opened.SessionId);
        Assert.Equal(HandoffSupport.Replay, state.Mode);
        Assert.Contains(state.SeedEvents.OfType<TerminalOutputEvent>(), e => e.Data.Contains("wrapped-hello", StringComparison.Ordinal));

        // Target host: accept the handoff. The child's log opens with a ForkedFromEvent naming the source —
        // the reconstructed session on a second in-process host (connectivity/03 pattern).
        var targetAdapter = new WrappedCliAdapter("sh", ["-c", "exit 0"], new PortaPtySpawner());
        await using var target = NewHost(targetAdapter);
        var accepted = await target.AcceptHandoffAsync(state, NewDir());

        var childSnap = await target.GetSnapshotAsync(accepted.SessionId, 0);
        var marker = Assert.Single(childSnap.Events.OfType<ForkedFromEvent>());
        Assert.Equal(opened.SessionId, marker.ParentSessionId);
        Assert.Contains(await target.ListSessionSummariesAsync(), s => s.SessionId == accepted.SessionId);
    }

    [Fact]
    public async Task Exiting_the_wrapped_CLI_ends_the_session_cleanly()
    {
        await EnsurePtyAsync();
        var adapter = new WrappedCliAdapter("sh", ["-c", "echo bye"], new PortaPtySpawner());
        var host = NewHost(adapter);

        await host.OpenSessionAsync(WrappedCliAdapter.AdapterId, NewDir(), useSandbox: false);
        var terminal = adapter.LastSession!;

        // The wrapped command runs to completion on its own; its exit is observed without a fault/relaunch.
        var exitCode = await terminal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, exitCode);
        Assert.True(terminal.HasExited);

        // Teardown is clean: disposing the host tears the session (and its PTY) down with no leak/throw.
        await host.DisposeAsync();
        Assert.True(terminal.HasExited);
    }
}
