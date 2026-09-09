using System.ComponentModel;
using Agnes.App.Desktop.Controls;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Agnes.App.Desktop.Views;

/// <summary>
/// The Avalonia glue for the graphical sandbox's screen: decode frames onto a bitmap, and turn this control's
/// pointer and keyboard into guest input. All state that can be reasoned about without a window lives in
/// <see cref="DisplayViewModel"/>; the two genuinely testable pieces here — the fit transform and the keysym
/// table — are pulled out into <see cref="DisplayFit"/> and <see cref="X11Keysyms"/>.
/// </summary>
public partial class ScreenPanelView : UserControl
{
    // A frame every ~16 ms is one per display refresh; anything more is bandwidth the guest cannot use.
    private static readonly TimeSpan PointerInterval = TimeSpan.FromMilliseconds(16);

    // Resize storms while dragging a splitter would otherwise renegotiate quality dozens of times a second.
    private static readonly TimeSpan ResizeDebounce = TimeSpan.FromMilliseconds(250);

    // Two Escapes further apart than this are two Escapes for the guest, not a request to leave.
    private static readonly TimeSpan EscapeWindow = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _pointerTimer;
    private readonly DispatcherTimer _resizeTimer;

    private DisplayViewModel? _vm;
    private RenderTargetBitmap? _canvas;
    private Panel? _surface;
    private Image? _screen;
    private TextBlock? _placeholder;
    private Border? _captureBadge;

    private (int X, int Y)? _pendingPointer;
    private DateTimeOffset _lastEscape = DateTimeOffset.MinValue;
    private bool _keyboardCaptured;

    public ScreenPanelView()
    {
        InitializeComponent();
        _surface = this.FindControl<Panel>("Surface");
        _screen = this.FindControl<Image>("Screen");
        _placeholder = this.FindControl<TextBlock>("Placeholder");
        _captureBadge = this.FindControl<Border>("CaptureBadge");

        _pointerTimer = new DispatcherTimer { Interval = PointerInterval };
        _pointerTimer.Tick += (_, _) => FlushPointer();

        _resizeTimer = new DispatcherTimer { Interval = ResizeDebounce };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); NegotiateQuality(); };

        if (_surface is { } surface)
        {
            surface.PointerMoved += OnPointerMoved;
            surface.PointerPressed += OnPointerPressed;
            surface.PointerReleased += OnPointerReleased;
            surface.PointerWheelChanged += OnPointerWheel;
            surface.KeyDown += OnKeyDown;
            surface.KeyUp += OnKeyUp;
            surface.TextInput += OnTextInput;
            surface.GotFocus += (_, _) => SetCaptured(true);
            surface.LostFocus += (_, _) => SetCaptured(false);
        }

        DataContextChanged += (_, _) => Rebind();
        AttachedToVisualTree += (_, _) => { Rebind(); NegotiateQuality(); };
        DetachedFromVisualTree += (_, _) => Unbind();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ---- binding ----

    private void Rebind()
    {
        var next = DataContext as DisplayViewModel;
        if (ReferenceEquals(next, _vm))
        {
            return;
        }

        Unbind();
        _vm = next;
        if (_vm is null)
        {
            UpdatePlaceholder();
            return;
        }

        _vm.FrameArrived += OnFrame;
        _vm.PropertyChanged += OnViewModelChanged;
        UpdatePlaceholder();
    }

    private void Unbind()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.FrameArrived -= OnFrame;
        _vm.PropertyChanged -= OnViewModelChanged;
        _pointerTimer.Stop();
        _vm = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplayViewModel.Info)
            or nameof(DisplayViewModel.IsConnected)
            or nameof(DisplayViewModel.IsBusy)
            or nameof(DisplayViewModel.Status))
        {
            UpdatePlaceholder();
            if (e.PropertyName == nameof(DisplayViewModel.Info))
            {
                // The geometry only becomes known with the Info frame, so quality is negotiated once we can
                // say something meaningful about it.
                NegotiateQuality();
            }
        }
    }

    private void UpdatePlaceholder()
    {
        if (_placeholder is not { } placeholder)
        {
            return;
        }

        var text = _vm switch
        {
            null => "This session has no display.",
            { IsBusy: true } => "Connecting…",
            { IsConnected: false, Status.Length: > 0 } vm => vm.Status,
            { IsConnected: false } => "Not connected. Press Connect to open the screen.",
            { LastFrameAt: null } => "Waiting for the first frame…",
            _ => string.Empty,
        };

        placeholder.Text = text;
        placeholder.IsVisible = text.Length > 0;
    }

    // ---- painting ----

    /// <summary>
    /// Paints one frame. A <see cref="DisplayFrameKind.Full"/> frame replaces the picture; a
    /// <see cref="DisplayFrameKind.Tile"/> repaints only the rectangle its header names.
    /// </summary>
    /// <remarks>
    /// The canvas is a <see cref="RenderTargetBitmap"/> rather than a locked <see cref="WriteableBitmap"/>
    /// because the host is allowed to send a frame SMALLER than the display it depicts — that is what
    /// <see cref="DisplayQuality"/> asks for, and the simulated host does it. Blitting raw pixels would then
    /// need a scaler of our own; <c>DrawImage</c> into the destination rect gets Skia's, which is both correct
    /// and the fast path.
    /// </remarks>
    private void OnFrame(DisplayFrame frame)
    {
        var displayWidth = frame.Header.DisplayWidth;
        var displayHeight = frame.Header.DisplayHeight;
        if (displayWidth == 0 || displayHeight == 0)
        {
            return;
        }

        Bitmap decoded;
        try
        {
            using var stream = new MemoryStream(frame.Payload.ToArray(), writable: false);
            decoded = new Bitmap(stream);
        }
        catch
        {
            // A frame we cannot decode is one frame, not a broken session: the next one very likely decodes.
            return;
        }

        using (decoded)
        {
            EnsureCanvas(displayWidth, displayHeight);
            if (_canvas is not { } canvas)
            {
                return;
            }

            var destination = frame.Header.Kind == DisplayFrameKind.Tile && frame.Header.Width > 0 && frame.Header.Height > 0
                ? new Rect(frame.Header.X, frame.Header.Y, frame.Header.Width, frame.Header.Height)
                : new Rect(0, 0, displayWidth, displayHeight);

            using var context = canvas.CreateDrawingContext(clear: false);
            context.DrawImage(decoded, new Rect(0, 0, decoded.PixelSize.Width, decoded.PixelSize.Height), destination);
        }

        // The Image holds the same bitmap object across frames, so nudge it to repaint.
        _screen?.InvalidateVisual();
        UpdatePlaceholder();
    }

    private void EnsureCanvas(int width, int height)
    {
        if (_canvas is { } existing && existing.PixelSize.Width == width && existing.PixelSize.Height == height)
        {
            return;
        }

        _canvas?.Dispose();
        _canvas = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        if (_screen is { } image)
        {
            image.Source = _canvas;
        }
    }

    // ---- quality ----

    /// <summary>
    /// Tells the host what this panel can actually use: never more pixels across than the panel is wide, and
    /// 30 fps, which is smooth for a desktop and half the bandwidth of 60.
    /// </summary>
    private void NegotiateQuality()
    {
        if (_vm is not { IsConnected: true } vm)
        {
            return;
        }

        var width = (int)Math.Round(Bounds.Width * ((VisualRoot as TopLevel)?.RenderScaling ?? 1.0));
        if (width <= 0)
        {
            return;
        }

        _ = vm.SetQualityAsync(Math.Clamp(width, 320, 4096), 30);
    }

    // ---- pointer ----

    /// <summary>The current letterbox fit, from the guest geometry and the panel's size.</summary>
    private DisplayFit Fit()
        => _vm is { Width: > 0, Height: > 0 } vm && _surface is { } surface
            ? DisplayFit.Compute(vm.Width, vm.Height, surface.Bounds.Width, surface.Bounds.Height)
            : default;

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is not { IsConnected: true } || _surface is not { } surface)
        {
            return;
        }

        var point = e.GetPosition(surface);
        var fit = Fit();
        if (fit.Scale <= 0)
        {
            return;
        }

        // Coalesce: only the newest position matters, so keep one and let the timer send it.
        _pendingPointer = fit.ToGuest(point.X, point.Y);
        if (!_pointerTimer.IsEnabled)
        {
            FlushPointer();
            _pointerTimer.Start();
        }
    }

    private void FlushPointer()
    {
        if (_pendingPointer is not { } pending)
        {
            _pointerTimer.Stop();
            return;
        }

        _pendingPointer = null;
        _ = _vm?.PointerMoveAsync(pending.X, pending.Y);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _surface?.Focus();
        SendButton(e, down: true);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => SendButton(e, down: false);

    private void SendButton(PointerEventArgs e, bool down)
    {
        if (_vm is not { IsConnected: true } vm || _surface is not { } surface)
        {
            return;
        }

        var fit = Fit();
        var point = e.GetPosition(surface);
        if (!fit.Contains(point.X, point.Y))
        {
            return;
        }

        // Move first: a guest that receives a click with no preceding motion clicks wherever the pointer
        // happened to be, which is the classic remote-desktop off-by-one-click bug.
        var (x, y) = fit.ToGuest(point.X, point.Y);
        _pendingPointer = null;
        _ = vm.PointerMoveAsync(x, y);

        var properties = e.GetCurrentPoint(surface).Properties;
        var button = properties.IsRightButtonPressed || properties.PointerUpdateKind is PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased
            ? 2
            : properties.IsMiddleButtonPressed || properties.PointerUpdateKind is PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased
                ? 1
                : 0;
        _ = vm.PointerButtonAsync(button, down);
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is not { IsConnected: true } vm || _surface is not { } surface)
        {
            return;
        }

        var fit = Fit();
        var point = e.GetPosition(surface);
        if (!fit.Contains(point.X, point.Y))
        {
            return;
        }

        var (x, y) = fit.ToGuest(point.X, point.Y);
        _ = vm.ScrollAsync(x, y, (int)Math.Round(e.Delta.X), (int)Math.Round(e.Delta.Y));
        e.Handled = true;
    }

    // ---- keyboard ----

    /// <summary>
    /// Keyboard capture is explicit — clicking the surface focuses it, and the badge says so. Without that,
    /// every keystroke meant for the composer would vanish into the guest with no way to tell.
    /// </summary>
    private void SetCaptured(bool captured)
    {
        _keyboardCaptured = captured;
        if (_captureBadge is { } badge)
        {
            badge.IsVisible = captured;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsConnected: true } vm || !_keyboardCaptured)
        {
            return;
        }

        // Escape twice inside a second gives the keyboard back. A single Escape still reaches the guest,
        // because Escape is a key a person genuinely needs to send.
        if (e.Key == Key.Escape)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastEscape <= EscapeWindow)
            {
                _lastEscape = DateTimeOffset.MinValue;
                ReleaseKeyboard();
                e.Handled = true;
                return;
            }

            _lastEscape = now;
        }

        if (X11Keysyms.For(e.Key) is { } keysym)
        {
            _ = vm.KeyAsync(keysym, down: true);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsConnected: true } vm || !_keyboardCaptured)
        {
            return;
        }

        if (X11Keysyms.For(e.Key) is { } keysym)
        {
            _ = vm.KeyAsync(keysym, down: false);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Characters the key table deliberately does not model — punctuation, anything layout- or AltGr-dependent
    /// — arrive here already resolved by the platform, which is the only place that knows the layout.
    /// </summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_vm is not { IsConnected: true } vm || !_keyboardCaptured)
        {
            return;
        }

        if (X11Keysyms.ForText(e.Text) is { } keysym)
        {
            _ = vm.KeyAsync(keysym, down: true);
            _ = vm.KeyAsync(keysym, down: false);
            e.Handled = true;
        }
    }

    private void ReleaseKeyboard()
    {
        SetCaptured(false);
        if (_vm is { } vm)
        {
            vm.Status = "Keyboard released — click the screen to type into it again.";
        }

        // Hand focus to the panel itself, which is not a keyboard sink, so the next keystroke goes where the
        // rest of the app expects.
        Focus();
    }
}
