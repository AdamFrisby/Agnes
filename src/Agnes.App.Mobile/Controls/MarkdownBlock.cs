using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Markdown.Avalonia;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// Renders markdown inline in the transcript.
///
/// Deliberately not <c>MarkdownScrollViewer</c>, which the desktop head uses: that control wraps its
/// output in its own ScrollViewer, and a ScrollViewer that permits horizontal scrolling measures its
/// content at infinite width — so on a 412dp screen every agent reply laid out one long line and got
/// clipped at the edge instead of wrapping. Here the engine's output is hosted directly, so it wraps to
/// the transcript's width like any other content.
///
/// Nesting a scroller inside the transcript's scroller would also have been wrong for touch: the two
/// would fight over the same vertical drag.
/// </summary>
public sealed class MarkdownBlock : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));

    private readonly global::Markdown.Avalonia.Markdown _engine = new();

    public MarkdownBlock()
    {
        // The engine emits bare controls; this supplies the heading/list/code/table styling.
        Styles.Add(MarkdownStyle.FluentAvalonia);
    }

    /// <summary>The markdown source. Re-rendered whenever it changes, which for a streaming reply is
    /// every chunk — the engine is fast enough at message scale, and the alternative (re-rendering only
    /// on turn end) would leave the reply invisible while it streamed.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Render(change.GetNewValue<string?>());
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);

        // The engine's output reports a desired width taken from its longest unbroken run of text rather
        // than from the space it was offered, so on a phone a long reply laid out past the screen edge
        // instead of wrapping. Measuring the subtree again against the real constraint makes it re-wrap.
        // Cheap: it only happens when the first measure actually overflowed.
        if (!double.IsInfinity(availableSize.Width)
            && size.Width > availableSize.Width
            && Presenter?.Child is Layoutable child)
        {
            child.InvalidateMeasure();
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            return new Size(availableSize.Width, child.DesiredSize.Height);
        }

        return size;
    }

    private void Render(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            Content = null;
            return;
        }

        try
        {
            Content = _engine.Transform(markdown);
        }
        catch
        {
            // Malformed markdown must never lose the message: fall back to the raw text.
            Content = new SelectableTextBlock
            {
                Text = markdown,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
        }
    }
}
