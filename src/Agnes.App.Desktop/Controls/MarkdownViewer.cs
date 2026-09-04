using System.Text.RegularExpressions;
using Agnes.Ui.Core.Markdown;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// The desktop Markdown viewer with Agnes' opt-in rendering for <c>markdown</c>/<c>md</c> fences.
/// It remains a MarkdownScrollViewer, preserving the library's cross-block selection and scroll
/// behaviour, while the custom parser only claims explicitly-labelled Markdown fences.
/// </summary>
public sealed class MarkdownViewer : global::Markdown.Avalonia.Full.MarkdownScrollViewer
{
    private readonly MarkdownFencePlugin _fencePlugin;
    private readonly HashSet<int> _sourceFences = [];

    public MarkdownViewer()
    {
        _fencePlugin = new MarkdownFencePlugin(CreateFence);
        Plugins = MarkdownEngine.CreatePlugins(_fencePlugin);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == MarkdownProperty)
        {
            _fencePlugin.BeginRender(change.GetNewValue<string?>());
        }

        base.OnPropertyChanged(change);
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

    public static global::Markdown.Avalonia.Markdown Create(MarkdownFencePlugin fencePlugin)
        => new() { Plugins = CreatePlugins(fencePlugin) };
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
        _toggle.Classes.Add("ghost");
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
        ToolTip.SetTip(
            _toggle,
            _showSource ? "Render this code block as Markdown" : "Show the Markdown source as code");
        _body.Content = _showSource ? CreateSource() : new InlineMarkdown(_source);
    }

    private Control CreateSource()
    {
        var text = new SelectableTextBlock
        {
            Text = _source,
            TextWrapping = TextWrapping.NoWrap,
        };
        text.Classes.Add("markdownFenceSource");
        return new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }
}

internal sealed class InlineMarkdown : ContentControl
{
    private readonly MarkdownFencePlugin _fencePlugin;
    private readonly HashSet<int> _sourceFences = [];

    public InlineMarkdown(string source)
    {
        Styles.Add(new StyleInclude(new Uri("avares://Agnes.App.Desktop/"))
        {
            Source = new Uri("avares://Agnes.App.Desktop/Themes/MarkdownNestedCodeStyles.axaml"),
        });
        _fencePlugin = new MarkdownFencePlugin(CreateFence);
        var engine = MarkdownEngine.Create(_fencePlugin);

        try
        {
            _fencePlugin.BeginRender(source);
            Content = engine.Transform(source);
        }
        catch
        {
            var fallback = new SelectableTextBlock { Text = source, TextWrapping = TextWrapping.Wrap };
            fallback.Classes.Add("markdownFenceSource");
            Content = fallback;
        }
    }

    private Control CreateFence(string source, int ordinal)
        => new MarkdownFenceView(
            source,
            _sourceFences.Contains(ordinal),
            showSource =>
            {
                if (showSource) { _sourceFences.Add(ordinal); }
                else { _sourceFences.Remove(ordinal); }
            });
}
