using System.Text.RegularExpressions;
using Agnes.Ui.Core.Markdown;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

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

    private readonly MarkdownFencePlugin _fencePlugin;
    private readonly global::Markdown.Avalonia.Markdown _engine;
    private readonly HashSet<int> _sourceFences = [];

    public MarkdownBlock()
    {
        _fencePlugin = new MarkdownFencePlugin(CreateFence);
        _engine = new global::Markdown.Avalonia.Markdown
        {
            Plugins = MarkdownEngine.CreatePlugins(_fencePlugin),
        };

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
            _fencePlugin.BeginRender(markdown);
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

    private Control CreateFence(string source, int ordinal)
        => new MarkdownFenceView(
            source,
            _sourceFences.Contains(ordinal),
            showSource =>
            {
                if (showSource)
                {
                    _sourceFences.Add(ordinal);
                }
                else
                {
                    _sourceFences.Remove(ordinal);
                }
            });
}

internal sealed class MarkdownFencePlugin(Func<string, int, Control> create) : IMdAvPlugin
{
    private int _nextOrdinal;
    private string[] _originalFences = [];

    public void BeginRender(string? markdown)
    {
        _nextOrdinal = 0;
        _originalFences = MarkdownFence.Pattern.Matches(markdown ?? string.Empty)
            .Select(MarkdownFence.Body)
            .ToArray();
    }

    public void Setup(SetupInfo info)
    {
        info.Register(new MarkdownCodeFenceOverride(info, CreateFence));

        var parser = BlockParser.New(
            MarkdownFence.Pattern,
            MarkdownFence.ParserName,
            (Match match, ParseStatus _) => CreateFence(match));

        // Markdown.Avalonia only has a built-in parser for backticks. The override above handles that
        // parser; this parser additionally claims CommonMark's tilde form.
        info.RegisterTop(parser);
    }

    private Control CreateFence(Match match)
    {
        var ordinal = _nextOrdinal++;
        var source = ordinal < _originalFences.Length
            ? _originalFences[ordinal]
            : MarkdownFence.Body(match);
        return create(source, ordinal);
    }
}

internal sealed class MarkdownCodeFenceOverride(
    SetupInfo setup,
    Func<Match, Control> create) : IBlockOverride
{
    private const string BuiltinParserName = "CodeBlocksWithLangEvaluator";

    public string ParserName => BuiltinParserName;

    public IEnumerable<Control> Convert(
        string text,
        Match match,
        ParseStatus status,
        IMarkdownEngine engine,
        out int parseTextBegin,
        out int parseTextEnd)
    {
        var language = match.Groups[2].Value.Trim();
        if (language.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            || language.Equals("md", StringComparison.OrdinalIgnoreCase))
        {
            var markdownFence = MarkdownFence.Pattern.Match(text, match.Index);
            if (markdownFence.Success && markdownFence.Index == match.Index)
            {
                parseTextBegin = markdownFence.Index;
                parseTextEnd = markdownFence.Index + markdownFence.Length;
                return [create(markdownFence)];
            }
        }

        var closing = new Regex(
            $"\\n[ ]*{Regex.Escape(match.Groups[1].Value)}[ ]*\\n",
            RegexOptions.CultureInvariant).Match(text, match.Index + match.Length);
        if (!closing.Success && !setup.EnablePreRenderingCodeBlock)
        {
            parseTextBegin = -1;
            parseTextEnd = -1;
            return null!;
        }

        parseTextBegin = match.Index;
        parseTextEnd = closing.Success ? closing.Index + closing.Length : text.Length;

        // The custom override is first so it can claim markdown/md. Send every other backtick fence
        // through an unmodified full engine, preserving normal code rendering and syntax highlighting.
        var fallback = new global::Markdown.Avalonia.Markdown
        {
            Plugins = new global::Markdown.Avalonia.Full.MdAvPlugins(),
        };
        var source = text[parseTextBegin..parseTextEnd];
        return fallback.RunBlockGamut(source, status);
    }
}

internal static class MarkdownEngine
{
    public static global::Markdown.Avalonia.Full.MdAvPlugins CreatePlugins(IMdAvPlugin fencePlugin)
    {
        var plugins = new global::Markdown.Avalonia.Full.MdAvPlugins();
        // Parser overrides are first-match-wins; Agnes must precede SyntaxHigh's code-fence override.
        plugins.Plugins.Insert(0, fencePlugin);
        return plugins;
    }
}

internal sealed class MarkdownFenceView : Border
{
    private readonly string _source;
    private readonly Action<bool> _modeChanged;
    private readonly Button _toggle;
    private readonly ContentControl _body;
    private bool _showSource;

    public MarkdownFenceView(string source, bool showSource, Action<bool> modeChanged)
    {
        _source = source;
        _showSource = showSource;
        _modeChanged = modeChanged;
        Classes.Add("markdownFence");

        _toggle = new Button
        {
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _toggle.Classes.Add("markdownFenceToggle");
        _toggle.Click += (_, _) =>
        {
            _showSource = !_showSource;
            _modeChanged(_showSource);
            ShowCurrentMode();
        };

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        toolbar.Classes.Add("markdownFenceToolbar");
        Grid.SetColumn(_toggle, 1);
        toolbar.Children.Add(_toggle);

        _body = new ContentControl();
        _body.Classes.Add("markdownFenceBody");

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        layout.Children.Add(toolbar);
        Grid.SetRow(_body, 1);
        layout.Children.Add(_body);
        Child = layout;

        ShowCurrentMode();
    }

    private void ShowCurrentMode()
    {
        _toggle.Content = _showSource ? "Render" : "Code";
        AutomationProperties.SetName(
            _toggle,
            _showSource ? "Render Markdown block" : "Show Markdown block source");
        ToolTip.SetTip(
            _toggle,
            _showSource ? "Render this code block as Markdown" : "Show the Markdown source as code");
        _body.Content = _showSource ? CreateSource() : new MarkdownBlock { Markdown = _source };
    }

    private Control CreateSource()
    {
        var text = new SelectableTextBlock
        {
            Text = _source,
            TextWrapping = TextWrapping.Wrap,
        };
        text.Classes.Add("markdownFenceSource");
        return text;
    }
}
