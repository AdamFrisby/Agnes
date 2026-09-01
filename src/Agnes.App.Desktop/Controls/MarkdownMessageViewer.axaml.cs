using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// Presents an assistant response as one code-block surface. The response is rendered as Markdown by
/// default; the top-right toggle exposes the exact source without changing the surrounding surface.
/// </summary>
public partial class MarkdownMessageViewer : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownMessageViewer, string?>(nameof(Markdown));

    private Button? _modeToggle;
    private Grid? _modeToolbar;
    private MarkdownViewer? _renderedMarkdown;
    private ScrollViewer? _sourceScroller;
    private bool _showSource;

    public MarkdownMessageViewer()
    {
        AvaloniaXamlLoader.Load(this);
        _modeToggle = this.FindControl<Button>("ModeToggle");
        _modeToolbar = this.FindControl<Grid>("ModeToolbar");
        _renderedMarkdown = this.FindControl<MarkdownViewer>("RenderedMarkdown");
        _sourceScroller = this.FindControl<ScrollViewer>("SourceScroller");
        UpdateCodeFenceShape(Markdown);
    }

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
            UpdateCodeFenceShape(change.GetNewValue<string?>());
        }
    }

    private void UpdateCodeFenceShape(string? markdown)
    {
        if (_renderedMarkdown is null)
        {
            return;
        }

        const string className = "standaloneCodeMessage";
        var isStandaloneCode = IsStandaloneOrdinaryCodeFence(markdown);
        if (isStandaloneCode)
        {
            if (!_renderedMarkdown.Classes.Contains(className))
            {
                _renderedMarkdown.Classes.Add(className);
            }
        }
        else
        {
            _renderedMarkdown.Classes.Remove(className);
        }

        if (_modeToolbar is not null)
        {
            _modeToolbar.IsVisible = !isStandaloneCode;
        }

        // A streaming reply can change shape after the user has opened its raw source. If it ends up
        // as a standalone fence, return to rendered mode before hiding the only way back.
        if (isStandaloneCode && _showSource)
        {
            SetSourceMode(false);
        }
    }

    private static bool IsStandaloneOrdinaryCodeFence(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return false;
        }

        var lines = markdown.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var opening = lines[0];
        var indent = 0;
        while (indent < opening.Length && opening[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3 || indent == opening.Length || opening[indent] is not ('`' or '~'))
        {
            return false;
        }

        var fenceCharacter = opening[indent];
        var fenceLength = 0;
        while (indent + fenceLength < opening.Length && opening[indent + fenceLength] == fenceCharacter)
        {
            fenceLength++;
        }

        if (fenceLength < 3)
        {
            return false;
        }

        var info = opening[(indent + fenceLength)..].Trim();
        if (fenceCharacter == '`' && info.Contains('`'))
        {
            return false;
        }

        var languageEnd = info.IndexOfAny([' ', '\t']);
        var language = languageEnd < 0 ? info : info[..languageEnd];
        if (language.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            || language.Equals("md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineIndent = 0;
            while (lineIndent < line.Length && line[lineIndent] == ' ' && lineIndent <= 3)
            {
                lineIndent++;
            }

            if (lineIndent > 3)
            {
                continue;
            }

            var runLength = 0;
            while (lineIndent + runLength < line.Length
                && line[lineIndent + runLength] == fenceCharacter)
            {
                runLength++;
            }

            if (runLength >= fenceLength
                && string.IsNullOrWhiteSpace(line[(lineIndent + runLength)..]))
            {
                return lines[(lineIndex + 1)..].All(string.IsNullOrWhiteSpace);
            }
        }

        // Keep an unfinished streaming fence flat; it can be reclassified when more content arrives.
        return true;
    }

    private void OnToggleMode(object? sender, RoutedEventArgs e)
    {
        SetSourceMode(!_showSource);
    }

    private void SetSourceMode(bool showSource)
    {
        _showSource = showSource;

        if (_renderedMarkdown is not null)
        {
            _renderedMarkdown.IsVisible = !_showSource;
        }

        if (_sourceScroller is not null)
        {
            _sourceScroller.IsVisible = _showSource;
        }

        if (_modeToggle is not null)
        {
            _modeToggle.Content = _showSource ? "Render" : "Code";
            _modeToggle.SetValue(
                ToolTip.TipProperty,
                _showSource ? "Render this message as Markdown" : "Show the Markdown source as code");
            AutomationProperties.SetName(
                _modeToggle,
                _showSource ? "Render Markdown" : "Show Markdown source");
        }
    }
}
