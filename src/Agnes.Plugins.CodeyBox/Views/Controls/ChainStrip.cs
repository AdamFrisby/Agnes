using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// A chain as a row of pips: one per step, in dependency order, left to right.
/// </summary>
/// <remarks>
/// The board's unit of reading is the chain and its unit of acting is the item, and this is what joins
/// them. Nine items authored in one second as a seven-step series were previously nine rows that said
/// nothing about each other; as a strip they are one row whose shape says where the work has got to —
/// three solid mint, one solid sky, and the rest hollow — and where it stopped, without opening
/// anything. Pips are clickable because the step you can see is the step you want to select; the
/// tooltip names them, so nothing here depends on the operator decoding a colour they have not been
/// taught yet.
/// </remarks>
public sealed class ChainStrip : ThemedDrawing
{
    private const double Pip = 8;
    private const double Gap = 3;

    /// <summary>Room above and below the pip for the selected step's ring.</summary>
    private const double Ring = 2;

    public static readonly StyledProperty<IReadOnlyList<Step>?> StepsProperty =
        AvaloniaProperty.Register<ChainStrip, IReadOnlyList<Step>?>(nameof(Steps));

    /// <summary>The item id the pane is showing, so its pip is ringed. Null rings nothing.</summary>
    public static readonly StyledProperty<string?> SelectedIdProperty =
        AvaloniaProperty.Register<ChainStrip, string?>(nameof(SelectedId));

    /// <summary>Selects a step. Parameter: the <see cref="Step"/> whose pip was clicked.</summary>
    public static readonly StyledProperty<ICommand?> StepCommandProperty =
        AvaloniaProperty.Register<ChainStrip, ICommand?>(nameof(StepCommand));

    static ChainStrip()
    {
        AffectsRender<ChainStrip>(StepsProperty, SelectedIdProperty);
        AffectsMeasure<ChainStrip>(StepsProperty);
    }

    public IReadOnlyList<Step>? Steps
    {
        get => GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    public string? SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    public ICommand? StepCommand
    {
        get => GetValue(StepCommandProperty);
        set => SetValue(StepCommandProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Steps?.Count ?? 0;
        var width = count == 0 ? 0 : (count * (Pip + Gap)) - Gap;
        return new Size(width, Pip + (Ring * 2));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StepsProperty)
        {
            // Composed here rather than on the record: naming every step is a fact about how this
            // control degrades when the pips alone are not enough, not a fact about the chain.
            ToolTip.SetTip(this, Describe(Steps));
        }

        if (change.Property == StepCommandProperty)
        {
            Cursor = StepCommand is null ? null : new Cursor(StandardCursorType.Hand);
        }
    }

    public override void Render(DrawingContext context)
    {
        // A drawn control with no fill is not hit-testable, and a strip whose pips cannot be clicked is
        // exactly the kind of silently-dead affordance this codebase already has a test class for.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (Steps is not { Count: > 0 } steps)
        {
            return;
        }

        var top = Math.Max(0, (Bounds.Height - Pip) / 2);
        for (var i = 0; i < steps.Count; i++)
        {
            var x = i * (Pip + Gap);
            if (x + Pip > Bounds.Width + 0.5)
            {
                // Ran out of room. Better a short strip than pips drawn over the text beside it.
                return;
            }

            var rect = new Rect(x, top, Pip, Pip);
            var (role, fill) = StepMarks.Of(steps[i].State);
            StepMarks.Draw(context, rect, Brush(role), fill);

            if (SelectedId is { Length: > 0 } selected && steps[i].Item.Id == selected)
            {
                context.DrawRectangle(null, Pen(1, ThemedRoles.Fg), new RoundedRect(rect.Inflate(1.6), 4));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Steps is not { Count: > 0 } steps || StepCommand is not { } command)
        {
            return;
        }

        var index = (int)Math.Floor(e.GetPosition(this).X / (Pip + Gap));
        if (index < 0 || index >= steps.Count)
        {
            return;
        }

        var step = steps[index];
        if (!command.CanExecute(step))
        {
            return;
        }

        command.Execute(step);

        // Stops the click reaching the row button underneath, which would select the chain's head and
        // undo the more specific thing the operator just asked for.
        e.Handled = true;
    }

    /// <summary>"3/7  Wire the broker — running", one line per step.</summary>
    internal static string? Describe(IReadOnlyList<Step>? steps)
    {
        if (steps is not { Count: > 0 })
        {
            return null;
        }

        var text = new StringBuilder();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var series = step.Series is { Length: > 0 } given
                ? given
                : string.Create(CultureInfo.CurrentCulture, $"{i + 1}/{steps.Count}");

            if (i > 0)
            {
                text.Append('\n');
            }

            text.Append(series).Append("  ").Append(step.Item.Title)
                .Append(" — ").Append(StepMarks.Word(step.State));
        }

        return text.ToString();
    }
}
