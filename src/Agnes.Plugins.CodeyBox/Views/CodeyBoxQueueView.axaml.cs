using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The queue view. Its only code-behind concern is following the agent's output as it streams — a
/// scroll position is view state, so it does not belong in the view model.
/// </summary>
public partial class CodeyBoxQueueView : UserControl
{
    private CodeyBoxQueueViewModel? _bound;

    public CodeyBoxQueueView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Rebind()
    {
        if (_bound is { } previous)
        {
            previous.OutputAppended -= OnOutputAppended;
        }

        _bound = DataContext as CodeyBoxQueueViewModel;
        if (_bound is { } current)
        {
            current.OutputAppended += OnOutputAppended;
            current.Start();
        }
    }

    // Follow the tail, the way a terminal does. Only when already at the bottom, so scrolling back to
    // read something is not yanked away by the next chunk.
    private void OnOutputAppended(string _)
    {
        // Output can arrive before this control is attached — a session selected while the tab is being
        // built streams immediately — and looking a name up outside a name scope throws rather than
        // returning null. Resolved defensively so a chunk arriving early cannot take the pane down.
        ScrollViewer? scroller;
        try
        {
            scroller = this.FindControl<ScrollViewer>("OutputScroller");
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (scroller is null)
        {
            return;
        }

        var atBottom = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 24;
        if (atBottom)
        {
            scroller.ScrollToEnd();
        }
    }
}
