using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// The base every drawn control in the overview sits on: it turns a <em>role</em> — "the hue that means
/// in motion" — into whatever brush the host's current theme has for it.
/// </summary>
/// <remarks>
/// A drawn control cannot use <c>DynamicResource</c>, so it has to do the lookup itself, and doing it
/// naively is how a plugin ends up with hard-coded colours. Three things matter here:
/// <list type="bullet">
/// <item>the lookup walks the tree (<c>TryFindResource</c>), because the roles live in the host app's
/// resources, not the plugin's;</item>
/// <item>a role is asked for as a <em>chain</em> — the status hue first, then the older role that is
/// always present, then the inherited text foreground — so a host that predates the status roles still
/// draws something legible rather than nothing;</item>
/// <item>the cache is dropped whenever the theme variant changes or the control is re-attached, since a
/// brush resolved under Dark is simply wrong under Light.</item>
/// </list>
/// </remarks>
public abstract class ThemedDrawing : Control
{
    /// <summary>Role chains, in the app's own vocabulary. One meaning per hue.</summary>
    protected static class Roles
    {
        /// <summary>In motion: a turn running, an item advancing.</summary>
        public static readonly string[] Sky = ["StatusWorking", "Accent"];

        /// <summary>Blocked on you.</summary>
        public static readonly string[] Amber = ["StatusAttention", "Accent"];

        /// <summary>Done / healthy.</summary>
        public static readonly string[] Mint = ["StatusDone", "Accent"];

        /// <summary>Failed or destructive.</summary>
        public static readonly string[] Pink = ["StatusError", "Danger"];

        /// <summary>Present, but not asking for anything.</summary>
        public static readonly string[] Faint = ["StatusIdle", "FgFaint"];

        public static readonly string[] Dim = ["FgDim"];
        public static readonly string[] Fg = ["Fg"];
        public static readonly string[] Line = ["Line"];
        public static readonly string[] PanelAlt = ["PanelAlt", "Panel"];
    }

    private readonly Dictionary<string, IBrush> _brushes = new(StringComparer.Ordinal);

    protected ThemedDrawing() => ActualThemeVariantChanged += (_, _) => Retheme();

    /// <summary>The brush for a role chain, resolved against the host's theme and cached per variant.</summary>
    protected IBrush Brush(params string[] chain)
    {
        var key = chain[0];
        if (_brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = Resolve(chain);
        _brushes[key] = resolved;
        return resolved;
    }

    private IBrush Resolve(string[] chain)
    {
        foreach (var key in chain)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
            {
                return brush;
            }
        }

        // Last resort: the inherited text colour. Never a literal — a plugin that invents a hex stops
        // matching the moment the user switches theme, which is the whole reason for this class.
        return TextElement.GetForeground(this) ?? Brushes.Transparent;
    }

    /// <summary>A pen in a role's hue.</summary>
    protected IPen Pen(double thickness, params string[] chain) => new Pen(Brush(chain), thickness);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Retheme();
    }

    private void Retheme()
    {
        _brushes.Clear();
        InvalidateVisual();
    }

    /// <summary>Text drawn inside a rendered control: axis labels and the like.</summary>
    protected FormattedText Label(string text, double size, params string[] chain)
        => new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
               Typeface.Default, size, Brush(chain));
}
