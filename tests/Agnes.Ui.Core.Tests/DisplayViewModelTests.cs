using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The display view model: what a head binds to. Frames reach it in order and on the dispatcher, Info and
/// Control move its state, and input leaves as the wire's own JSON.
/// </summary>
public class DisplayViewModelTests
{
    /// <summary>A channel a test drives by hand, standing in for a host's stream.</summary>
    private sealed class ScriptedChannel : IDisplayChannel
    {
        private readonly Channel<DisplayFrame> _frames = Channel.CreateUnbounded<DisplayFrame>();

        public List<DisplayClientMessage> Sent { get; } = [];

        public ChannelReader<DisplayFrame> Frames => _frames.Reader;

        public Task SendAsync(DisplayClientMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public void Emit(DisplayFrameKind kind, byte[] payload, int width = 1280, int height = 800, int x = 0, int y = 0)
            => _frames.Writer.TryWrite(new DisplayFrame(
                new DisplayFrameHeader(kind, 1, (ushort)x, (ushort)y, (ushort)width, (ushort)height, 1280, 800, (uint)payload.Length),
                payload));

        public void EmitJson<T>(DisplayFrameKind kind, T payload)
            => Emit(kind, JsonSerializer.SerializeToUtf8Bytes(payload, DisplayWire.Json));

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    // Re-lists IAgnesHost on purpose: OpenDisplayAsync is a DEFAULT interface member, so a method declared on a
    // subclass alone would never be the one called through the interface — the default would win, and the fake
    // would silently do nothing.
    private sealed class DisplayHost : StubAgnesHost, IAgnesHost
    {
        public ScriptedChannel Channel { get; } = new();

        public bool HasDisplayInCatalogue { get; init; } = true;

        public Task<IDisplayChannel> OpenDisplayAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IDisplayChannel>(Channel);

        public override Task<IReadOnlyList<SessionSummary>> ListSessionsAsync()
            => Task.FromResult<IReadOnlyList<SessionSummary>>(
            [
                new SessionSummary("s1", "opencode", "/work", null, SessionRunState.Idle, 0, HasDisplay: HasDisplayInCatalogue),
            ]);
    }

    private static async Task<(DisplayViewModel Vm, DisplayHost Host)> ConnectedAsync()
    {
        var host = new DisplayHost();
        var vm = new DisplayViewModel(host, "s1", ImmediateDispatcher.Instance);
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, host);
    }

    private static async Task PumpAsync(Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task An_info_frame_sets_the_geometry_and_the_driver()
    {
        var (vm, host) = await ConnectedAsync();

        host.Channel.EmitJson(DisplayFrameKind.Info, new DisplayInfo(1280, 800, 96, DisplayControlHolder.Agent, null));
        await PumpAsync(() => vm.Width > 0);

        Assert.Equal(1280, vm.Width);
        Assert.Equal(800, vm.Height);
        Assert.True(vm.IsAgentDriving);
        Assert.False(vm.IsUserDriving);
        Assert.Equal("Agent is driving", vm.DriverLabel);
    }

    [Fact]
    public async Task A_control_frame_moves_the_driver_without_touching_the_picture()
    {
        var (vm, host) = await ConnectedAsync();

        host.Channel.EmitJson(DisplayFrameKind.Control, new DisplayControlNotice(DisplayControlHolder.User, "device-3"));
        await PumpAsync(() => vm.IsUserDriving);

        Assert.True(vm.IsUserDriving);
        Assert.Equal("device-3", vm.HolderDeviceId);
        Assert.Equal("You have control", vm.DriverLabel);
        Assert.Null(vm.LatestFrame); // a control notice is not a picture
    }

    [Fact]
    public async Task Image_frames_raise_FrameArrived_in_order_and_are_counted()
    {
        var (vm, host) = await ConnectedAsync();

        var seen = new List<uint>();
        vm.FrameArrived += f => seen.Add(f.Header.Sequence);

        host.Channel.Emit(DisplayFrameKind.Full, Encoding.UTF8.GetBytes("frame-one"));
        host.Channel.Emit(DisplayFrameKind.Tile, Encoding.UTF8.GetBytes("frame-two"), width: 32, height: 32, x: 8, y: 16);
        await PumpAsync(() => seen.Count == 2);

        Assert.Equal(2, seen.Count);
        Assert.NotNull(vm.LatestFrame);
        Assert.Equal(DisplayFrameKind.Tile, vm.LatestFrame!.Header.Kind);
        Assert.Equal(8, vm.LatestFrame.Header.X);
        Assert.Equal(16, vm.LatestFrame.Header.Y);
        Assert.NotNull(vm.LastFrameAt);
    }

    [Fact]
    public async Task Input_leaves_as_the_wire_s_own_messages_in_guest_pixels()
    {
        var (vm, host) = await ConnectedAsync();

        await vm.PointerMoveAsync(640, 400);
        await vm.PointerButtonAsync(2, down: true);
        await vm.ScrollAsync(10, 20, 0, -3);
        await vm.KeyAsync("Page_Up", down: true);
        await vm.SetQualityAsync(960, 30);

        var move = Assert.IsType<DisplayPointerMove>(host.Channel.Sent[0]);
        Assert.Equal((640, 400), (move.X, move.Y));

        var button = Assert.IsType<DisplayPointerButton>(host.Channel.Sent[1]);
        Assert.Equal(2, button.Button);
        Assert.True(button.Down);

        var scroll = Assert.IsType<DisplayPointerScroll>(host.Channel.Sent[2]);
        Assert.Equal((10, 20, 0, -3), (scroll.X, scroll.Y, scroll.Dx, scroll.Dy));

        var key = Assert.IsType<DisplayKey>(host.Channel.Sent[3]);
        Assert.Equal("Page_Up", key.Key);

        var quality = Assert.IsType<DisplayQuality>(host.Channel.Sent[4]);
        Assert.Equal(960, quality.MaxWidth);
        Assert.Equal(30, quality.MaxFps);

        // And the serialised form is the one the host parses — the discriminator is "t", keys are camelCase.
        Assert.Equal(
            """{"t":"move","x":640,"y":400}""",
            JsonSerializer.Serialize<DisplayClientMessage>(move, DisplayWire.Json));
    }

    [Fact]
    public async Task Taking_and_handing_back_control_are_the_same_message_with_opposite_intent()
    {
        var (vm, host) = await ConnectedAsync();

        await vm.TakeControlCommand.ExecuteAsync(null);
        host.Channel.EmitJson(DisplayFrameKind.Control, new DisplayControlNotice(DisplayControlHolder.User, "me"));
        await PumpAsync(() => vm.IsUserDriving);
        await vm.ReleaseControlCommand.ExecuteAsync(null);

        Assert.True(Assert.IsType<DisplayControlRequest>(host.Channel.Sent[0]).Take);
        Assert.False(Assert.IsType<DisplayControlRequest>(host.Channel.Sent[1]).Take);
    }

    [Fact]
    public async Task A_host_that_cannot_open_the_display_leaves_the_reason_on_the_view_model()
    {
        var vm = new DisplayViewModel(new NoDisplayHost(), "s1", ImmediateDispatcher.Instance);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.False(vm.IsBusy);
        Assert.Contains("does not offer a display channel", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoDisplayHost : StubAgnesHost;

    [Fact]
    public async Task A_session_learns_from_the_catalogue_that_it_has_a_screen()
    {
        var host = new DisplayHost();
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", "/work", 0), [], 0));

        await using var session = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        await PumpAsync(() => session.HasDisplay);

        Assert.True(session.HasDisplay);
        var display = Assert.IsType<DisplayViewModel>(session.Display);
        Assert.Same(display, session.Display); // built once, not per access

        // Showing the panel connects it; hiding it disconnects, so a hidden screen costs nothing.
        session.IsDisplayVisible = true;
        await PumpAsync(() => display.IsConnected);
        Assert.True(display.IsConnected);

        session.IsDisplayVisible = false;
        await PumpAsync(() => !display.IsConnected);
        Assert.False(display.IsConnected);
    }

    [Fact]
    public async Task A_session_without_a_screen_offers_none()
    {
        var host = new DisplayHost { HasDisplayInCatalogue = false };
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", "/work", 0), [], 0));

        await using var session = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        await PumpAsync(() => session.HasDisplay);

        Assert.False(session.HasDisplay);
        Assert.Null(session.Display);
    }
}
