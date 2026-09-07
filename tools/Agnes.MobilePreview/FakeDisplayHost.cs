using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Agnes.App.Mobile.Preview;

/// <summary>
/// A display channel with nothing behind it, for rendering the Screen segment without a sandbox.
///
/// The simulated host has no graphical session, and standing one up would mean a VM. What the screen
/// actually needs is narrower than that: an <see cref="DisplayFrameKind.Info"/> and then some JPEGs. So
/// this hands over frames the caller pushes in, and <see cref="FakeDisplayHost"/> is the two-method
/// <see cref="IAgnesHost"/> that vends it — everything else throws, because
/// <see cref="Agnes.Ui.Core.ViewModels.DisplayViewModel"/> calls nothing else and a fake that pretends
/// to do more is a fake that lies.
/// </summary>
public sealed class FakeDisplayChannel : IDisplayChannel
{
    private readonly Channel<DisplayFrame> _frames = Channel.CreateUnbounded<DisplayFrame>();

    public ChannelReader<DisplayFrame> Frames => _frames.Reader;

    /// <summary>Everything the client sent, so a test can assert on the input the surface produced.</summary>
    public List<DisplayClientMessage> Sent { get; } = [];

    public Task SendAsync(DisplayClientMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public void Push(DisplayFrame frame) => _frames.Writer.TryWrite(frame);

    /// <summary>The Info the real channel always sends first.</summary>
    public void PushInfo(int width, int height, DisplayControlHolder holder = DisplayControlHolder.Agent)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DisplayInfo(width, height, 96, holder, null), DisplayWire.Json);
        Push(new DisplayFrame(
            new DisplayFrameHeader(DisplayFrameKind.Info, 0, 0, 0, 0, 0, (ushort)width, (ushort)height, (uint)json.Length),
            json));
    }

    public void PushFull(int width, int height, byte[] jpeg)
        => Push(new DisplayFrame(
            new DisplayFrameHeader(
                DisplayFrameKind.Full, 1, 0, 0, (ushort)width, (ushort)height,
                (ushort)width, (ushort)height, (uint)jpeg.Length),
            jpeg));

    public void PushTile(int x, int y, int width, int height, int displayWidth, int displayHeight, byte[] jpeg)
        => Push(new DisplayFrame(
            new DisplayFrameHeader(
                DisplayFrameKind.Tile, 2, (ushort)x, (ushort)y, (ushort)width, (ushort)height,
                (ushort)displayWidth, (ushort)displayHeight, (uint)jpeg.Length),
            jpeg));

    public ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IAgnesHost"/> that can do exactly one thing: open a display. Handed to a
/// <c>DisplayViewModel</c> so the Screen segment can be rendered offline. Every other member throws
/// rather than returning a plausible empty value — if something starts calling one, that is a change
/// worth noticing, not one worth papering over.
/// </summary>
public sealed class FakeDisplayHost : IAgnesHost
{
    public FakeDisplayChannel Channel { get; } = new();

    public string HostUrl => "sim://display";

    public AgnesConnectionState State => AgnesConnectionState.Connected;

    public event Action<AgnesConnectionState>? StateChanged { add => _ = value; remove => _ = value; }

    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged { add => _ = value; remove => _ = value; }

    public event Action<InboxRun>? InboxRunReceived { add => _ = value; remove => _ = value; }

    public event Action<SessionGoal>? GoalChanged { add => _ = value; remove => _ = value; }

    public event Action<string, long, bool>? ReadStateChanged { add => _ = value; remove => _ = value; }

    public Task<IDisplayChannel> OpenDisplayAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IDisplayChannel>(Channel);

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => new NotSupportedException($"{member} is not part of the display fake.");

    public Task<HostInfo> GetHostInfoAsync() => throw Unused();

    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync() => throw Unused();

    public Task<SessionInfo> OpenSessionAsync(
        string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false,
        string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true,
        string? modelId = null) => throw Unused();

    public Task<SessionView> SubscribeAsync(string sessionId, long since = 0) => throw Unused();

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content) => throw Unused();

    public Task CancelAsync(string sessionId) => throw Unused();

    public Task SetModeAsync(string sessionId, string modeId) => throw Unused();

    public Task SwitchModelAsync(string sessionId, string? modelId) => throw Unused();

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId) => throw Unused();

    public Task<GitStatus> GetGitStatusAsync(string sessionId) => throw Unused();

    public Task<GitCommitResult> GitCommitAsync(string sessionId, string message) => throw Unused();

    public Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data) => throw Unused();

    public Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request) => throw Unused();

    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync() => throw Unused();

    public Task RemoveScheduledTaskAsync(string taskId) => throw Unused();

    public Task<IReadOnlyList<InboxRun>> GetInboxAsync() => throw Unused();

    public Task MarkSessionReadAsync(string sessionId, long sequence) => throw Unused();

    public Task MarkSessionUnreadAsync(string sessionId) => throw Unused();

    public Task PauseSandboxAsync(string sessionId) => throw Unused();

    public Task ResumeSandboxAsync(string sessionId) => throw Unused();

    public Task DeleteSandboxAsync(string sessionId) => throw Unused();

    public Task StopSessionAsync(string sessionId) => throw Unused();

    public Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId) => throw Unused();
}

/// <summary>
/// Draws a plausible desktop and encodes it as JPEG, so the Screen segment can be rendered with real
/// pixels in it rather than a placeholder rectangle. Drawn rather than checked in: the harness stays a
/// single project with no binary fixtures, and a synthetic frame can be any size a test wants.
/// </summary>
public static class FakeDesktop
{
    /// <summary>A window on a desktop background, at the guest's size.</summary>
    public static byte[] Jpeg(int width, int height, string caption, double hue = 0)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(28, 24, 46), 0),
                        new GradientStop(Color.FromRgb((byte)(60 + hue), 30, 70), 1),
                    },
                },
                new Rect(0, 0, width, height));

            // A window, a title bar, and some text lines: enough that a screenshot of the segment is
            // obviously a desktop and not a colour swatch.
            var window = new Rect(width * 0.08, height * 0.14, width * 0.62, height * 0.62);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 28)), window, 8);
            context.FillRectangle(
                new SolidColorBrush(Color.FromRgb(44, 44, 52)),
                new Rect(window.X, window.Y, window.Width, 30), 8);
            context.DrawText(
                new FormattedText(
                    caption, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 16, Brushes.White),
                new Point(window.X + 14, window.Y + 6));

            for (var i = 0; i < 9; i++)
            {
                var y = window.Y + 48 + (i * 22);
                var w = window.Width * (0.3 + (0.55 * ((i * 7 % 10) / 10.0)));
                context.FillRectangle(
                    new SolidColorBrush(Color.FromRgb(90, 96, 120), i % 3 == 0 ? 0.9 : 0.55),
                    new Rect(window.X + 16, y, w, 10), 3);
            }

            // A taskbar, so the letterbox is obviously the phone's and not the desktop's.
            context.FillRectangle(
                new SolidColorBrush(Color.FromRgb(16, 16, 22)), new Rect(0, height - 34, width, 34));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, new JpegBitmapEncoderOptions { Quality = 80 });
        return stream.ToArray();
    }
}
