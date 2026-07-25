using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;

namespace Agnes.App.Mobile.Controls;

/// <summary>Which display edge a <see cref="SafeSpacer"/> reserves room for.</summary>
public enum SafeEdge
{
    Top,
    Bottom,
}

/// <summary>
/// A zero-width strut as tall as one display safe-area inset.
///
/// The app runs edge to edge — a bar's background should reach the screen edge while its content stays
/// clear of the status bar or the gesture handle. Rather than binding a Padding through an attached
/// property (which reflection bindings resolve at runtime, and get wrong), each bar places one of these
/// as its first or last child: the layout is then a plain measurement with nothing to resolve.
///
/// Collapses to nothing on a display with no inset, so the same markup works on every device.
/// </summary>
public sealed class SafeSpacer : Control
{
    public static readonly StyledProperty<SafeEdge> EdgeProperty =
        AvaloniaProperty.Register<SafeSpacer, SafeEdge>(nameof(Edge));

    private IInsetsManager? _insets;

    static SafeSpacer() => AffectsMeasure<SafeSpacer>(EdgeProperty);

    public SafeEdge Edge
    {
        get => GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _insets = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (_insets is not null)
        {
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            InvalidateMeasure();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _insets = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var padding = _insets?.SafeAreaPadding ?? default;
        return new Size(0, Edge == SafeEdge.Top ? padding.Top : padding.Bottom);
    }
}
