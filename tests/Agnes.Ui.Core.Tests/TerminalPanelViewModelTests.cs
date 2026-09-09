using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public class TerminalPanelViewModelTests
{
    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "opencode", string.Empty, 0), [], 0));
        return view;
    }

    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    [Fact]
    public void Feeding_terminal_output_events_surfaces_the_decoded_output_in_order()
    {
        var view = Live();
        var vm = new TerminalPanelViewModel(new RecordingTerminalHost(), view, ImmediateDispatcher.Instance);

        var chunks = new List<string>();
        vm.OutputAppended += chunks.Add;

        view.Apply(Seq(new TerminalOutputEvent("term-1", "hello "), 1));
        view.Apply(Seq(new TerminalOutputEvent("term-1", "world"), 2));

        Assert.Equal("hello world", vm.Output);
        Assert.Equal(["hello ", "world"], chunks);       // per-chunk, in order
        Assert.Equal("term-1", vm.ActiveTerminalId);      // adopted from the stream
        Assert.True(vm.HasTerminal);
    }

    [Fact]
    public void Output_replayed_from_the_snapshot_restores_scrollback_before_live_events()
    {
        // A client reopening a session gets its prior terminal output in the snapshot; the panel restores it.
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(
            new SessionInfo("s1", "opencode", string.Empty, 1),
            [Seq(new TerminalOutputEvent("term-1", "prior scrollback"), 1)],
            1));

        var vm = new TerminalPanelViewModel(new RecordingTerminalHost(), view, ImmediateDispatcher.Instance);

        Assert.Equal("prior scrollback", vm.Output);
        Assert.Equal("term-1", vm.ActiveTerminalId);
    }

    [Fact]
    public async Task A_keystroke_writes_input_bytes_to_the_active_terminal()
    {
        var host = new RecordingTerminalHost();
        var vm = new TerminalPanelViewModel(host, Live(), ImmediateDispatcher.Instance);

        await vm.OpenAsync();
        Assert.Equal("term-1", vm.ActiveTerminalId);
        Assert.True(vm.IsVisible);

        await vm.SendTextAsync("ls\n");

        var (sessionId, terminalId, data) = Assert.Single(host.Writes);
        Assert.Equal("s1", sessionId);
        Assert.Equal("term-1", terminalId);
        Assert.Equal("ls\n", System.Text.Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task A_resize_reports_the_new_size_to_the_host()
    {
        var host = new RecordingTerminalHost();
        var vm = new TerminalPanelViewModel(host, Live(), ImmediateDispatcher.Instance);
        await vm.OpenAsync();

        await vm.ResizeAsync(100, 40);

        var (sessionId, terminalId, cols, rows) = Assert.Single(host.Resizes);
        Assert.Equal("s1", sessionId);
        Assert.Equal("term-1", terminalId);
        Assert.Equal(100, cols);
        Assert.Equal(40, rows);
        Assert.Equal(100, vm.Columns);
        Assert.Equal(40, vm.Rows);
    }

    [Fact]
    public async Task Input_and_resize_are_no_ops_before_a_terminal_exists()
    {
        var host = new RecordingTerminalHost();
        var vm = new TerminalPanelViewModel(host, Live(), ImmediateDispatcher.Instance);

        await vm.SendTextAsync("ignored");
        await vm.ResizeAsync(80, 24);

        Assert.Empty(host.Writes);
        Assert.Empty(host.Resizes);   // size still remembered locally for the next open
        Assert.Equal(80, vm.Columns);
    }

    [Fact]
    public void Moving_the_dock_location_does_not_disturb_the_live_terminal()
    {
        var view = Live();
        var vm = new TerminalPanelViewModel(new RecordingTerminalHost(), view, ImmediateDispatcher.Instance);
        view.Apply(Seq(new TerminalOutputEvent("term-1", "output"), 1));

        vm.DockLocation = TerminalDockLocation.Sidebar;

        // The terminal id and its scrollback survive the move unchanged.
        Assert.Equal("term-1", vm.ActiveTerminalId);
        Assert.Equal("output", vm.Output);
        Assert.Equal(TerminalDockLocation.Sidebar, vm.DockLocation);
    }

    [Fact]
    public async Task A_console_panel_ignores_the_shell_terminal_sharing_its_session()
    {
        // A session can carry two PTYs at once — its shell and its agent console — and both write
        // TerminalOutputEvents into the one session log. A panel that adopted whichever spoke first would
        // render the other's output and send its keystrokes there.
        var view = Live();
        var host = new RecordingTerminalHost { AgentConsoleId = "console-1" };
        var console = new TerminalPanelViewModel(host, view, ImmediateDispatcher.Instance, adoptFromStream: false);

        // The shell speaks first; the unclaimed console panel must not take its id.
        view.Apply(Seq(new TerminalOutputEvent("term-shell", "$ ls\n"), 1));
        Assert.Null(console.ActiveTerminalId);
        Assert.Equal(string.Empty, console.Output);

        Assert.True(await console.OpenAgentConsoleAsync());
        Assert.Equal("console-1", console.ActiveTerminalId);

        // Now only its own terminal reaches it.
        view.Apply(Seq(new TerminalOutputEvent("term-shell", "more shell\n"), 2));
        view.Apply(Seq(new TerminalOutputEvent("console-1", "> /help\n"), 3));
        Assert.Equal("> /help\n", console.Output);
    }

    [Fact]
    public async Task Attaching_to_a_console_recovers_the_output_it_produced_before_anyone_looked()
    {
        // The console is opened with the session and runs whether or not a client is attached, so its
        // output is already in the log by the time the panel learns which terminal is its own.
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(
            new SessionInfo("s1", "opencode", string.Empty, 2),
            [
                Seq(new TerminalOutputEvent("term-shell", "shell noise"), 1),
                Seq(new TerminalOutputEvent("console-1", "Copilot ready\n"), 2),
            ],
            2));

        var console = new TerminalPanelViewModel(
            new RecordingTerminalHost { AgentConsoleId = "console-1" }, view, ImmediateDispatcher.Instance,
            adoptFromStream: false);

        Assert.True(await console.OpenAgentConsoleAsync());

        // Its own scrollback, and none of the shell's.
        Assert.Equal("Copilot ready\n", console.Output);
    }

    [Fact]
    public async Task An_agent_with_no_console_leaves_the_panel_unclaimed()
    {
        var console = new TerminalPanelViewModel(
            new RecordingTerminalHost { AgentConsoleId = null }, Live(), ImmediateDispatcher.Instance,
            adoptFromStream: false);

        Assert.False(await console.OpenAgentConsoleAsync());
        Assert.Null(console.ActiveTerminalId);
        Assert.False(console.IsVisible);
    }
}
