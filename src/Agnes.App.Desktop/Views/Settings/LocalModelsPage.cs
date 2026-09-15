using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views.Settings;

public partial class LocalModelsPage : UserControl
{
    public LocalModelsPage() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
