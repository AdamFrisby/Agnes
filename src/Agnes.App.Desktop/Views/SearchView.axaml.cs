using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
