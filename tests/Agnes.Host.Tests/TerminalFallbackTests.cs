using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class TerminalFallbackTests
{
    private sealed class CollectingBroadcaster : ISessionBroadcaster
    {
        public ConcurrentQueue<(string SessionId, SessionEvent Event)> Published { get; } = new();

        public Task PublishAsync(string sessionId, SessionEvent @event)
        {
            Published.Enqueue((sessionId, @event));
            return Task.CompletedTask;
        }
    }

    /// <summary>A fake CLI-fallback whose handle records writes/resizes and can stream output back.</summary>
    private sealed class FakeCliFallback : ICliFallback
    {
        public List<TerminalOptions> Opened { get; } = [];
        public FakeTerminalHandle? Last { get; private set; }

        public Task<ITerminalHandle> OpenTerminalAsync(TerminalOptions options, CancellationToken cancellationToken = default)
        {
            Opened.Add(options);
            var handle = new FakeTerminalHandle($"pty-{Opened.Count}");
            Last = handle;
            return Task.FromResult<ITerminalHandle>(handle);
        }
    }

    private sealed class FakeTerminalHandle : ITerminalHandle, ITerminalOutputSource, ITerminalExitSource
    {
        public FakeTerminalHandle(string id) => TerminalId = id;

        public string TerminalId { get; }
        public List<byte[]> Writes { get; } = [];
        public List<(int Columns, int Rows)> Resizes { get; } = [];
        public bool Disposed { get; private set; }

        public event Action<string, string>? OutputReceived;
        public event Action? Exited;

        public void EmitOutput(string data) => OutputReceived?.Invoke(TerminalId, data);

        public void EmitExit() => Exited?.Invoke();

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Writes.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            Resizes.Add((columns, rows));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A scripted adapter that also exposes an interactive login command (platform/03 reuse) and a
    /// probe-counting auth status, so a test can assert the login flow re-checks auth on terminal exit.</summary>
    private sealed class LoginAgentAdapter : IAgentAdapter
    {
        public AgentDescriptor Descriptor { get; } = new() { Id = "login-agent", DisplayName = "Login Agent" };
        public ProviderLoginCommand Login { get; } = new("login-agent", ["auth", "login"]);
        public int AuthProbes { get; private set; }

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new ScriptedAgentSession());

        public ProviderLoginCommand? GetInteractiveLoginCommand() => Login;

        public Task<ProviderAuthStatus?> GetAuthStatusAsync(CancellationToken cancellationToken = default)
        {
            AuthProbes++;
            return Task.FromResult<ProviderAuthStatus?>(new ProviderAuthStatus(true, "user", "cli", null, DateTimeOffset.UtcNow));
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task OpenTerminal_returns_an_id_and_write_resize_reach_the_fallback_handle()
    {
        var adapter = new ScriptedAgentAdapter();
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var info = await manager.OpenSessionAsync("scripted", Path.Combine(Path.GetTempPath(), "term-work"), useSandbox: false);

        var terminalId = await manager.OpenTerminalAsync(info.SessionId, command: null, arguments: null, workingDirectory: null, columns: 90, rows: 30);

        Assert.Equal("pty-1", terminalId);
        var options = Assert.Single(fallback.Opened);
        Assert.Equal(90, options.Columns);
        // A null working directory defaults to the session's own working directory.
        Assert.Equal(info.WorkingDirectory, options.WorkingDirectory);

        await manager.WriteTerminalAsync(info.SessionId, terminalId, "echo hi\n"u8.ToArray());
        await manager.ResizeTerminalAsync(info.SessionId, terminalId, 120, 40);

        var handle = fallback.Last!;
        Assert.Equal("echo hi\n", System.Text.Encoding.UTF8.GetString(Assert.Single(handle.Writes)));
        Assert.Equal((120, 40), Assert.Single(handle.Resizes));
    }

    [Fact]
    public async Task Terminal_output_is_appended_to_the_session_log_as_terminal_output_events()
    {
        var adapter = new ScriptedAgentAdapter();
        var fallback = new FakeCliFallback();
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster,
            NullLoggerFactory.Instance, cliFallback: fallback);

        var info = await manager.OpenSessionAsync("scripted", Path.Combine(Path.GetTempPath(), "term-out"), useSandbox: false);
        var terminalId = await manager.OpenTerminalAsync(info.SessionId, null, null, null, 80, 24);

        // The fallback streams output; it must ride the session event stream as TerminalOutputEvents.
        fallback.Last!.EmitOutput("hello from pty");

        await WaitForAsync(() => broadcaster.Published.Any(p => p.Event is TerminalOutputEvent));

        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        var output = Assert.Single(snapshot.Events.OfType<TerminalOutputEvent>());
        Assert.Equal(terminalId, output.TerminalId);
        Assert.Equal("hello from pty", output.Data);
    }

    [Fact]
    public async Task Write_and_resize_are_no_ops_for_an_unknown_terminal_id()
    {
        var adapter = new ScriptedAgentAdapter();
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var info = await manager.OpenSessionAsync("scripted", Path.Combine(Path.GetTempPath(), "term-noop"), useSandbox: false);

        // No exception, nothing opened.
        await manager.WriteTerminalAsync(info.SessionId, "ghost", [1, 2, 3]);
        await manager.ResizeTerminalAsync(info.SessionId, "ghost", 10, 10);
        Assert.Empty(fallback.Opened);
    }

    [Fact]
    public async Task Provider_login_routes_through_the_same_cli_fallback_path()
    {
        var adapter = new LoginAgentAdapter();
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var terminalId = await manager.BeginProviderLoginAsync("login-agent");

        // The login command was spawned through ICliFallback.OpenTerminalAsync — not a bespoke process.
        Assert.Equal("pty-1", terminalId);
        var options = Assert.Single(fallback.Opened);
        Assert.Equal("login-agent", options.Command);
        Assert.Equal(["auth", "login"], options.Arguments);
    }

    [Fact]
    public async Task Provider_login_streams_terminal_output_to_the_returned_session()
    {
        var adapter = new LoginAgentAdapter();
        var fallback = new FakeCliFallback();
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster,
            NullLoggerFactory.Instance, cliFallback: fallback);

        // The returned id doubles as the login SESSION id (subscribe here) and the login TERMINAL id (write here).
        var loginId = await manager.BeginProviderLoginAsync("login-agent");
        Assert.Equal("pty-1", loginId);

        // The login CLI prints its prompt: it must surface on the returned session as a TerminalOutputEvent —
        // through the same snapshot/tail path as any other event, never a bespoke channel.
        fallback.Last!.EmitOutput("Open the browser to https://example/login and paste the code: ");
        await WaitForAsync(() => broadcaster.Published.Any(p => p.Event is TerminalOutputEvent));

        var snapshot = await manager.GetSnapshotAsync(loginId, 0);
        var output = Assert.Single(snapshot.Events.OfType<TerminalOutputEvent>());
        Assert.Equal(loginId, output.TerminalId);
        Assert.Equal("Open the browser to https://example/login and paste the code: ", output.Data);
    }

    [Fact]
    public async Task Provider_login_terminal_is_interactive_input_reaches_the_login_pty()
    {
        var adapter = new LoginAgentAdapter();
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var loginId = await manager.BeginProviderLoginAsync("login-agent");

        // Unlike a read-only external-session watch, the login terminal is interactive: input the user types
        // (and a resize) must reach the underlying login PTY handle, addressed by the login terminal id.
        await manager.WriteTerminalAsync(loginId, loginId, "device-code-1234\n"u8.ToArray());
        await manager.ResizeTerminalAsync(loginId, loginId, 100, 40);

        var handle = fallback.Last!;
        Assert.Equal("device-code-1234\n", System.Text.Encoding.UTF8.GetString(Assert.Single(handle.Writes)));
        Assert.Equal((100, 40), Assert.Single(handle.Resizes));
    }

    [Fact]
    public async Task Provider_login_refreshes_auth_status_when_the_login_terminal_exits()
    {
        var adapter = new LoginAgentAdapter();
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var loginId = await manager.BeginProviderLoginAsync("login-agent");
        var probesBefore = adapter.AuthProbes;

        // The login CLI finished: exiting must force a fresh provider auth check (so the login badge updates)
        // and tear the scratch login session down.
        fallback.Last!.EmitExit();
        await WaitForAsync(() => adapter.AuthProbes > probesBefore);

        Assert.True(fallback.Last!.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetSnapshotAsync(loginId, 0));
    }

    [Fact]
    public async Task Provider_login_without_a_login_command_throws()
    {
        var adapter = new ScriptedAgentAdapter();
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginProviderLoginAsync("scripted"));
    }
}
