using System.Text.RegularExpressions;
using Agnes.Ui.Core.Markdown;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;
using Markdown.Avalonia.Utils;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// The desktop Markdown viewer with Agnes' opt-in rendering for <c>markdown</c>/<c>md</c> fences.
/// It remains a MarkdownScrollViewer, preserving the library's cross-block selection and scroll
/// behaviour, while the custom parser only claims explicitly-labelled Markdown fences.
/// </summary>
/// <remarks>
/// Every viewer shares one plugin set (<see cref="MarkdownEngine.SharedPlugins"/>) — the library's full
/// set plus Agnes' fence plugin. Building that set is the expensive part of constructing a viewer (the
/// HTML plugin's parser tables are assembled by reflection and its patterns compiled), and it was being
/// done per transcript row: 17 ms of a row's 30 ms. This derives from the library's lean viewer rather
/// than its "Full" one for the same reason: the Full class exists only to construct a full plugin set in
/// its constructor, which this replaces anyway. The one plugin that needs to know which viewer it is
/// rendering for, the fence plugin, finds it through <see cref="FenceRendering"/>, which the viewer's
/// engine sets for the duration of each render.
/// </remarks>
public sealed class MarkdownViewer : global::Markdown.Avalonia.MarkdownScrollViewer, IFenceHost
{
    private readonly HashSet<int> _sourceFences = [];
    private int _nextOrdinal;
    private string[] _originalFences = [];

    public MarkdownViewer()
    {
        Plugins = MarkdownEngine.SharedPlugins;
        // The library renders from several places — the Markdown setter, an attach, a style change — all
        // through its engine, so the engine is where this viewer is made the current fence host.
        Engine = new ScopedEngine(this, new global::Markdown.Avalonia.Markdown { Plugins = MarkdownEngine.SharedPlugins });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == MarkdownProperty)
        {
            BeginRender(change.GetNewValue<string?>());
        }

        base.OnPropertyChanged(change);
    }

    /// <summary>A re-render of the same markdown (a theme change, an attach) starts the fence count over.</summary>
    void IFenceHost.BeginRender() => _nextOrdinal = 0;

    private void BeginRender(string? markdown)
    {
        _nextOrdinal = 0;
        _originalFences = MarkdownFence.Pattern.Matches(markdown ?? string.Empty)
            .Select(MarkdownFence.Body)
            .ToArray();
    }

    /// <summary>The fence plugin asks the rendering viewer for its next fence: the source as it was
    /// written (the parser's match has been normalised), and a toggle whose state survives re-renders.</summary>
    public Control NextFence(Match match)
    {
        var ordinal = _nextOrdinal++;
        var source = ordinal < _originalFences.Length
            ? _originalFences[ordinal]
            : MarkdownFence.Body(match);
        return new MarkdownFenceView(
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
}

/// <summary>Whatever is rendering markdown and owns the fences in it: a viewer, or a fence's own nested
/// render. Supplies each fence as the parser reaches it, with the source as written and a toggle whose
/// state survives re-renders.</summary>
internal interface IFenceHost
{
    /// <summary>Called by the engine at the start of every transform, so ordinals restart from zero.</summary>
    void BeginRender();

    Control NextFence(Match match);
}

/// <summary>
/// The engine a viewer renders with: the library's own, with every transform run as its host — so the
/// shared fence plugin, which has no state, always finds the viewer (or nested fence) whose fences it is
/// producing. The whole render is forced inside the scope, because the library builds its document
/// lazily and would otherwise run the parsers after the scope had closed.
/// </summary>
internal sealed class ScopedEngine(IFenceHost host, global::Markdown.Avalonia.Markdown inner) : IMarkdownEngine2
{
    public string AssetPathRoot { get => inner.AssetPathRoot; set => inner.AssetPathRoot = value; }
    public System.Windows.Input.ICommand? HyperlinkCommand { get => inner.HyperlinkCommand; set => inner.HyperlinkCommand = value; }
    public IContainerBlockHandler? ContainerBlockHandler { get => inner.ContainerBlockHandler; set => inner.ContainerBlockHandler = value; }
    public MdAvPlugins Plugins { get => inner.Plugins; set => inner.Plugins = value; }
    public bool UseResource { get => inner.UseResource; set => inner.UseResource = value; }
    public CascadeDictionary CascadeResources => inner.CascadeResources;
    public Avalonia.Controls.IResourceDictionary Resources { get => inner.Resources; set => inner.Resources = value; }

    public Control Transform(string text) => TransformElement(text).Control;

    public ColorDocument.Avalonia.DocumentElement TransformElement(string text)
    {
        host.BeginRender();
        using (FenceRendering.By(host))
        {
            var element = inner.TransformElement(text);
            _ = element.Control; // materialise now, while this host is current
            return element;
        }
    }

    public IEnumerable<ColorDocument.Avalonia.DocumentElement> ParseGamutElement(string? text, ParseStatus status)
    {
        using (FenceRendering.By(host))
        {
            return inner.ParseGamutElement(text, status).ToList();
        }
    }

    public IEnumerable<ColorTextBlock.Avalonia.CInline> ParseGamutInline(string? text)
    {
        using (FenceRendering.By(host))
        {
            return inner.ParseGamutInline(text).ToList();
        }
    }
}

/// <summary>The host whose markdown is being transformed on this thread right now. A transform is
/// synchronous, which is what lets one shared plugin set serve every viewer: the plugin asks here.</summary>
internal static class FenceRendering
{
    [ThreadStatic]
    private static IFenceHost? _current;

    public static IFenceHost? Current => _current;

    /// <summary>Makes <paramref name="host"/> current until disposed; nests, so a fence's own render inside
    /// a viewer's render hands fences to the fence, then the viewer again.</summary>
    public static Scope By(IFenceHost host) => new(host);

    public readonly struct Scope : IDisposable
    {
        private readonly IFenceHost? _previous;

        public Scope(IFenceHost host)
        {
            _previous = _current;
            _current = host;
        }

        public void Dispose() => _current = _previous;
    }
}

/// <summary>Agnes' fence handling, registered once into the shared plugin set. It carries no state of
/// its own: the host being rendered supplies the fence (<see cref="IFenceHost.NextFence"/>). A render
/// outside any host — none exists today — gets a plain fence from the match.</summary>
internal sealed class MarkdownFencePlugin : IMdAvPlugin
{
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

    private static Control CreateFence(Match match)
        => FenceRendering.Current?.NextFence(match)
            ?? new MarkdownFenceView(MarkdownFence.Body(match), showSource: false, _ => { });
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
            Plugins = MarkdownEngine.StockPlugins,
        };
        var source = text[parseTextBegin..parseTextEnd];
        return fallback.RunBlockGamut(source, status);
    }
}

internal static class MarkdownEngine
{
    /// <summary>The library's own full plugin set, built once: what a backtick fence's fallback engine
    /// renders with.</summary>
    public static global::Markdown.Avalonia.Full.MdAvPlugins StockPlugins { get; } = new();

    /// <summary>The plugin set every <see cref="MarkdownViewer"/> renders with, built once. Parser overrides
    /// are first-match-wins, so Agnes' fence plugin precedes SyntaxHigh's code-fence override.</summary>
    public static global::Markdown.Avalonia.Full.MdAvPlugins SharedPlugins { get; } = CreatePlugins(new MarkdownFencePlugin());

    public static global::Markdown.Avalonia.Full.MdAvPlugins CreatePlugins(IMdAvPlugin fencePlugin)
    {
        var plugins = new global::Markdown.Avalonia.Full.MdAvPlugins();
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
        AutomationProperties.SetName(
            _toggle,
            _showSource ? "Render Markdown block" : "Show Markdown block source");
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

internal sealed class InlineMarkdown : ContentControl, IFenceHost
{
    private readonly HashSet<int> _sourceFences = [];
    private readonly string[] _originalFences;
    private int _nextOrdinal;

    public InlineMarkdown(string source)
    {
        Styles.Add(new StyleInclude(new Uri("avares://Agnes.App.Desktop/"))
        {
            Source = new Uri("avares://Agnes.App.Desktop/Themes/MarkdownNestedCodeStyles.axaml"),
        });
        _originalFences = MarkdownFence.Pattern.Matches(source).Select(MarkdownFence.Body).ToArray();
        var engine = new ScopedEngine(this, new global::Markdown.Avalonia.Markdown { Plugins = MarkdownEngine.SharedPlugins });

        try
        {
            Content = engine.Transform(source);
        }
        catch
        {
            var fallback = new SelectableTextBlock { Text = source, TextWrapping = TextWrapping.Wrap };
            fallback.Classes.Add("markdownFenceSource");
            Content = fallback;
        }
    }

    void IFenceHost.BeginRender() => _nextOrdinal = 0;

    public Control NextFence(Match match)
    {
        var ordinal = _nextOrdinal++;
        var source = ordinal < _originalFences.Length ? _originalFences[ordinal] : MarkdownFence.Body(match);
        return new MarkdownFenceView(
            source,
            _sourceFences.Contains(ordinal),
            showSource =>
            {
                if (showSource) { _sourceFences.Add(ordinal); }
                else { _sourceFences.Remove(ordinal); }
            });
    }
}
