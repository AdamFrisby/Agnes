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

    // ----------------------------------------------------------------------------------------------
    // THE CAP. A live chain of 32 steps drew a 32-pip strip 349px wide, which on a 600px runway took the
    // title's width away from it and ran the agent name over the ordinal beside it. Past a dozen pips the
    // strip has also stopped being readable as a shape — nobody counts 27 solid squares — so beyond that
    // it is drawn as its beginning, a gap, and its end: where the work started, that there is a lot of
    // it, and where it has got to. The tooltip still names every step, so nothing is lost, only folded.
    // ----------------------------------------------------------------------------------------------

    /// <summary>Longest strip drawn in full.</summary>
    internal const int MaxPips = 12;

    /// <summary>How much of a longer chain is drawn from the front.</summary>
    internal const int HeadPips = 8;

    /// <summary>…and from the end, which is where a long chain's news is.</summary>
    internal const int TailPips = 3;

    /// <summary>Width of the mark that stands for the steps not drawn.</summary>
    private const double Skip = 11;

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

    /// <summary>
    /// The slots drawn, left to right: a step, or <c>null</c> for the gap standing in for the ones folded
    /// away. The layout is computed rather than clipped so that measure, render and hit-testing cannot
    /// disagree about which pip is where.
    /// </summary>
    internal static IReadOnlyList<Step?> Lay(IReadOnlyList<Step>? steps)
    {
        if (steps is not { Count: > 0 })
        {
            return [];
        }

        if (steps.Count <= MaxPips)
        {
            return [.. steps];
        }

        return [.. steps.Take(HeadPips), null, .. steps.Skip(steps.Count - TailPips)];
    }

    private static double Widths(IReadOnlyList<Step?> slots)
    {
        var width = 0.0;
        for (var i = 0; i < slots.Count; i++)
        {
            width += (slots[i] is null ? Skip : Pip) + Gap;
        }

        return width <= 0 ? 0 : width - Gap;
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(Widths(Lay(Steps)), Pip + (Ring * 2));

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

        var slots = Lay(Steps);
        if (slots.Count == 0)
        {
            return;
        }

        var top = Math.Max(0, (Bounds.Height - Pip) / 2);
        var x = 0.0;
        foreach (var slot in slots)
        {
            var width = slot is null ? Skip : Pip;
            if (x + width > Bounds.Width + 0.5)
            {
                // Ran out of room. Better a short strip than pips drawn over the text beside it.
                return;
            }

            if (slot is null)
            {
                // The steps not drawn, as three dots on the pips' own baseline — never a text ellipsis,
                // which would be a character standing in for a glyph and would not take the role hue.
                var brush = Brush(ThemedRoles.Faint);
                for (var dot = 0; dot < 3; dot++)
                {
                    context.FillRectangle(brush, new Rect(x + (dot * 4), top + (Pip / 2) - 0.9, 1.8, 1.8));
                }

                x += width + Gap;
                continue;
            }

            var rect = new Rect(x, top, Pip, Pip);
            var (role, fill) = StepMarks.Of(slot.State);
            StepMarks.Draw(context, rect, Brush(role), fill);

            if (SelectedId is { Length: > 0 } selected && slot.Item.Id == selected)
            {
                context.DrawRectangle(null, Pen(1, ThemedRoles.Fg), new RoundedRect(rect.Inflate(1.6), 4));
            }

            x += width + Gap;
        }
    }

    /// <summary>The step under an x position, or null over the gap mark or past the end. Walked rather
    /// than divided, because a capped strip's slots are not all the same width.</summary>
    internal static Step? StepAt(IReadOnlyList<Step>? steps, double at)
    {
        var x = 0.0;
        foreach (var slot in Lay(steps))
        {
            var width = (slot is null ? Skip : Pip) + Gap;
            if (at < x + width)
            {
                return slot;
            }
            x += width;
        }
        return null;
    }

    private Step? _hovered;

    // ----------------------------------------------------------------------------------------------
    // THE TOOLTIP IS PER PIP. One tooltip naming all thirty-two steps is a list to read; the question a
    // pointer over a pip asks is "what is this one" — so the tip follows the pointer from pip to pip and
    // says that step's number, its state, its title and what is known about where it is. Over the gap
    // mark or off the end it falls back to the whole strip, which is the only way to reach the steps the
    // cap folded away.
    // ----------------------------------------------------------------------------------------------
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var step = StepAt(Steps, e.GetPosition(this).X);
        if (ReferenceEquals(step, _hovered))
        {
            return;
        }
        _hovered = step;
        ToolTip.SetTip(this, step is null ? Describe(Steps) : Tip(step, Steps));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
        ToolTip.SetTip(this, Describe(Steps));
    }

    /// <summary>The lines a pip's tooltip says, first to last: "Step 2 of 7 · running", the title, then
    /// the state the orchestrator reports with the agent and how long ago it moved, then the error if it
    /// carries one.</summary>
    internal static IReadOnlyList<string> TipLines(Step step, IReadOnlyList<Step>? steps)
    {
        var count = steps?.Count ?? 0;
        var position = step.Series is { Length: > 0 } given
            ? string.Create(CultureInfo.InvariantCulture, $"Step {given}")
            : string.Create(CultureInfo.InvariantCulture, $"Step {step.Index + 1} of {count}");
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{position} · {StepMarks.Word(step.State)}"),
            step.Item.Title,
        };
        var facts = new List<string> { step.Item.State };
        if (!string.IsNullOrWhiteSpace(step.Item.Agent))
        {
            facts.Add(step.Item.Agent);
        }
        if (step.Item.UpdatedAt != default)
        {
            facts.Add(WorkItemRow.Relative(step.Item.UpdatedAt));
        }
        lines.Add(string.Join(" · ", facts));
        if (step.Item.HasError)
        {
            lines.Add(step.Item.ErrorSummary);
        }
        return lines;
    }

    private Control Tip(Step step, IReadOnlyList<Step>? steps)
    {
        var lines = TipLines(step, steps);
        var panel = new StackPanel { Spacing = 2, MaxWidth = 360 };
        for (var i = 0; i < lines.Count; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = lines[i],
                TextWrapping = TextWrapping.Wrap,
                FontSize = i == 0 ? 12 : 11,
                FontWeight = i == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = Brush(i <= 1 ? ThemedRoles.Fg : ThemedRoles.Faint),
            });
        }
        return panel;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (StepCommand is not { } command)
        {
            return;
        }
        var step = StepAt(Steps, e.GetPosition(this).X);
        if (step is null || !command.CanExecute(step))
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
