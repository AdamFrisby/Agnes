using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views;

public partial class SessionTabView : UserControl
{
    public SessionTabView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
