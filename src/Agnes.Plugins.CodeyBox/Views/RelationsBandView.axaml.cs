using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The selected item's place in its chain, its edges, and the four ways to make more work from here.
/// DataContext: the queue view model.
/// </summary>
/// <remarks>
/// Its own control rather than markup inside the pane for a reason that is not tidiness: everything it
/// draws is a function of one <see cref="Relations"/> record, so it can be rendered — and looked at —
/// from a hand-built one, which is the only way the blocked-parent cases (failed, cancelled, still in
/// flight, all three at once) get seen before a live host produces them.
/// </remarks>
public partial class RelationsBandView : UserControl
{
    public RelationsBandView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
