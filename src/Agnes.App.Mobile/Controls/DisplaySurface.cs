using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// The guest's screen, on a phone.
///
/// Frames arrive as JPEG — a Full replaces the picture, a Tile repaints one rectangle of it — so the
/// control keeps a bitmap at the <em>guest's</em> size and composites into it. It is a
/// <see cref="RenderTargetBitmap"/> rather than a <see cref="WriteableBitmap"/> on purpose: a tile is
/// a decoded image drawn into a rect, and doing that through a drawing context is one call that cannot
/// get the pixel format or the stride wrong, where the hand-rolled blit can and would fail as a smear
/// rather than an exception.
///
/// We never ask the guest to resize. A 1280×800 desktop stays 1280×800 and the phone is a window onto
/// it: scaled to fit, letterboxed, then zoomed and panned. Input is a trackpad, not a touchscreen — see
/// <see cref="DisplayTrackpad"/> for why — and nothing is sent at all unless the person has taken
/// control, so watching can never nudge the agent's mouse.
/// </summary>
public sealed class DisplaySurface : Control
{
    /// <summary>How often the long press is checked. A finger held still emits no events of its own, so
    /// something has to look.</summary>
    private static readonly TimeSpan HoldInterval = TimeSpan.FromMilliseconds(60);

    private const double MaxZoom = 6;

    public static readonly StyledProperty<DisplayViewModel?> DisplayProperty =
        AvaloniaProperty.Register<DisplaySurface, DisplayViewModel?>(nameof(Display));

    /// <summary>The bars either side of a picture that doesn't fill the view.</summary>
    public static readonly StyledProperty<IBrush?> LetterboxBrushProperty =
        AvaloniaProperty.Register<DisplaySurface, IBrush?>(nameof(LetterboxBrush), Brushes.Black);

    /// <summary>The drawn pointer. Painted in the status hue for "you are driving", so the one thing that
    /// only exists while you have control is coloured like it.</summary>
    public static readonly StyledProperty<IBrush?> CursorBrushProperty =
        AvaloniaProperty.Register<DisplaySurface, IBrush?>(nameof(CursorBrush), Brushes.Gold);

    private readonly DispatcherTimer _hold;
    private DisplayTrackpad _pad = new(1280, 800);
    private RenderTargetBitmap? _canvas;
    private DisplayViewModel? _bound;
    private int _guestWidth;
    private int _guestHeight;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private double _zoomAtPinchStart = 1;
    private bool _pinching;
    private Point? _panFrom;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public DisplaySurface()
    {
        Focusable = true;
        ClipToBounds = true;
        GestureRecognizers.Add(new PinchGestureRecognizer());
        Pinch += OnPinch;
        PinchEnded += OnPinchEnded;

        _hold = new DispatcherTimer { Interval = HoldInterval };
        _hold.Tick += (_, _) => Apply(_pad.Tick(_clock.Elapsed.TotalMilliseconds));
    }

    static DisplaySurface()
    {
        AffectsRender<DisplaySurface>(LetterboxBrushProperty, CursorBrushProperty);
    }

    public DisplayViewModel? Display
    {
        get => GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    public IBrush? LetterboxBrush
    {
        get => GetValue(LetterboxBrushProperty);
        set => SetValue(LetterboxBrushProperty, value);
    }

    public IBrush? CursorBrush
    {
        get => GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }

    /// <summary>True once a frame has been composited — the pane shows its "waiting" state until then.</summary>
    public bool HasPicture => _canvas is not null;

    /// <summary>Zoom, as a multiple of fit-to-view. Reset when the segment is left.</summary>
    public double Zoom => _zoom;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DisplayProperty)
        {
            Rebind(change.GetNewValue<DisplayViewModel?>());
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hold.Stop();
    }

    private void Rebind(DisplayViewModel? display)
    {
        if (ReferenceEquals(_bound, display))
        {
            return;
        }

        if (_bound is not null)
        {
            _bound.FrameArrived -= OnFrame;
            _bound.PropertyChanged -= OnDisplayChanged;
        }

        _bound = display;
        _canvas?.Dispose();
        _canvas = null;
        _guestWidth = 0;
        _guestHeight = 0;
        ResetView();

        if (display is not null)
        {
            display.FrameArrived += OnFrame;
            display.PropertyChanged += OnDisplayChanged;
        }

        InvalidateVisual();
    }

    private void OnDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplayViewModel.Holder) or nameof(DisplayViewModel.Info))
        {
            InvalidateVisual();
        }
    }

    /// <summary>Back to fit-to-view, centred. Called when the Screen segment is entered, so a session
    /// never opens mid-zoom on a corner of a desktop with no clue how to get back.</summary>
    public void ResetView()
    {
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        _pad.PlaceCursor(_guestWidth / 2, _guestHeight / 2);
        InvalidateVisual();
    }

    // ---- frames ----

    private void OnFrame(DisplayFrame frame)
    {
        var header = frame.Header;
        if (!frame.IsImage)
        {
            return;
        }

        var width = header.DisplayWidth > 0 ? header.DisplayWidth : Display?.Width ?? 0;
        var height = header.DisplayHeight > 0 ? header.DisplayHeight : Display?.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var full = header.Kind == DisplayFrameKind.Full;
        if (_canvas is null || _guestWidth != width || _guestHeight != height)
        {
            // A geometry change can only be honoured by a Full frame: repainting one tile of a canvas
            // that has just been reallocated would leave the rest transparent.
            if (!full)
            {
                return;
            }

            _canvas?.Dispose();
            _canvas = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            _guestWidth = width;
            _guestHeight = height;
            _pad.Resize(width, height);
            _pad.PlaceCursor(width / 2, height / 2);
        }

        Bitmap decoded;
        try
        {
            using var stream = new MemoryStream(frame.Payload.ToArray(), writable: false);
            decoded = new Bitmap(stream);
        }
        catch (Exception)
        {
            // A truncated or corrupt frame is a dropped frame. The stream keeps coming and the next one
            // repaints; throwing here would take down the session screen over one bad JPEG.
            return;
        }

        using (decoded)
        using (var context = _canvas.CreateDrawingContext(clear: full))
        {
            var destination = full
                ? new Rect(0, 0, width, height)
                : new Rect(header.X, header.Y, header.Width, header.Height);
            context.DrawImage(decoded, new Rect(decoded.Size), destination);
        }

        InvalidateVisual();
    }

    // ---- painting ----

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(LetterboxBrush ?? Brushes.Black, new Rect(Bounds.Size));

        if (_canvas is null)
        {
            return;
        }

        var fit = Fit();
        if (fit.IsEmpty)
        {
            return;
        }

        var destination = new Rect(
            fit.OffsetX, fit.OffsetY, _guestWidth * fit.Scale, _guestHeight * fit.Scale);
        context.DrawImage(_canvas, new Rect(_canvas.Size), destination);

        if (Display is { IsUserDriving: true })
        {
            DrawCursor(context, fit);
        }
    }

    /// <summary>
    /// The pointer, drawn by us. The guest's own cursor is baked into the frames but a phone needs one
    /// that is legible at a quarter scale and unmistakably <em>ours</em> — a ring around a dot, outlined
    /// dark so it survives being over white, in the "you are driving" hue.
    /// </summary>
    private void DrawCursor(DrawingContext context, DisplayFit fit)
    {
        var (x, y) = fit.ToView(_pad.CursorX, _pad.CursorY);
        var centre = new Point(x, y);
        var brush = CursorBrush ?? Brushes.Gold;

        context.DrawEllipse(null, new Pen(Brushes.Black, 3.5), centre, 9.5, 9.5);
        context.DrawEllipse(null, new Pen(brush, 2), centre, 9.5, 9.5);
        context.DrawEllipse(brush, new Pen(Brushes.Black, 1), centre, 2.5, 2.5);
    }

    private DisplayFit Fit()
        => DisplayFit.Compute(Bounds.Width, Bounds.Height, _guestWidth, _guestHeight, _zoom, _panX, _panY);

    // ---- input ----

    private bool Driving => Display is { IsUserDriving: true };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;

        if (!Driving)
        {
            // Watching: one finger pans the view. Nothing reaches the guest.
            _panFrom = e.GetPosition(this);
            return;
        }

        var (gx, gy) = Guest(e.GetPosition(this));
        Apply(_pad.Down(e.Pointer.Id, gx, gy, _clock.Elapsed.TotalMilliseconds));
        _hold.Start();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!Driving)
        {
            if (_panFrom is { } from)
            {
                var now = e.GetPosition(this);
                _panX += now.X - from.X;
                _panY += now.Y - from.Y;
                _panFrom = now;
                InvalidateVisual();
            }

            return;
        }

        var (gx, gy) = Guest(e.GetPosition(this));
        Apply(_pad.Move(e.Pointer.Id, gx, gy, _clock.Elapsed.TotalMilliseconds));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _panFrom = null;
        Apply(_pad.Up(e.Pointer.Id, _clock.Elapsed.TotalMilliseconds));
        if (!_pad.IsGesturing)
        {
            _hold.Stop();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panFrom = null;
        _pad.Cancel();
        _hold.Stop();
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        if (!_pinching)
        {
            _pinching = true;
            _zoomAtPinchStart = _zoom;
            // A pinch is not a scroll and not a tap: abandon whatever the fingers were becoming.
            _pad.Cancel();
        }

        _zoom = Math.Clamp(_zoomAtPinchStart * e.Scale, 1, MaxZoom);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPinchEnded(object? sender, PinchEndedEventArgs e)
    {
        _pinching = false;
        e.Handled = true;
    }

    /// <summary>A view point in guest units. Only deltas are used by the trackpad, so the letterbox
    /// offset cancels and is deliberately not subtracted.</summary>
    private (double X, double Y) Guest(Point point)
    {
        var fit = Fit();
        return fit.IsEmpty ? (point.X, point.Y) : (point.X / fit.Scale, point.Y / fit.Scale);
    }

    private void Apply(IReadOnlyList<TrackpadAction> actions)
    {
        if (actions.Count == 0 || Display is not { } display)
        {
            return;
        }

        foreach (var action in actions)
        {
            switch (action)
            {
                case TrackpadMove move:
                    _ = display.PointerMoveAsync(move.X, move.Y);
                    FollowCursor(move.X, move.Y);
                    break;
                case TrackpadClick click:
                    _ = ClickAsync(display, click);
                    break;
                case TrackpadScroll scroll:
                    _ = display.ScrollAsync(scroll.X, scroll.Y, scroll.Dx, scroll.Dy);
                    break;
                default:
                    break;
            }
        }

        InvalidateVisual();
    }

    private static async Task ClickAsync(DisplayViewModel display, TrackpadClick click)
    {
        await display.PointerMoveAsync(click.X, click.Y).ConfigureAwait(true);
        await display.PointerButtonAsync(click.Button, down: true).ConfigureAwait(true);
        await display.PointerButtonAsync(click.Button, down: false).ConfigureAwait(true);
    }

    /// <summary>Keeps the drawn cursor on screen while zoomed in. Losing the pointer off the edge with
    /// no way to find it is the failure everyone who has used a phone VNC client remembers.</summary>
    private void FollowCursor(int x, int y)
    {
        (_panX, _panY) = DisplayFit.Follow(
            Bounds.Width, Bounds.Height, _guestWidth, _guestHeight, _zoom, _panX, _panY, x, y);
    }

    // ---- keyboard ----

    /// <summary>Types finished text from the IME. Characters with no keysym are dropped rather than
    /// guessed at — see <see cref="DisplayKeysyms"/>.</summary>
    public void TypeText(string text)
    {
        if (Display is not { IsUserDriving: true } display)
        {
            return;
        }

        _ = TypeAsync(display, DisplayKeysyms.Map(text));
    }

    /// <summary>Sends one named key as a press and release (<c>Return</c>, <c>BackSpace</c>,
    /// <c>Escape</c>, <c>Tab</c>).</summary>
    public void PressKey(string keysym)
    {
        if (Display is not { IsUserDriving: true } display)
        {
            return;
        }

        _ = TypeAsync(display, [keysym]);
    }

    private static async Task TypeAsync(DisplayViewModel display, IReadOnlyList<string> keysyms)
    {
        foreach (var keysym in keysyms)
        {
            await display.KeyAsync(keysym, down: true).ConfigureAwait(true);
            await display.KeyAsync(keysym, down: false).ConfigureAwait(true);
        }
    }
}
