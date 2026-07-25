using Agnes.App.Mobile.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// Hosts the active <see cref="SheetViewModel"/>: slides it up, dims what's behind it, and lets it be
/// dragged or flung back down.
///
/// Dismissal has three routes on purpose — the grabber, the scrim, and the system back gesture (routed
/// by the shell) — because on a phone the one you reach for depends on which hand is free.
/// </summary>
public partial class SheetHost : UserControl
{
    /// <summary>How far down the sheet must be dragged before release dismisses it, as a fraction of its
    /// height. Below this it springs back.</summary>
    private const double DismissFraction = 0.32;

    /// <summary>A downward flick dismisses regardless of distance, above this speed (px per second).</summary>
    private const double FlingVelocity = 900;

    public static readonly StyledProperty<SheetViewModel?> SheetProperty =
        AvaloniaProperty.Register<SheetHost, SheetViewModel?>(nameof(Sheet));

    private Border _panel = null!;
    private Border _scrim = null!;
    private Control _grabber = null!;
    private TextBlock _title = null!;
    private TextBlock _subtitle = null!;

    private bool _dragging;
    private double _dragStart;
    private double _offset;
    private double _lastY;
    private DateTime _lastMove;
    private double _velocity;

    public SheetHost()
    {
        AvaloniaXamlLoader.Load(this);
        _panel = this.FindControl<Border>("Panel")!;
        _scrim = this.FindControl<Border>("Scrim")!;
        _grabber = this.FindControl<Control>("Grabber")!;
        _title = this.FindControl<TextBlock>("SheetTitle")!;
        _subtitle = this.FindControl<TextBlock>("SheetSubtitle")!;

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => RequestClose();

        _scrim.PointerPressed += (_, _) => RequestClose();

        // The sheet layer spans the whole window, so with no sheet open it must be transparent to
        // input — otherwise it silently eats every tap meant for the screen behind it. Present() sets
        // this per sheet, but it only runs on a *change*, so the initial state has to be set here.
        IsHitTestVisible = false;

        _grabber.PointerPressed += OnGrabPressed;
        _grabber.PointerMoved += OnGrabMoved;
        _grabber.PointerReleased += OnGrabReleased;
        _grabber.PointerCaptureLost += (_, _) => Settle(dismiss: false);
    }

    /// <summary>The sheet to present, or null for none.</summary>
    public SheetViewModel? Sheet
    {
        get => GetValue(SheetProperty);
        set => SetValue(SheetProperty, value);
    }

    /// <summary>Raised when the user dismissed the sheet by gesture or button.</summary>
    public event EventHandler? Dismissed;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SheetProperty)
        {
            Present(change.GetNewValue<SheetViewModel?>());
        }
    }

    private void Present(SheetViewModel? sheet)
    {
        // Deliberately NOT `DataContext = sheet`: this control's own DataContext is the shell, which is
        // what the Sheet property is bound to. Re-pointing it would make that binding resolve to null and
        // close the sheet the instant it opened.
        _panel.DataContext = sheet;
        IsHitTestVisible = sheet is not null;

        // The header is filled in rather than bound: with no sheet the panel inherits the shell's
        // DataContext, and bound Title/Subtitle would resolve against ShellViewModel and log an error
        // on every launch.
        _title.Text = sheet?.Title;
        _subtitle.Text = sheet?.Subtitle;
        _subtitle.IsVisible = !string.IsNullOrWhiteSpace(sheet?.Subtitle);

        if (sheet is null)
        {
            _scrim.Opacity = 0;
            _scrim.IsHitTestVisible = false;
            SetOffset(_panel.Bounds.Height > 0 ? _panel.Bounds.Height : 800);
            // Let the slide-out finish before the panel leaves the tree.
            DispatcherTimer.RunOnce(() =>
            {
                if (Sheet is null)
                {
                    _panel.IsVisible = false;
                }
            }, TimeSpan.FromMilliseconds(260));
            return;
        }

        _panel.IsVisible = true;
        _panel.MaxHeight = Bounds.Height > 0 ? Bounds.Height * sheet.HeightFraction : double.PositiveInfinity;

        // Start off-screen without animating, then release to 0 on the next frame so the transition runs.
        SetOffset(Bounds.Height > 0 ? Bounds.Height : 800, animate: false);
        Dispatcher.UIThread.Post(() =>
        {
            _scrim.IsHitTestVisible = true;
            _scrim.Opacity = 1;
            SetOffset(0);
        }, DispatcherPriority.Render);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (Sheet is { } sheet && finalSize.Height > 0)
        {
            _panel.MaxHeight = finalSize.Height * sheet.HeightFraction;
        }

        return result;
    }

    private void SetOffset(double y, bool animate = true)
    {
        _offset = Math.Max(0, y);
        var transitions = _panel.Transitions;
        if (!animate)
        {
            _panel.Transitions = null;
        }

        _panel.RenderTransform = TransformOperations.Parse($"translateY({_offset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px)");

        if (!animate)
        {
            _panel.Transitions = transitions;
        }
    }

    // ---- drag ----

    private void OnGrabPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this).Y;
        _lastY = _dragStart;
        _lastMove = DateTime.UtcNow;
        _velocity = 0;
        e.Pointer.Capture(_grabber);
    }

    private void OnGrabMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var y = e.GetPosition(this).Y;
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastMove).TotalSeconds;
        if (elapsed > 0.001)
        {
            _velocity = (y - _lastY) / elapsed;
            _lastMove = now;
            _lastY = y;
        }

        // Follow the finger downward only — dragging a sheet up past its natural top is a rubber-band
        // effect that costs more than it earns here.
        SetOffset(Math.Max(0, y - _dragStart), animate: false);
    }

    private void OnGrabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        e.Pointer.Capture(null);
        var height = _panel.Bounds.Height;
        var dismiss = _velocity > FlingVelocity || (height > 0 && _offset > height * DismissFraction);
        Settle(dismiss);
    }

    private void Settle(bool dismiss)
    {
        _dragging = false;
        if (dismiss)
        {
            RequestClose();
        }
        else
        {
            SetOffset(0);
        }
    }

    private void RequestClose() => Dismissed?.Invoke(this, EventArgs.Empty);
}
