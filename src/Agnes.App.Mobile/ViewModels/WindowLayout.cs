using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>Material's window size classes by width: a phone, a folded phone and a phone in a hand are
/// compact; a small tablet, a fold's inner screen and most landscape phones are medium or expanded.</summary>
public enum WidthClass
{
    /// <summary>Under 600 dp: one column, bottom bar, everything pushed.</summary>
    Compact,

    /// <summary>600 to 839 dp: a rail instead of a bar, still one pane unless there is height for two.</summary>
    Medium,

    /// <summary>840 dp and up: two panes and side sheets.</summary>
    Expanded,
}

/// <summary>
/// What the window is right now — its size in device-independent pixels — and every layout decision
/// the app derives from it, in one place, so a fold, a rotation, a tablet and a phone in landscape are
/// all the same question: how wide, how tall.
/// </summary>
/// <remarks>
/// <para>The activity survives every one of those changes without being recreated (it declares them
/// all in <c>ConfigurationChanges</c>), so this updates in place from the root view's size and the views
/// re-lay themselves; nothing reloads. The numbers are Material's breakpoints with two adjustments a
/// phone forces: two panes need <i>height</i> as well as width (an 891×411 landscape phone is
/// "expanded" by width and has no room for a list beside a transcript), and a rail is worth having in
/// any landscape, because the bar's sixty pixels come out of a height that is already short.</para>
/// <para>Geometries this is tuned against, in dp: phone 411×891; phone landscape 891×411; Pixel 9 Pro
/// Fold inner 890×923; Galaxy Z Fold 6 inner 794×924, outer 378×927; the 10" tablet it is verified on,
/// 640×1072 (its panel is 800×1340 at 200 dpi).</para>
/// </remarks>
public sealed partial class WindowLayout : ObservableObject
{
    public const double MediumFrom = 600;
    public const double ExpandedFrom = 840;

    /// <summary>Two panes need this much width, and enough height to make the second pane worth its width.</summary>
    public const double TwoPaneWidthFrom = 720;
    public const double TwoPaneHeightFrom = 480;

    /// <summary>The list pane beside a detail: wide enough for a session card, no more.</summary>
    public const double PaneWidthNarrow = 340;
    public const double PaneWidthWide = 400;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Height))]
    private double _width = 411;

    [ObservableProperty]
    private double _height = 891;

    /// <summary>Feeds a new size in. Everything below is recomputed and announced once, in one pass.</summary>
    public void Update(double width, double height)
    {
        if (width <= 0 || height <= 0 || (Math.Abs(width - Width) < 0.5 && Math.Abs(height - Height) < 0.5))
        {
            return;
        }

        var before = Snapshot();
        Width = width;
        Height = height;
        var after = Snapshot();
        if (before != after)
        {
            OnPropertyChanged(nameof(WidthClass));
            OnPropertyChanged(nameof(IsLandscape));
            OnPropertyChanged(nameof(UseRail));
            OnPropertyChanged(nameof(TwoPane));
            OnPropertyChanged(nameof(SideSheets));
            OnPropertyChanged(nameof(GridColumns));
            OnPropertyChanged(nameof(PaneWidth));
            OnPropertyChanged(nameof(Describe));
        }
    }

    public WidthClass WidthClass => Width >= ExpandedFrom ? WidthClass.Expanded : Width >= MediumFrom ? WidthClass.Medium : WidthClass.Compact;

    public bool IsLandscape => Width > Height;

    /// <summary>The destinations as a vertical rail on the left rather than a bar along the bottom: any
    /// landscape (the bar's height is the scarce dimension) and any width past compact.</summary>
    public bool UseRail => IsLandscape || WidthClass != WidthClass.Compact;

    /// <summary>A list pane beside a detail pane: the sessions list stays while a session is open, the
    /// fleet's queue stays while an item is open. Needs height as well as width.</summary>
    public bool TwoPane => Width >= TwoPaneWidthFrom && Height >= TwoPaneHeightFrom;

    /// <summary>Sheets arrive from the right edge as a panel beside the content, rather than from the
    /// bottom over it, wherever there is width to spare for one.</summary>
    public bool SideSheets => TwoPane || (IsLandscape && Width >= TwoPaneWidthFrom);

    /// <summary>How many columns a card grid (the fleet's vitals) gets at this width.</summary>
    public int GridColumns => Width >= 1000 ? 4 : Width >= TwoPaneWidthFrom ? 3 : 2;

    /// <summary>The list pane's width in two-pane mode.</summary>
    public double PaneWidth => Width >= 1000 ? PaneWidthWide : PaneWidthNarrow;

    /// <summary>"891×411 · Expanded · landscape · rail · one pane · side sheets" — for tests and the About page.</summary>
    public string Describe =>
        $"{Width:0}×{Height:0} · {WidthClass} · {(IsLandscape ? "landscape" : "portrait")} · {(UseRail ? "rail" : "bar")} · {(TwoPane ? "two panes" : "one pane")} · {(SideSheets ? "side sheets" : "bottom sheets")}";

    private (WidthClass, bool, bool, bool, bool, int, double) Snapshot()
        => (WidthClass, IsLandscape, UseRail, TwoPane, SideSheets, GridColumns, PaneWidth);
}
