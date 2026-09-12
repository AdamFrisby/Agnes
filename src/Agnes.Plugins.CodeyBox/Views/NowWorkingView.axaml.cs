using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The wall. Its DataContext is a <see cref="NowWorkingViewModel"/> and nothing else.
/// </summary>
/// <remarks>
/// No code behind it on purpose. Everything that moves on this screen is a value on the view model,
/// stepped by the view model's own clock, so the whole thing can be rendered — and photographed at two
/// points on the same animation — without a window manager, a frame loop or a real fleet. An Avalonia
/// <c>Animation</c> would have put that behaviour somewhere a test cannot reach.
/// </remarks>
public partial class NowWorkingView : UserControl
{
    public NowWorkingView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
