using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views;

/// <summary>The settings shell: the search box, the category rail, and one page per category from
/// <c>Views/Settings</c>. The pages share this view's styles and its DataContext (the main window
/// view model), so each is just its own markup.</summary>
public partial class SettingsTabView : UserControl
{
    public SettingsTabView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
