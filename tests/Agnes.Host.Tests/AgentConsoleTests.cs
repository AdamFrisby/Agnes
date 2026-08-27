using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Sandbox;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The agent console: the agent's own CLI, run interactively in a PTY, wherever the agent runs.
/// </summary>
/// <remarks>
/// It is a <b>second process</b> and has to be. A live agent is a JSON-RPC peer whose stdin is the protocol
/// channel — there is no prompt behind it, and bytes typed at it are parsed as protocol. (Verified against
/// Copilot 1.0.80: <c>printf '/help\n' | copilot --acp</c> answers nothing, and hosting that process on a PTY
/// rather than pipes breaks it outright, because the line discipline echoes every request back into the
/// reader.) So the console exists to reach what the protocol does not carry: slash commands and the rest.
/// </remarks>
public class AgentConsoleTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private sealed class FakeCliFallback : ICliFallback
    {
        public ConcurrentQueue<TerminalOptions> Opened { get; } = new();

        public Task<ITerminalHandle> OpenTerminalAsync(TerminalOptions options, CancellationToken cancellationToken = default)
        {
            Opened.Enqueue(options);
            return Task.FromResult<ITerminalHandle>(new FakeHandle($"pty-{Opened.Count}"));
        }
    }

    private sealed class FakeHandle(string id) : ITerminalHandle
    {
        public string TerminalId { get; } = id;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An adapter whose CLI has both an ACP mode (what the session runs) and a console mode.</summary>
    private sealed class ConsoleAgentAdapter : IAgentAdapter
    {
        public AgentDescriptor Descriptor { get; } = new() { Id = "console-agent", DisplayName = "Console Agent" };

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new ScriptedAgentSession());

        public AgentConsoleCommand? GetInteractiveConsoleCommand() => new("console-agent", []);
    }

    /// <summary>A sandbox that wraps like Incus does, recording what it was asked to wrap.</summary>
    private sealed class FakeSandbox : ISandbox
    {
        public string Id { get; } = "fake-vm";
        public string HomeDirectory => "/home/agnes";
        public SandboxInfo Info => new("fake", Id, SandboxState.Running);
        public ConcurrentQueue<(string Command, IReadOnlyList<string> Args, string Cwd)> Wraps { get; } = new();

        public (string Command, IReadOnlyList<string> Arguments) WrapCommand(
            string command, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Wraps.Enqueue((command, arguments, workingDirectory));
            var argv = new List<string> { "exec", Id, "--cwd", workingDirectory, "--", command };
            argv.AddRange(arguments);
            return ("fakebox", argv);
        }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task MaterializeCredentialAsync(SandboxCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        public string Name => "fake";
        public FakeSandbox Last { get; private set; } = null!;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
        {
            Last = new FakeSandbox();
            return Task.FromResult<ISandbox>(Last);
        }

        public Task<IReadOnlyList<SandboxInfo>> ListManagedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInfo>>([]);

        public Task<ISandbox> AttachAsync(string vmName, SandboxSpec spec, bool start, CancellationToken cancellationToken = default)
        {
            Last = new FakeSandbox();
            return Task.FromResult<ISandbox>(Last);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            try
            {
                cts.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(because);
            }

            await Task.Delay(10, CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_console_starts_with_the_session_rather_than_when_first_looked_at()
    {
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ConsoleAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var info = await manager.OpenSessionAsync("console-agent", Path.Combine(Path.GetTempPath(), "console-work"), useSandbox: false);

        // "Always there, just not rendered": the PTY exists from the beginning, with nobody attached.
        await WaitForAsync(() => !fallback.Opened.IsEmpty, "the console should have been opened with the session");
        Assert.NotNull(manager.GetAgentConsoleId(info.SessionId));

        var opened = fallback.Opened.Single();
        Assert.Equal("console-agent", opened.Command);
        Assert.Empty(opened.Arguments); // the console mode, not the ACP argv the session itself runs
    }

    [Fact]
    public async Task Attaching_returns_the_console_already_running_not_a_second_one()
    {
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ConsoleAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var info = await manager.OpenSessionAsync("console-agent", Path.Combine(Path.GetTempPath(), "console-work"), useSandbox: false);
        await WaitForAsync(() => !fallback.Opened.IsEmpty, "the console should have been opened with the session");

        var first = await manager.OpenAgentConsoleAsync(info.SessionId, 100, 40);
        var second = await manager.OpenAgentConsoleAsync(info.SessionId, 100, 40);

        // One console per session, kept for its lifetime — so a client attaching finds the scrollback intact
        // rather than a freshly-spawned CLI.
        Assert.Equal(first, second);
        Assert.Single(fallback.Opened);
    }

    [Fact]
    public async Task An_agent_with_no_console_offers_none()
    {
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var info = await manager.OpenSessionAsync("scripted", Path.Combine(Path.GetTempPath(), "console-work"), useSandbox: false);

        Assert.Null(await manager.OpenAgentConsoleAsync(info.SessionId, 80, 24));
        Assert.False(manager.HasAgentConsole(info.SessionId));
        Assert.Empty(fallback.Opened); // and nothing was spawned on its behalf
    }

    [Fact]
    public async Task Both_terminals_run_inside_the_sandbox_where_the_agent_lives()
    {
        var fallback = new FakeCliFallback();
        var sandboxes = new FakeSandboxProvider();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ConsoleAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, TestPluginRegistries.Sandboxes(sandboxes), cliFallback: fallback);

        var info = await manager.OpenSessionAsync("console-agent", Path.Combine(Path.GetTempPath(), "console-work"), useSandbox: true);
        await WaitForAsync(() => !fallback.Opened.IsEmpty, "the console should have been opened with the session");

        await manager.OpenTerminalAsync(info.SessionId, command: null, arguments: null, workingDirectory: null, columns: 80, rows: 24);
        await WaitForAsync(() => fallback.Opened.Count == 2, "the shell terminal should have been opened");

        // Both go through the sandbox's own WrapCommand — the same one the agent launch uses, so there is a
        // single notion of "run this inside the sandbox" rather than two that can drift.
        Assert.All(fallback.Opened, o => Assert.Equal("fakebox", o.Command));

        // And both are pointed at the guest's working directory, not the host path the session records.
        // HostSession.WorkingDirectory is always the host directory, so handing that to the guest names
        // nothing that exists there.
        Assert.All(sandboxes.Last.Wraps, w => Assert.Equal("/work", w.Cwd));
        Assert.Contains(sandboxes.Last.Wraps, w => w.Command == "console-agent"); // the console
        Assert.Contains(sandboxes.Last.Wraps, w => w.Command == "bash");          // the shell
    }

    [Fact]
    public async Task An_unsandboxed_terminal_still_opens_on_the_host()
    {
        var fallback = new FakeCliFallback();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, cliFallback: fallback);

        var work = Path.Combine(Path.GetTempPath(), "console-work");
        var info = await manager.OpenSessionAsync("scripted", work, useSandbox: false);

        await manager.OpenTerminalAsync(info.SessionId, command: null, arguments: null, workingDirectory: null, columns: 80, rows: 24);

        // No sandbox, no wrapping: the host session's own directory, exactly as before this feature.
        var opened = fallback.Opened.Single();
        Assert.Equal(info.WorkingDirectory, opened.WorkingDirectory);
        Assert.NotEqual("fakebox", opened.Command);
    }

    [Fact]
    public void The_shipped_adapters_offer_their_own_cli_as_the_console()
    {
        // The console is the SAME executable the session runs, minus whatever puts it into machine mode.
        // Getting this wrong in the other direction is the whole trap: a console that inherited the ACP
        // argv would start a second protocol peer, showing the user a silent process rather than a prompt.
        var copilot = Agnes.Agents.Copilot.CopilotAgent.CreateLaunchSpec();
        Assert.Equal(copilot.Command, Assert.IsType<AgentConsoleCommand>(
            Agnes.Agents.Copilot.CopilotAgent.Create(NullLoggerFactory.Instance).GetInteractiveConsoleCommand()).Command);
        Assert.Empty(copilot.ConsoleArguments!);
        Assert.Contains("--acp", copilot.Arguments); // …and the session's own mode is still the flagged one

        var claude = Agnes.Agents.Native.ClaudeCodeNative.Create(NullLoggerFactory.Instance);
        var claudeConsole = Assert.IsType<AgentConsoleCommand>(claude.GetInteractiveConsoleCommand());
        Assert.Equal("claude", claudeConsole.Command);
        Assert.Empty(claudeConsole.Arguments);
        Assert.Contains("--print", Agnes.Agents.Native.ClaudeCodeNative.DefaultArguments);
    }
}
