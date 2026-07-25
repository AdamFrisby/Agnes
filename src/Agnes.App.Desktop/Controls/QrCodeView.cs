using Agnes.Ui.Core.Qr;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// Draws a <see cref="QrMatrix"/> as vector rectangles.
///
/// Modules are snapped to whole device pixels and adjacent dark modules in a row are merged into one
/// rectangle. Both matter: a QR drawn at fractional module sizes gets anti-aliased edges that cheap
/// phone scanners read badly, and one rectangle per module is thousands of draw calls for something
/// that redraws on every resize.
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<QrMatrix?> MatrixProperty =
        AvaloniaProperty.Register<QrCodeView, QrMatrix?>(nameof(Matrix));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush?>(nameof(Foreground), Brushes.Black);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush?>(nameof(Background), Brushes.White);

    static QrCodeView() => AffectsRender<QrCodeView>(MatrixProperty, ForegroundProperty, BackgroundProperty);

    public QrMatrix? Matrix
    {
        get => GetValue(MatrixProperty);
        set => SetValue(MatrixProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Matrix is not { } matrix || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // Whole-pixel modules, centred in whatever box we were given: a QR with half-pixel module edges
        // scans measurably worse than one that's slightly smaller and crisp.
        var available = Math.Min(Bounds.Width, Bounds.Height);
        var module = Math.Max(1, Math.Floor(available / matrix.Size));
        var drawn = module * matrix.Size;
        var offsetX = Math.Floor((Bounds.Width - drawn) / 2);
        var offsetY = Math.Floor((Bounds.Height - drawn) / 2);

        if (Background is { } background)
        {
            context.FillRectangle(background, new Rect(offsetX, offsetY, drawn, drawn));
        }

        if (Foreground is not { } foreground)
        {
            return;
        }

        for (var y = 0; y < matrix.Size; y++)
        {
            var x = 0;
            while (x < matrix.Size)
            {
                if (!matrix[x, y])
                {
                    x++;
                    continue;
                }

                // Merge the run of dark modules starting here into a single rectangle.
                var start = x;
                while (x < matrix.Size && matrix[x, y])
                {
                    x++;
                }

                context.FillRectangle(foreground, new Rect(
                    offsetX + (start * module), offsetY + (y * module), (x - start) * module, module));
            }
        }
    }
}
