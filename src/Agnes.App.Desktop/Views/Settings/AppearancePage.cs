using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views.Settings;

public partial class AppearancePage : UserControl
{
    public AppearancePage() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
