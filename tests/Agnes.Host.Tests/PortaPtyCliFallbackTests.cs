using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// End-to-end tests for the REAL <see cref="PortaPtyCliFallback"/> against an actual pseudo-terminal. These run
/// headlessly wherever <c>/dev/ptmx</c> is available (the CI sandbox has it); the one PTY-allocation guard below
/// skips cleanly if a platform genuinely can't allocate one.
/// </summary>
public class PortaPtyCliFallbackTests
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

    private static PortaPtyCliFallback NewFallback() => new(NullLoggerFactory.Instance);

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting: {because}");
            }

            await Task.Delay(20, cts.Token);
        }
    }

    // True once no live process holds the pid (either it's gone entirely, or its Process handle reports exited).
    private static bool ProcessGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task Open_terminal_streams_process_output_as_it_is_produced()
    {
        var fallback = NewFallback();
        var options = new TerminalOptions
        {
            // Bare command names exercise PATH resolution (and avoid an absolute-path literal, PH2080).
            Command = "sh",
            Arguments = ["-c", "echo hello-pty"],
            WorkingDirectory = Path.GetTempPath(),
            Columns = 80,
            Rows = 24,
        };

        ITerminalHandle handle;
        try
        {
            handle = await fallback.OpenTerminalAsync(options);
        }
        catch (InvalidOperationException ex)
        {
            // Only reached if a PTY genuinely cannot be allocated on this platform — /dev/ptmx is present in the
            // sandbox, so this is a clean skip rather than a failure.
            Assert.Fail($"PTY allocation unavailable in this environment: {ex.Message}");
            return;
        }

        var output = new StringBuilder();
        ((ITerminalOutputSource)handle).OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(data);
            }
        };

        await using (handle)
        {
            await WaitForAsync(
                () => { lock (output) { return output.ToString().Contains("hello-pty", StringComparison.Ordinal); } },
                "the echoed line to surface through OutputReceived");
        }
    }

    [Fact]
    public async Task Missing_command_surfaces_a_clear_error_rather_than_crashing()
    {
        var fallback = NewFallback();
        var options = new TerminalOptions
        {
            Command = "definitely-not-a-real-command-xyzzy-42",
            WorkingDirectory = Path.GetTempPath(),
        };

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => fallback.OpenTerminalAsync(options));
        Assert.Contains("definitely-not-a-real-command-xyzzy-42", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_terminal_echoes_input_resizes_and_dispose_kills_the_child()
    {
        var fallback = NewFallback();
        var options = new TerminalOptions
        {
            Command = "cat", // echoes stdin back to stdout — a simple interactive case.
            WorkingDirectory = Path.GetTempPath(),
            Columns = 80,
            Rows = 24,
        };

        var handle = await fallback.OpenTerminalAsync(options);
        var pid = ((PortaPtyTerminalHandle)handle).ProcessId;

        var output = new StringBuilder();
        ((ITerminalOutputSource)handle).OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(data);
            }
        };

        // Input the user "types" must reach the PTY and come back out (tty echo and/or cat re-emitting the line).
        await handle.WriteAsync(Encoding.UTF8.GetBytes("ping-123\n"));
        await WaitForAsync(
            () => { lock (output) { return output.ToString().Contains("ping-123", StringComparison.Ordinal); } },
            "written input to echo back through the PTY output stream");

        // Resizing a live PTY succeeds without throwing.
        await handle.ResizeAsync(120, 40);

        // Disposing must terminate the child process and free the PTY.
        Assert.False(ProcessGone(pid));
        await handle.DisposeAsync();
        await WaitForAsync(() => ProcessGone(pid), "the child process to be terminated after dispose");
    }

    [Fact]
    public async Task Exit_source_fires_when_the_child_process_exits_on_its_own()
    {
        var fallback = NewFallback();
        var options = new TerminalOptions
        {
            Command = "sh",
            Arguments = ["-c", "exit 0"],
            WorkingDirectory = Path.GetTempPath(),
        };

        await using var handle = await fallback.OpenTerminalAsync(options);
        var exited = new TaskCompletionSource();
        ((ITerminalExitSource)handle).Exited += () => exited.TrySetResult();

        await WaitForAsync(() => exited.Task.IsCompleted, "the exit source to fire when the child exits");
    }

    [Fact]
    public async Task Session_manager_open_terminal_surfaces_output_as_terminal_output_events()
    {
        var adapter = new ScriptedAgentAdapter();
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster,
            NullLoggerFactory.Instance, cliFallback: NewFallback());

        // A real PTY chdirs into the session working directory, so it must exist (a nonexistent cwd makes the
        // child fail to launch and produce no output).
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pty-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        var info = await manager.OpenSessionAsync("scripted", workingDirectory, useSandbox: false);

        // Drive the real PTY end to end through the SessionManager terminal protocol.
        var terminalId = await manager.OpenTerminalAsync(
            info.SessionId, command: "sh", arguments: ["-c", "echo hello-from-session"], workingDirectory: null, columns: 80, rows: 24);

        await WaitForAsync(
            () => broadcaster.Published.Any(p => p.Event is TerminalOutputEvent t && t.Data.Contains("hello-from-session", StringComparison.Ordinal)),
            "the PTY output to be appended to the session log as TerminalOutputEvents");

        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        var combined = string.Concat(snapshot.Events.OfType<TerminalOutputEvent>().Select(e => e.Data));
        Assert.Contains("hello-from-session", combined, StringComparison.Ordinal);
        Assert.All(snapshot.Events.OfType<TerminalOutputEvent>(), e => Assert.Equal(terminalId, e.TerminalId));
    }
}
