using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        Agnes.App.Mobile.Services.StartupTrace.MarkOnce("tabview.built SearchView");
        AvaloniaXamlLoader.Load(this);
    }
}
