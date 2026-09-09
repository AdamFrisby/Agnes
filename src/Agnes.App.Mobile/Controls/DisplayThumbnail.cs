using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// A small, slow picture of the guest's screen, shown above the transcript.
///
/// It exists so that "the agent is doing something in a graphical sandbox" is visible without leaving
/// the conversation — the phone equivalent of glancing at the other monitor. Deliberately cheap:
/// <b>Full frames only</b> (a tile is a fragment and compositing one here would need a second canvas for
/// no benefit) and at most one repaint a second, because a thumbnail nobody is looking at must not cost
/// a decode per frame on a device that is also holding a radio open.
/// </summary>
public sealed class DisplayThumbnail : Control
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    public static readonly StyledProperty<DisplayViewModel?> DisplayProperty =
        AvaloniaProperty.Register<DisplayThumbnail, DisplayViewModel?>(nameof(Display));

    private DisplayViewModel? _bound;
    private Bitmap? _picture;
    private DateTime _paintedAt = DateTime.MinValue;

    public DisplayViewModel? Display
    {
        get => GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    /// <summary>True once there is something to show; the caller hides the strip until then rather than
    /// reserving a grey rectangle.</summary>
    public bool HasPicture => _picture is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DisplayProperty)
        {
            return;
        }

        if (_bound is not null)
        {
            _bound.FrameArrived -= OnFrame;
        }

        _bound = change.GetNewValue<DisplayViewModel?>();
        _picture?.Dispose();
        _picture = null;
        if (_bound is not null)
        {
            _bound.FrameArrived += OnFrame;
        }

        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_bound is not null)
        {
            _bound.FrameArrived -= OnFrame;
            _bound = null;
        }
    }

    private void OnFrame(DisplayFrame frame)
    {
        if (frame.Header.Kind != DisplayFrameKind.Full || DateTime.UtcNow - _paintedAt < MinInterval)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(frame.Payload.ToArray(), writable: false);
            var picture = new Bitmap(stream);
            _picture?.Dispose();
            _picture = picture;
            _paintedAt = DateTime.UtcNow;
        }
        catch (Exception)
        {
            return; // a bad frame is a stale thumbnail, never a crash
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_picture is not { } picture || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var fit = DisplayFit.Compute(
            Bounds.Width, Bounds.Height, (int)picture.PixelSize.Width, (int)picture.PixelSize.Height);
        if (fit.IsEmpty)
        {
            return;
        }

        context.DrawImage(
            picture,
            new Rect(picture.Size),
            new Rect(fit.OffsetX, fit.OffsetY, picture.PixelSize.Width * fit.Scale, picture.PixelSize.Height * fit.Scale));
    }
}
