using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Agnes.App.Controls;

public sealed partial class SessionPanel : UserControl
{
    public SessionPanel() => InitializeComponent();

    /// <summary>
    /// Shows or hides the screen pane.
    ///
    /// The column's width is set here rather than left to the pane's own Visibility: a collapsed element
    /// in a star-sized column still reserves half the panel, which would leave the transcript squeezed
    /// beside an empty gap.
    /// </summary>
    private void OnToggleScreen(object sender, RoutedEventArgs e)
    {
        var on = ScreenToggle.IsChecked == true;
        Screen.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ScreenColumn.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Screen.SetActive(on);
    }
}
