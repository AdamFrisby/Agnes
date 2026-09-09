using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>Search, and a ticked list of things this work could wait on. DataContext: the picker.</summary>
public partial class DependencyPickerView : UserControl
{
    public DependencyPickerView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
