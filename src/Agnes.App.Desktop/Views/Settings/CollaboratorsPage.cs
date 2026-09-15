using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views.Settings;

public partial class CollaboratorsPage : UserControl
{
    public CollaboratorsPage() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
