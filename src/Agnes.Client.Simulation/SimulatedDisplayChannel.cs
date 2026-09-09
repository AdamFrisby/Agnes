using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Client.Simulation;

/// <summary>
/// A fake graphical sandbox: an Info frame, then a JPEG of a synthetic 1280×800 desktop every ~100 ms, and
/// Control frames echoing whoever asks for the mouse.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the Screen panel is a real, moving thing offline — in the screenshot tool, in the headless
/// desktop tests, and for anyone developing the panel without an Incus VM to hand. It is a full participant in
/// the wire contract rather than a stub: it honours <see cref="DisplayQuality"/>, it answers
/// <see cref="DisplayControlRequest"/>, and its frames go through the same header the host writes, so a bug in
/// the client's parsing shows up here rather than only against real hardware.
/// </para>
/// <para>
/// Frames are rendered at the size the subscriber asked for, not at the guest's own 1280×800, exactly as the
/// contract says a host may ("serves the smallest size any subscriber asked for"). That also keeps the
/// hand-rolled encoder cheap: a half-scale frame is a quarter of the blocks.
/// </para>
/// </remarks>
public sealed class SimulatedDisplayChannel : IDisplayChannel
{
    /// <summary>The guest's own geometry, matching the graphical sandbox the host offers.</summary>
    public const int DisplayWidth = 1280;

    public const int DisplayHeight = 800;

    private readonly Channel<DisplayFrame> _frames = Channel.CreateBounded<DisplayFrame>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly CancellationTokenSource _life = new();
    private readonly TimeProvider _time;
    private readonly Task _painter;
    private readonly Lock _gate = new();

    private DisplayControlHolder _holder = DisplayControlHolder.Agent;
    private int _renderWidth = 640;
    private int _quality = 70;
    private int _frameIntervalMs = 100;
    private int _sequence;

    public SimulatedDisplayChannel(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _frames.Writer.TryWrite(BuildInfo());
        _painter = Task.Run(() => PaintLoopAsync(_life.Token));
    }

    public ChannelReader<DisplayFrame> Frames => _frames.Reader;

    public Task SendAsync(DisplayClientMessage message, CancellationToken cancellationToken = default)
    {
        switch (message)
        {
            case DisplayQuality quality:
                lock (_gate)
                {
                    _renderWidth = Math.Clamp(quality.MaxWidth, 160, DisplayWidth);
                    _quality = Math.Clamp(quality.JpegQuality, 10, 95);
                    _frameIntervalMs = Math.Clamp(1000 / Math.Max(1, quality.MaxFps), 33, 1000);
                }

                break;

            case DisplayControlRequest request:
                // The real host arbitrates; the simulation always says yes, which is the interesting case for
                // a UI (the panel has to show control changing hands and switch the driver chip's hue).
                DisplayControlHolder holder;
                lock (_gate)
                {
                    _holder = request.Take ? DisplayControlHolder.User : DisplayControlHolder.Agent;
                    holder = _holder;
                }

                _frames.Writer.TryWrite(new DisplayFrame(
                    new DisplayFrameHeader(DisplayFrameKind.Control, NextSequence(), 0, 0, 0, 0, DisplayWidth, DisplayHeight, 0),
                    DisplayFrameCodec.EncodeJson(
                        DisplayFrameKind.Control,
                        0,
                        new DisplayControlNotice(holder, holder == DisplayControlHolder.User ? "simulated-device" : null),
                        DisplayWidth,
                        DisplayHeight)
                        .AsMemory(DisplayFrameHeader.Size)));
                break;

            default:
                // Pointer and key input has nowhere to go in a picture of a desktop; accepting it silently is
                // the honest simulation of a guest that received it.
                break;
        }

        return Task.CompletedTask;
    }

    private DisplayFrame BuildInfo()
    {
        var encoded = DisplayFrameCodec.EncodeJson(
            DisplayFrameKind.Info,
            NextSequence(),
            new DisplayInfo(DisplayWidth, DisplayHeight, 96, _holder, null),
            DisplayWidth,
            DisplayHeight);
        _ = DisplayFrameHeader.TryRead(encoded, out var header);
        return new DisplayFrame(header, encoded.AsMemory(DisplayFrameHeader.Size));
    }

    // The wire's sequence is a uint; the counter is an int so Interlocked can have it, and wraps the same way.
    private uint NextSequence() => unchecked((uint)Interlocked.Increment(ref _sequence));

    private async Task PaintLoopAsync(CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int width, quality, interval;
                lock (_gate)
                {
                    width = _renderWidth;
                    quality = _quality;
                    interval = _frameIntervalMs;
                }

                var height = width * DisplayHeight / DisplayWidth;
                var elapsed = (_time.GetUtcNow() - started).TotalSeconds;
                var pixels = SyntheticDesktop.Render(width, height, elapsed, _time.GetUtcNow());
                var jpeg = JpegEncoder.Encode(pixels, width, height, quality);

                var header = new DisplayFrameHeader(
                    DisplayFrameKind.Full,
                    NextSequence(),
                    0,
                    0,
                    (ushort)width,
                    (ushort)height,
                    DisplayWidth,
                    DisplayHeight,
                    (uint)jpeg.Length);
                _frames.Writer.TryWrite(new DisplayFrame(header, jpeg));

                await Task.Delay(TimeSpan.FromMilliseconds(interval), _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // disposing
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            await _painter.ConfigureAwait(false);
        }
        catch
        {
            // The paint loop's only job was to stop.
        }

        _life.Dispose();
    }
}
